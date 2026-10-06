using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop;

internal static class PlayerPerformanceChecks
{
    public static Task Run(string[] args)
    {
        string Option(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        var source = Path.GetFullPath(Option("--source", "artifacts/player-large-files/4k-large.avi"));
        var output = Path.GetFullPath(Option("--output", "artifacts/player-performance"));
        var skin = Option("--skin", "Light");
        var seconds = double.Parse(Option("--seconds", "4"), System.Globalization.CultureInfo.InvariantCulture);
        Directory.CreateDirectory(output);
        if (!File.Exists(source)) throw new FileNotFoundException("Generate or supply a real media fixture.", source);
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        Skin.Apply(skin);
        Motion.SetReducedMotion(false);
        var engine = new MediaEngine(new());
        Playback? decoder = null;
        MeasuredAudio? sink = null;
        var failures = new List<string>();
        var samples = new List<double>();
        var process = Process.GetCurrentProcess();
        var peakMemory = process.WorkingSet64;
        var allocated = GC.GetTotalAllocatedBytes();
        var cpu = process.TotalProcessorTime;
        var timer = Stopwatch.StartNew();
        var pendingBeat = 0;
        using var wallClock = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref pendingBeat, 1) != 0) return;
            var postedAt = Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(() =>
            {
                samples.Add(Stopwatch.GetElapsedTime(postedAt).TotalMilliseconds);
                Interlocked.Exchange(ref pendingBeat, 0);
            }, DispatcherPriority.Background);
        }, null, 10, 10);
        var state = new Storage(Path.Combine(output, "state"));
        var window = new PlayerWindow(engine, [source], (_, path) =>
        {
            decoder = new Playback(engine, path, (provider, _) => sink = new MeasuredAudio(provider));
            decoder.Error += failures.Add;
            return decoder;
        }, preferences: state);
        window.Show();
        Complete(window.Ready, 15000);
        var firstFrame = timer.Elapsed.TotalMilliseconds;
        var framesAtStart = decoder!.DecodedFrames;
        var playback = Stopwatch.StartNew();
        while (playback.Elapsed.TotalSeconds < seconds && window.IsPlaying)
        {
            Pump(); Thread.Sleep(1); process.Refresh();
            peakMemory = Math.Max(peakMemory, process.WorkingSet64);
        }
        var playbackFrames = decoder.DecodedFrames - framesAtStart;
        var measuredSeconds = playback.Elapsed.TotalSeconds;
        Complete(window.TogglePlaybackAsync(), 5000);
        var pausedAt = window.SourcePosition;
        var processesAtPause = decoder.StartedProcesses;
        Advance(150);
        if (window.SourcePosition != pausedAt) throw new InvalidOperationException("Paused source time changed.");
        var resume = Stopwatch.StartNew();
        Complete(window.TogglePlaybackAsync(), 5000);
        var beforeResume = decoder.DecodedFrames;
        var beforePosition = window.SourcePosition;
        Wait(() => decoder.DecodedFrames > beforeResume || window.SourcePosition > beforePosition + .01 || !window.IsPlaying, 5000);
        var resumeMs = resume.Elapsed.TotalMilliseconds;
        if (decoder.StartedProcesses != processesAtPause) throw new InvalidOperationException("Resume restarted decoding.");
        var seek = Stopwatch.StartNew();
        Complete(window.SeekAsync(8, true), 15000);
        Complete(decoder.FirstFrame, 15000);
        var seekMs = seek.Elapsed.TotalMilliseconds;
        if (window.SourcePosition < 7.9) throw new InvalidOperationException("Seek did not reach the requested source position.");
        var audioBytes = decoder.DecodedAudioBytes;
        var audioPeak = decoder.PeakAudioBufferBytes;
        var stop = Stopwatch.StartNew();
        window.Close();
        Wait(() => !decoder.HasSession && (sink is null || sink.Disposed), 5000); Advance(100);
        wallClock.Change(Timeout.Infinite, Timeout.Infinite); process.Refresh();
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
        if (audioPeak > 96000) throw new InvalidOperationException("PCM buffer exceeded half a second.");
        var ordered = samples.Order().ToArray();
        double Percentile(double fraction) => ordered.Length == 0 ? 0 : ordered[Math.Min(ordered.Length - 1, (int)Math.Ceiling(ordered.Length * fraction) - 1)];
        var result = new
        {
            mode = "Headless Skia UI, real FFmpeg decoding, measured PCM consumer; OS file cache retained",
            source, sourceBytes = new FileInfo(source).Length, skin, firstFrameMs = firstFrame,
            playbackFrames, measuredSeconds, displayedFps = playbackFrames / measuredSeconds,
            uiHeartbeatP95Ms = Percentile(.95), uiHeartbeatP99Ms = Percentile(.99), uiHeartbeatMaxMs = ordered.LastOrDefault(),
            resumeMs, seekMs, closeMs = stop.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes() - allocated, peakWorkingSetBytes = peakMemory,
            cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, audioBytes, audioPeak,
            frames = decoder.DecodedFrames, processes = decoder.StartedProcesses, failures
        };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "profile.json"), json);
        Console.WriteLine(json);
        return Task.CompletedTask;
    }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Wait(Func<bool> ready, int timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.ElapsedMilliseconds < timeout) { Pump(); Thread.Sleep(1); }
        if (!ready()) throw new TimeoutException("Player operation exceeded its measurement deadline.");
        Pump();
    }
    private static void Complete(Task task, int timeout) { Wait(() => task.IsCompleted, timeout); task.GetAwaiter().GetResult(); }
    private static void Advance(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(1); }
    }
}
