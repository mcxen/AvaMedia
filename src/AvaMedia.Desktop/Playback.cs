using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AvaMedia.Core;
using NAudio.Wave;

namespace AvaMedia.Desktop;

internal sealed class Playback : IPlaybackSession
{
    private readonly IMediaEngine _engine;
    private readonly string _path;
    private readonly Func<IWaveProvider, Action<string>, IAudioOutput> _createAudio;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;
    private bool _disposed, _hasAudio, _muted;
    private float _volume = 1;
    private int _videoStreamIndex, _audioStreamIndex, _startedProcesses;
    private double _frameRate = 25;

    public WriteableBitmap Frame { get; private set; } = new(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
    public event Action<double>? Updated;
    public event Action? Finished;
    public event Action<string>? Error;
    public bool HasSession => _session is not null;
    public bool IsPlaying => _session is { Paused: false };
    public bool IsPaused => _session is { Paused: true };
    public int DecodedFrames { get; private set; }
    public int StartedProcesses => _startedProcesses;
    public long DecodedAudioBytes { get; private set; }
    public int PeakAudioBufferBytes { get; private set; }
    public double Speed { get; set; } = 1;
    public Task FirstFrame => _session?.FirstFrame.Task ?? Task.CompletedTask;
    public bool Muted { get => _muted; set { _muted = value; ApplyVolume(); } }
    public float Volume { get => _volume; set { _volume = Math.Clamp(value, 0, 1); ApplyVolume(); } }

    public Playback(IMediaEngine engine, string path, Func<IWaveProvider, Action<string>, IAudioOutput>? createAudio = null)
    { _engine = engine; _path = path; _createAudio = createAudio ?? AudioOutput.Create; }

    public void SetStreams(int video, int audio) { _videoStreamIndex = video; _audioStreamIndex = audio; }
    public void Configure(MediaInfo info)
    {
        _hasAudio = info.HasAudio;
        _frameRate = info.FrameRate > 0 ? Math.Min(60, info.FrameRate) : 25;
        SetStreams(info.VideoStreamIndex, info.AudioStreamIndex);
        if (info.HasVideo) SetVideoSize(info.Width, info.Height);
    }
    public void SetVideoSize(int width, int height)
    {
        var scale = Math.Min(1, Math.Min(1280d / Math.Max(1, width), 720d / Math.Max(1, height)));
        var size = new PixelSize(Math.Max(2, (int)(width * scale) / 2 * 2), Math.Max(2, (int)(height * scale) / 2 * 2));
        if (Frame.PixelSize == size) return;
        var old = Frame;
        Frame = new(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        old.Dispose();
    }
    // Editor compatibility: audio is decoded on demand, never to a whole-file WAV.
    public Task PrepareAudio(CancellationToken ct) { ct.ThrowIfCancellationRequested(); _hasAudio = true; return Task.CompletedTask; }

    public async Task Play(double seconds, bool video, double end)
    {
        if (!double.IsFinite(seconds) || !double.IsFinite(end) || seconds < 0 || end <= seconds || !double.IsFinite(Speed) || Speed < .25 || Speed > 4)
            throw new ArgumentException("播放区间或速度无效。");
        await _gate.WaitAsync();
        try
        {
            await StopCore();
            if (_disposed) return;
            var executable = _engine.FFmpeg;
            var session = new Session(seconds, end, Speed);
            _session = session;
            var size = Frame.PixelSize;
            var videoIndex = _videoStreamIndex; var audioIndex = _audioStreamIndex;
            session.Worker = Task.Run(() => Run(session, executable, video, size, videoIndex, audioIndex));
        }
        finally { _gate.Release(); }
    }

    public void Pause()
    {
        if (_session is not { } session || session.Paused) return;
        session.Pause(); session.Audio?.Pause();
    }
    public void Resume()
    {
        if (_session is not { } session || !session.Paused) return;
        session.Audio?.Play(); session.Resume();
    }
    private void ApplyVolume() { if (_session?.Audio is { } audio) audio.Volume = Muted ? 0 : Volume; }

    private async Task Run(Session session, string executable, bool video, PixelSize size, int videoIndex, int audioIndex)
    {
        var token = session.Cancellation.Token;
        var audio = _hasAudio ? DecodeAudio(session, executable, audioIndex) : Task.CompletedTask;
        if (!_hasAudio) session.AudioReady.TrySetResult();
        try
        {
            if (video)
            {
                var fps = _frameRate;
                using var process = Start(executable, ["-v", "error", "-nostdin", "-threads", "2", "-ss", MediaEngine.Number(session.Start), "-i", _path,
                    "-map", $"0:v:{videoIndex}", "-an", "-sn", "-filter_threads", "1", "-vf", $"fps={MediaEngine.Number(fps)}:start_time=0,scale={size.Width}:{size.Height}",
                    "-pix_fmt", "bgra", "-t", MediaEngine.Number(session.End - session.Start), "-threads", "1", "-f", "rawvideo", "pipe:1"]);
                using var registration = token.Register(() => Kill(process));
                var errors = process.StandardError.ReadToEndAsync();
                var data = new byte[size.Width * size.Height * 4];
                var index = 0L;
                while (await ReadBlock(process.StandardOutput.BaseStream, data, token) == data.Length)
                {
                    var position = session.Start + index++ / fps;
                    if (position >= session.End) break;
                    if (index == 1)
                        try { await session.AudioReady.Task.WaitAsync(TimeSpan.FromMilliseconds(250), token); } catch (TimeoutException) { }
                    if (index > 1) await WaitPosition(session, position, token);
                    // Drop late frames instead of stretching playback when decoding cannot keep up.
                    if (index > 1 && session.Position - position > .12 * session.Speed) continue;
                    var shown = false;
                    while (!shown)
                    {
                        if (index > 1) await session.WaitRunning(token);
                        shown = await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (!Current(session)) return true;
                            if (session.Paused && index > 1) return false;
                            session.StartClock();
                            using (var buffer = Frame.Lock())
                                for (var row = 0; row < size.Height; row++) Marshal.Copy(data, row * size.Width * 4, buffer.Address + row * buffer.RowBytes, size.Width * 4);
                            DecodedFrames++; Updated?.Invoke(position); session.FirstFrame.TrySetResult();
                            return true;
                        });
                    }
                }
                await process.WaitForExitAsync(token);
                var error = await errors;
                if (process.ExitCode != 0) throw new IOException(error);
                if (!session.Started) throw new InvalidDataException("视频轨没有可播放的画面。");
            }
            else
            {
                await session.AudioReady.Task.WaitAsync(token);
                await session.WaitRunning(token);
                await Dispatcher.UIThread.InvokeAsync(() => { if (Current(session)) { session.StartClock(); session.FirstFrame.TrySetResult(); } });
            }
            // Audio or container duration can extend beyond video EOF: retain the last frame.
            while (session.Position < session.End)
            {
                await session.WaitRunning(token);
                await Task.Delay(20, token);
                await Dispatcher.UIThread.InvokeAsync(() => { if (Current(session) && !session.Paused) Updated?.Invoke(session.Position); });
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!Current(session)) return;
                Updated?.Invoke(session.End); Finished?.Invoke();
            });
        }
        catch (OperationCanceledException) { session.FirstFrame.TrySetCanceled(token); }
        catch (Exception ex)
        {
            session.FirstFrame.TrySetException(ex);
            Dispatcher.UIThread.Post(() => { if (Current(session)) { Error?.Invoke(ex.Message); Finished?.Invoke(); } });
        }
        finally { session.Cancellation.Cancel(); await audio; }
    }

    private async Task DecodeAudio(Session session, string executable, int index)
    {
        var token = session.Cancellation.Token;
        try
        {
            using var process = Start(executable, ["-v", "error", "-nostdin", "-threads", "2", "-ss", MediaEngine.Number(session.Start), "-i", _path,
                "-map", $"0:a:{index}", "-vn", "-sn", "-af", "aresample=async=1:first_pts=0," + Tempo(session.Speed), "-t", MediaEngine.Number((session.End - session.Start) / session.Speed),
                "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1"]);
            using var registration = token.Register(() => Kill(process));
            var errors = process.StandardError.ReadToEndAsync();
            var pcm = new BufferedWaveProvider(new WaveFormat(48000, 16, 2)) { BufferDuration = TimeSpan.FromSeconds(.5), ReadFully = true };
            var bytes = new byte[3840]; // 20 ms PCM; backpressure is independent of source length.
            var discardRemaining = 0;
            while (await ReadBlock(process.StandardOutput.BaseStream, bytes, token) is var count && count > 0)
            {
                var skip = Math.Min(discardRemaining, count); discardRemaining -= skip;
                if (skip == count) continue;
                while (pcm.BufferedBytes > pcm.WaveFormat.AverageBytesPerSecond * .3) await Task.Delay(5, token);
                pcm.AddSamples(bytes, skip, count - skip); DecodedAudioBytes += count;
                PeakAudioBufferBytes = Math.Max(PeakAudioBufferBytes, pcm.BufferedBytes);
                if (session.Audio is null && pcm.BufferedDuration.TotalMilliseconds >= 60) await CreateOutput();
            }
            if (session.Audio is null && pcm.BufferedBytes > 0) await CreateOutput();
            await process.WaitForExitAsync(token);
            var error = await errors;
            if (process.ExitCode != 0) throw new IOException(error);

            async Task CreateOutput()
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!Current(session)) return;
                    // A late audio decoder discards elapsed PCM to rejoin the video clock.
                    var elapsed = (int)(session.Elapsed * pcm.WaveFormat.AverageBytesPerSecond) / pcm.WaveFormat.BlockAlign * pcm.WaveFormat.BlockAlign;
                    var discard = new byte[Math.Min(elapsed, pcm.BufferedBytes)];
                    if (discard.Length > 0) pcm.Read(discard, 0, discard.Length);
                    discardRemaining = elapsed - discard.Length;
                    session.Audio = _createAudio(pcm, message => Dispatcher.UIThread.Post(() => { if (Current(session)) Error?.Invoke(message); }));
                    ApplyVolume();
                    if (session.Started && !session.Paused) session.Audio.Play();
                });
                session.AudioReady.TrySetResult();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Dispatcher.UIThread.Post(() => { if (Current(session)) Error?.Invoke("声音播放不可用：" + ex.Message); }); }
        finally { session.AudioReady.TrySetResult(); }
    }

    private Process Start(string executable, string[] arguments) { Interlocked.Increment(ref _startedProcesses); return ProcessRunner.Start(executable, arguments); }
    private bool Current(Session session) => !_disposed && ReferenceEquals(_session, session) && !session.Cancellation.IsCancellationRequested;
    private static void Kill(Process process) { try { process.Kill(true); } catch (InvalidOperationException) { } }
    private static async Task<int> ReadBlock(Stream stream, byte[] data, CancellationToken token)
    {
        var offset = 0;
        while (offset < data.Length) { var count = await stream.ReadAsync(data.AsMemory(offset), token); if (count == 0) break; offset += count; }
        return offset;
    }
    private static string Tempo(double speed)
    {
        List<string> filters = [];
        while (speed < .5) { filters.Add("atempo=0.5"); speed *= 2; }
        while (speed > 2) { filters.Add("atempo=2"); speed /= 2; }
        filters.Add("atempo=" + MediaEngine.Number(speed)); return string.Join(',', filters);
    }
    private static async Task WaitPosition(Session session, double position, CancellationToken token)
    {
        while (true)
        {
            await session.WaitRunning(token);
            var remaining = (position - session.Position) / session.Speed;
            if (!session.Started || remaining <= 0) return;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(.02, remaining)), token);
        }
    }
    public async Task Stop()
    {
        await _gate.WaitAsync(); try { await StopCore(); } finally { _gate.Release(); }
    }
    private async Task StopCore()
    {
        var session = _session; _session = null;
        if (session is null) return;
        session.Cancellation.Cancel(); session.Audio?.Stop();
        await session.Worker;
        session.Audio?.Dispose(); session.Cancellation.Dispose();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _session?.Cancellation.Cancel(); _ = Release();
        async Task Release() { await Stop(); Frame.Dispose(); }
    }

    private sealed class Session(double start, double end, double speed)
    {
        public double Start { get; } = start;
        public double End { get; } = end;
        public double Speed { get; } = speed;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource FirstFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AudioReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Worker { get; set; } = Task.CompletedTask;
        public IAudioOutput? Audio { get; set; }
        private readonly object _sync = new();
        private readonly Stopwatch _clock = new();
        private TaskCompletionSource _running = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started { get; private set; }
        public bool Paused { get; private set; }
        public double Elapsed { get { lock (_sync) return _clock.Elapsed.TotalSeconds; } }
        public double Position => Math.Min(End, Start + Elapsed * Speed);
        public void StartClock() { lock (_sync) { if (Started) return; Started = true; if (!Paused) { _clock.Start(); Audio?.Play(); } } }
        public void Pause() { lock (_sync) { Paused = true; _clock.Stop(); _running = new(TaskCreationOptions.RunContinuationsAsynchronously); } }
        public void Resume() { lock (_sync) { Paused = false; if (Started) _clock.Start(); _running.TrySetResult(); } }
        public Task WaitRunning(CancellationToken token) { lock (_sync) { token.ThrowIfCancellationRequested(); return Paused ? _running.Task.WaitAsync(token) : Task.CompletedTask; } }
    }
}
