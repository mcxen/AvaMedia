using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop;
using NAudio.Wave;

if (args.Contains("--performance")) { await PlayerPerformanceChecks.Run(args); return; }
if (args.Contains("--details")) { ProjectDetailChecks.Run(); return; }

var root = Path.GetFullPath("artifacts/player-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(root);
var engine = new MediaEngine(new());
var source = Path.Combine(root, "视频 中文.mp4"); var next = Path.Combine(root, "next.mp4");
var longAudio = Path.Combine(root, "long.flac"); var shortClip = Path.Combine(root, "short.mp4"); var tracks = Path.Combine(root, "tracks.mkv");
async Task FF(params string[] args)
{ var result = await ProcessRunner.Run(engine.FFmpeg, args); if (result.ExitCode != 0) throw new Exception(result.Error); }
await FF("-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "12", "-c:v", "mpeg4", "-c:a", "aac", "-y", source);
await FF("-v", "error", "-f", "lavfi", "-i", "color=blue:size=160x90:rate=30", "-t", "2", "-c:v", "mpeg4", "-y", next);
await FF("-v", "error", "-f", "lavfi", "-i", "sine=frequency=660:sample_rate=48000", "-t", "600", "-c:a", "flac", "-y", longAudio);
await FF("-v", "error", "-i", source, "-t", "0.4", "-c", "copy", "-y", shortClip);
await FF("-v", "error", "-i", source, "-i", next, "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-map", "0:v", "-map", "1:v", "-map", "0:a", "-map", "2:a", "-t", "12", "-c:v", "copy", "-c:a", "aac", "-y", tracks);
var info = await engine.Probe(source); var longInfo = await engine.Probe(longAudio);
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
Motion.SetReducedMotion(true);
var checks = new List<string>(); var timings = new List<object>();
void Check(bool value, string message) { if (!value) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS " + message); }
void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
void Wait(Func<bool> value) { var timer = Stopwatch.StartNew(); while (!value() && timer.ElapsedMilliseconds < 15000) { Pump(); Thread.Sleep(2); } if (!value()) throw new Exception("UI wait timed out."); Pump(); }
void Complete(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
void Advance(int ms) { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < ms) { Pump(); Thread.Sleep(2); } }
T Find<T>(Window window, string name) where T : Control => window.FindControl<T>(name)!;
double Metric(Control control, string key) => control.TryFindResource(key, control.ActualThemeVariant, out var value) && value is double metric ? metric : throw new Exception("Missing player metric: " + key);
void Key(Window window, Avalonia.Input.Key key, KeyModifiers modifiers = KeyModifiers.None)
{
    window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = window }); Pump();
    window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = modifiers, Source = window }); Pump();
}
var outputs = new List<MeasuredAudio>();
Playback Create(string path) => new(engine, path, (provider, error) => { var audio = new MeasuredAudio(provider); outputs.Add(audio); return audio; });
var decoder = Create(source); decoder.Configure(info);
var errors = new List<string>(); decoder.Error += errors.Add;
double position = -1; decoder.Updated += time => position = time;
var launch = Stopwatch.StartNew(); Complete(decoder.Play(0, true, info.Duration)); Complete(decoder.FirstFrame);
Check(position == 0 && decoder.DecodedFrames >= 1, "The first displayed frame owns source time zero, without startup clock drift");
timings.Add(new { operation = "first-frame", milliseconds = launch.Elapsed.TotalMilliseconds });
Wait(() => outputs.Count > 0 && outputs[0].NonzeroBytes > 0);
Check(outputs[0].Provider.WaveFormat.SampleRate == 48000 && outputs[0].Provider.WaveFormat.Channels == 2, "Streaming audio produces real nonzero stereo PCM");
Check(decoder.StartedProcesses == 2 && decoder.PeakAudioBufferBytes <= 96000, "A/V uses two decoders with at most half a second of queued PCM");
decoder.Pause(); Advance(30); var paused = position; var frames = decoder.DecodedFrames; var read = outputs[0].ReadBytes;
Advance(200);
Check(decoder.IsPaused && position == paused && decoder.DecodedFrames == frames && outputs[0].ReadBytes == read, "Pause freezes video time and audio consumption");
var resume = Stopwatch.StartNew(); decoder.Resume(); Wait(() => decoder.DecodedFrames > frames);
Check(decoder.StartedProcesses == 2 && resume.ElapsedMilliseconds < 300, "Resume reuses both decoders and publishes promptly");
timings.Add(new { operation = "resume", milliseconds = resume.Elapsed.TotalMilliseconds });
decoder.PresentationVisible = false; Advance(30); var hiddenFrames = decoder.DecodedFrames; var hiddenAudio = outputs[0].ReadBytes;
Advance(200);
Check(decoder.DecodedFrames == hiddenFrames && outputs[0].ReadBytes > hiddenAudio, "Hidden video suppresses pixel transfers while streaming audio continues");
decoder.PresentationVisible = true; Wait(() => decoder.DecodedFrames > hiddenFrames);
Check(decoder.StartedProcesses == 2, "Restoring video resumes presentation without restarting either decoder");
decoder.Volume = .4f; Check(outputs[0].Volume == .4f, "Volume reaches the output device");
decoder.Muted = true; Check(outputs[0].Volume == 0, "Mute keeps decoding while silencing the output");
decoder.Muted = false; Check(outputs[0].Volume == .4f, "Unmute restores the chosen volume");
Complete(decoder.Stop()); var stopped = position; Advance(100);
Check(!decoder.HasSession && position == stopped && outputs[0].Disposed, "Stop joins decoders and releases the device without stale updates");
var doubleSpeed = Create(source); doubleSpeed.Configure(info); doubleSpeed.Speed = 2;
double accelerated = 0; doubleSpeed.Updated += p => accelerated = p;
Complete(doubleSpeed.Play(2, true, 3)); Complete(doubleSpeed.FirstFrame); var speedTimer = Stopwatch.StartNew();
Wait(() => accelerated >= 3); Complete(doubleSpeed.Stop());
Check(speedTimer.Elapsed.TotalSeconds < .9 && accelerated == 3, "2x playback obeys source boundaries and halves wall-clock duration");
var audioDecoder = Create(longAudio); audioDecoder.Configure(longInfo);
var longTimer = Stopwatch.StartNew(); Complete(audioDecoder.Play(0, false, 600)); Complete(audioDecoder.FirstFrame); Advance(100);
Check(longTimer.Elapsed.TotalSeconds < 3 && audioDecoder.DecodedAudioBytes < 384000 && audioDecoder.PeakAudioBufferBytes <= 96000, "Ten-minute audio starts from bounded PCM instead of decoding the whole file");
timings.Add(new { operation = "ten-minute-audio", milliseconds = longTimer.Elapsed.TotalMilliseconds, audioDecoder.DecodedAudioBytes });
Complete(audioDecoder.Stop()); audioDecoder.Dispose();
Check(errors.Count == 0, "The streaming A/V session reports no decoder failures");
var pausedFirst = Create(source); pausedFirst.Configure(info); Complete(pausedFirst.Play(0, true, 1)); pausedFirst.Pause(); Complete(pausedFirst.FirstFrame);
Check(pausedFirst.IsPaused && pausedFirst.DecodedFrames == 1, "Pausing during startup still presents the first frame");
Complete(pausedFirst.Stop()); pausedFirst.Dispose(); decoder.Dispose(); doubleSpeed.Dispose();

foreach (var skin in new[] { "Light", "Dark", "MacOS9" })
{
    Skin.Apply(skin);
    Playback? current = null;
    var window = new PlayerWindow(engine, [source, next], (_, path) => current = Create(path));
    window.Show(); Complete(window.Ready); Complete(window.FirstFrameReady);
    Check(window.IsPlaying && window.SourcePosition < 1 && Find<Image>(window, "VideoImage").Source is not null, skin + ": file opens directly into playback");
    Key(window, Avalonia.Input.Key.Space); Check(!window.IsPlaying && window.IsPaused, skin + ": Space pauses");
    var processes = current!.StartedProcesses; Key(window, Avalonia.Input.Key.Space); Check(window.IsPlaying && current.StartedProcesses == processes, skin + ": Space resumes without a new process");
    Key(window, Avalonia.Input.Key.Space);
    Key(window, Avalonia.Input.Key.Right); Complete(window.CommandReady); Check(window.SourcePosition > 4.9 && !window.IsPlaying, skin + ": Right seeks five seconds and preserves paused intent");
    Key(window, Avalonia.Input.Key.Left); Complete(window.CommandReady); Check(window.SourcePosition < .1, skin + ": Left seeks back and clamps at zero");
    Key(window, Avalonia.Input.Key.F); Complete(window.CommandReady); Check(window.SourcePosition > 0 && window.SourcePosition < .05 && !window.IsPlaying, skin + ": F locates the next real source frame");
    Key(window, Avalonia.Input.Key.D); Complete(window.CommandReady); Check(window.SourcePosition == 0, skin + ": D locates the preceding real source frame");
    Key(window, Avalonia.Input.Key.C); Complete(window.CommandReady); Check(window.PlaybackSpeed == 1.1, skin + ": C raises playback speed");
    Key(window, Avalonia.Input.Key.X); Complete(window.CommandReady); Check(window.PlaybackSpeed == 1, skin + ": X lowers playback speed");
    Complete(window.SetSpeedAsync(1.5)); Key(window, Avalonia.Input.Key.Z); Complete(window.CommandReady); Check(window.PlaybackSpeed == 1, skin + ": Z restores normal speed");
    Key(window, Avalonia.Input.Key.Z); Complete(window.CommandReady); Check(window.PlaybackSpeed == 1.5, skin + ": Z recalls the prior speed");
    Key(window, Avalonia.Input.Key.Down); Check(Find<Slider>(window, "PlayerVolume").Value == 95, skin + ": Down adjusts volume by five points");
    Key(window, Avalonia.Input.Key.M); Check(current.Muted && current.Volume == .95f, skin + ": M preserves the chosen volume");
    Key(window, Avalonia.Input.Key.Enter); Check(window.WindowState == WindowState.FullScreen, skin + ": Enter enters fullscreen");
    Key(window, Avalonia.Input.Key.Escape); Check(window.WindowState != WindowState.FullScreen, skin + ": Escape exits fullscreen");
    Key(window, Avalonia.Input.Key.F1); Check(Find<Border>(window, "ShortcutHelp").IsVisible, skin + ": F1 shows the key reference");
    Key(window, Avalonia.Input.Key.F1);
    Key(window, Avalonia.Input.Key.F6); Check(Find<Border>(window, "PlaylistPanel").IsVisible && Find<ListBox>(window, "PlaylistList").ItemCount >= 2, skin + ": F6 opens the real playlist beside the picture");
    Key(window, Avalonia.Input.Key.F6);
    foreach (var size in new[] { (1100d, 720d, "normal"), (760d, 460d, "minimum") })
    {
        window.Width = size.Item1; window.Height = size.Item2; Pump();
        foreach (var name in new[] { "PlayerPlayButton", "PlayerStopButton", "PlayerMuteButton", "PlayerSpeed", "FullscreenButton" })
        {
            var control = Find<Control>(window, name); var point = control.TranslatePoint(default, window)!.Value;
            Check(point.Y >= 0 && point.Y + control.Bounds.Height <= window.Bounds.Height + 1 && point.X >= 0 && point.X + control.Bounds.Width <= window.Bounds.Width + 1, skin + " " + size.Item3 + ": " + name + " is visible");
        }
        var transportSize = Metric(window, "UiPlayerButtonSize");
        Check(Find<Button>(window, "PlayerPlayButton").Bounds.Size == new Size(transportSize, transportSize) && Find<TextBlock>(window, "PlayerTime").FontSize == Metric(window, "UiTimeFontSize"), skin + " " + size.Item3 + ": transport and time metrics follow shared theme roles");
        var row = Find<Button>(window, "PlayerPlayButton").TranslatePoint(default, window)!.Value.Y;
        Check(Math.Abs(Find<Button>(window, "PlayerMuteButton").TranslatePoint(default, window)!.Value.Y - row) < 1 && Find<Button>(window, "PlayerPlayButton").TranslatePoint(default, window)!.Value.X < Find<TextBlock>(window, "PlayerTime").TranslatePoint(default, window)!.Value.X, skin + " " + size.Item3 + ": PotPlayer layout puts transport left, time next, volume right on one bottom row");
        var timeCenter = Find<TextBlock>(window, "PlayerTime").TranslatePoint(new Point(0, Find<TextBlock>(window, "PlayerTime").Bounds.Height / 2), window)!.Value.Y;
        Check(Math.Abs(timeCenter - row - transportSize / 2) < 1, skin + " " + size.Item3 + ": time text shares the control row center");
        if (args.Contains("--capture")) window.CaptureRenderedFrame()!.Save(Path.Combine(root, skin + "-" + size.Item3 + ".png"));
    }
    var oldSeek = window.SeekAsync(2); var latestSeek = window.SeekAsync(4); Complete(Task.WhenAll(oldSeek, latestSeek));
    Check(window.SourcePosition == 4, skin + ": rapid seek requests keep the most recent position");
    Complete(window.TogglePlaybackAsync());
    var playingSeek = window.SeekAsync(2); Advance(80); var newerPlayingSeek = window.SeekAsync(5); Complete(Task.WhenAll(playingSeek, newerPlayingSeek));
    Check(window.IsPlaying && window.SourcePosition >= 5, skin + ": seeking while a previous decoder stops preserves playback intent");
    var seekBeforePause = window.SeekAsync(7); Complete(window.TogglePlaybackAsync()); Complete(seekBeforePause); Advance(80);
    Check(!window.IsPlaying, skin + ": pause supersedes an in-flight seek instead of restarting later");
    Key(window, Avalonia.Input.Key.PageDown); Complete(window.CommandReady); Check(window.CurrentPath == next && window.IsPlaying && current.Muted, skin + ": next file autoplays and keeps mute");
    Key(window, Avalonia.Input.Key.PageUp); Complete(window.CommandReady); Check(window.CurrentPath == source, skin + ": previous file returns to the playlist entry");
    var closeSeek = window.SeekAsync(6); window.Close(); Complete(closeSeek); Pump();
    Check(!window.IsVisible && !window.IsPlaying, skin + ": closing cancels pending seek and playback");
}
Skin.Apply("Dark"); Playback? trackDecoder = null;
var trackWindow = new PlayerWindow(engine, [tracks], (_, path) => trackDecoder = Create(path)); trackWindow.Show(); Complete(trackWindow.Ready);
var trackMenu = trackWindow.BuildMenu();
var audioMenu = trackMenu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "音频轨");
audioMenu.Items.OfType<MenuItem>().Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); Complete(trackWindow.CommandReady); Advance(180);
Check(outputs.Last().Frequency > 700 && outputs.Last().Frequency < 1000, "The audio menu selects the actual 880 Hz second track");
var videoMenu = trackWindow.BuildMenu().Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "视频轨");
videoMenu.Items.OfType<MenuItem>().Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); Complete(trackWindow.CommandReady);
Check(trackDecoder!.Frame.PixelSize.Width == 160 && trackWindow.IsPlaying, "The video menu switches to the real second stream and its dimensions");
var stretchMenu = trackWindow.BuildMenu().Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "画面比例");
stretchMenu.Items.OfType<MenuItem>().Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
Check(Find<Image>(trackWindow, "VideoImage").Stretch == Avalonia.Media.Stretch.Fill, "The aspect menu changes the actual picture layout");
trackWindow.Close(); Pump();
var playlist = new PlayerWindow(engine, [shortClip, next], (_, path) => Create(path)); playlist.Show(); Complete(playlist.Ready);
Wait(() => playlist.CurrentPath == next && playlist.IsPlaying); Check(playlist.CurrentPath == next, "EOF automatically advances to the next file"); playlist.Close(); Pump();
var bad = new PlayerWindow(engine, [Path.Combine(root, "missing.mp4")]); bad.Show(); Complete(bad.Ready);
Check(bad.PlaybackError.Length > 0 && !bad.IsPlaying && !Find<Button>(bad, "PlayerPlayButton").IsEnabled, "An unreadable file presents an actionable error and leaves play disabled"); bad.Close();
Check(PlayerShortcuts.Resolve(Avalonia.Input.Key.Right, KeyModifiers.Shift) == PlayerCommand.Forward30 && PlayerShortcuts.Resolve(Avalonia.Input.Key.Left, KeyModifiers.Control) == PlayerCommand.Back60, "Modified arrow keys retain PotPlayer jump intervals");
Check(PlayerShortcuts.Resolve(Avalonia.Input.Key.C, KeyModifiers.Control) is null, "Text editing shortcuts are not converted into player commands");
var folder = Path.Combine(root, "folder"); var otherFolder = Path.Combine(root, "other-folder");
Directory.CreateDirectory(folder); Directory.CreateDirectory(otherFolder); Directory.CreateDirectory(Path.Combine(folder, "nested"));
var episode2 = Path.Combine(folder, "Episode 2.MP4"); var episode10 = Path.Combine(folder, "Episode 10.mp4"); var other = Path.Combine(otherFolder, "other.mp4");
File.Copy(source, episode2); File.Copy(next, episode10); File.Copy(source, other);
File.Copy(source, Path.Combine(folder, "nested", "hidden.mp4")); File.Copy(longAudio, Path.Combine(folder, "audio.flac")); File.WriteAllText(Path.Combine(folder, "notes.txt"), "not video");
var scan = new VideoFolderScanner().ScanAsync(folder, CancellationToken.None); Complete(scan);
Check(scan.Result.SequenceEqual(new[] { episode2, episode10 }), "Folder scan includes upper-case video extensions, sorts episode numbers, and excludes audio, documents and subfolders");
var cancelledScan = new VideoFolderScanner().ScanAsync(folder, new CancellationToken(true)); Wait(() => cancelledScan.IsCompleted);
Check(cancelledScan.IsCanceled, "Folder enumeration supports cancellation");
var deferred = new DeferredFolderScanner(); Playback? folderDecoder = null;
var folderWindow = new PlayerWindow(engine, [episode2], (_, path) => folderDecoder = Create(path), deferred); folderWindow.Show(); Complete(folderWindow.Ready);
Wait(() => deferred.Requests.Count == 1);
Check(folderWindow.IsPlaying && !folderWindow.PlaylistReady.IsCompleted && deferred.Requests[0].Directory == folder, "First-frame playback finishes while folder enumeration is still pending in the background");
Complete(folderWindow.TogglePlaybackAsync()); Complete(folderWindow.SeekAsync(2, false)); var priorProcesses = folderDecoder!.StartedProcesses;
deferred.Requests[0].Result.SetResult(new[] { episode2, episode10, episode10 }); Complete(folderWindow.PlaylistReady);
Check(Find<ListBox>(folderWindow, "PlaylistList").ItemCount == 2 && folderWindow.CurrentPath == episode2 && folderWindow.SourcePosition == 2 && folderDecoder.StartedProcesses == priorProcesses && !folderWindow.IsPlaying,
    "Folder results deduplicate entries and preserve the current file, position, pause state and decoder");
Complete(folderWindow.ExecuteAsync(PlayerCommand.NextFile));
Check(folderWindow.CurrentPath == episode10 && folderWindow.IsPlaying && deferred.Requests.Count == 1, "Next-file navigation uses the background folder list without rescanning the same directory");
folderWindow.Close(); Pump();
var staleScanner = new DeferredFolderScanner();
var staleWindow = new PlayerWindow(engine, [episode2], (_, path) => Create(path), staleScanner); staleWindow.Show(); Complete(staleWindow.Ready); Wait(() => staleScanner.Requests.Count == 1);
var abandoned = staleWindow.PlaylistReady; Complete(staleWindow.OpenAsync(other)); Wait(() => staleScanner.Requests.Count == 2);
staleScanner.Requests[0].Result.SetResult(new[] { episode2, episode10 }); Complete(abandoned);
Check(staleScanner.Requests[0].Token.IsCancellationRequested && Find<ListBox>(staleWindow, "PlaylistList").ItemCount == 1 && staleWindow.CurrentPath == other,
    "Switching folders cancels the old enumeration and discards late results");
staleScanner.Requests[1].Result.SetResult(new[] { other }); Complete(staleWindow.PlaylistReady);
Check(Find<ListBox>(staleWindow, "PlaylistList").SelectedIndex == 0 && staleWindow.CurrentPath == other, "Only the current folder result becomes the active playlist");
Complete(staleWindow.OpenAsync(episode2)); Wait(() => staleScanner.Requests.Count == 3); var closingList = staleWindow.PlaylistReady;
staleWindow.Close(); staleScanner.Requests[2].Result.SetResult(new[] { episode2, episode10 }); Complete(closingList);
Check(staleScanner.Requests[2].Token.IsCancellationRequested && !staleWindow.IsVisible, "Closing the player cancels folder work and prevents late UI updates");
File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new { checks = checks.Count, results = checks, timings }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Verified {checks.Count} player checks. {root}");

sealed class DeferredFolderScanner : IVideoFolderScanner
{
    public List<(string Directory, CancellationToken Token, TaskCompletionSource<IReadOnlyList<string>> Result)> Requests { get; } = [];
    public Task<IReadOnlyList<string>> ScanAsync(string directory, CancellationToken token)
    {
        var result = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Requests.Add((directory, token, result)); return result.Task;
    }
}

sealed class MeasuredAudio : IAudioOutput
{
    public IWaveProvider Provider { get; }
    public float Volume { get; set; } = 1;
    public bool Disposed { get; private set; }
    public long ReadBytes, NonzeroBytes;
    private long _crossings, _samples;
    private short _last;
    public double Frequency { get { lock (_sync) return _samples > 0 ? _crossings * 48000d / _samples : 0; } }
    private readonly Timer _timer;
    private readonly object _sync = new();
    private readonly byte[] _buffer = new byte[3840];
    private bool _playing;
    public MeasuredAudio(IWaveProvider provider)
    {
        Provider = provider;
        _timer = new(_ =>
        {
            lock (_sync)
            {
                if (!_playing || Disposed) return;
                var count = provider.Read(_buffer, 0, _buffer.Length); ReadBytes += count; NonzeroBytes += _buffer.Take(count).Count(b => b != 0);
                for (var offset = 0; offset + 3 < count; offset += 4) { var sample = BitConverter.ToInt16(_buffer, offset); if (_last <= 0 && sample > 0) _crossings++; _last = sample; _samples++; }
            }
        }, null, 20, 20);
    }
    public void Play() { lock (_sync) _playing = true; }
    public void Pause() { lock (_sync) _playing = false; }
    public void Stop() { lock (_sync) _playing = false; }
    public void Dispose() { lock (_sync) { _playing = false; Disposed = true; } _timer.Dispose(); }
}
