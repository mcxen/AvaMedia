using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Mcp;
using FrameStrip = AvaMedia.Mcp.FrameStrip;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// No arguments: focused contract tests without a desktop or server.
// Two arguments: explicitly invoked acceptance with native MainWindow, HTTP MCP client and real MediaEngine.
// Every mutation is confined to fixture copies and a separate queue/settings directory.
internal static class Program
{
    private static readonly CancellationTokenSource Lifetime = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly List<object> Checks = [];
    private static string Root = "";
    private static MainWindow? Window;
    private static McpClient? Client;
    private static int Calls, Failures;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0) return RunContracts().GetAwaiter().GetResult();
        if (args.Length != 2) { Console.Error.WriteLine("Usage: AvaMedia.McpTests <new-output-directory> <portrait-image>"); return 2; }
        Root = Path.GetFullPath(args[0]);
        if (Directory.Exists(Root)) throw new ArgumentException("Acceptance output directory must be new.");
        Directory.CreateDirectory(Root);
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().SetupWithoutStarting();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await Run(Path.GetFullPath(args[1])); }
            catch (Exception error) { Failures++; Console.Error.WriteLine(error); await File.WriteAllTextAsync(Path.Combine(Root, "fatal.txt"), error.ToString()); }
            finally
            {
                if (Client is not null) await Client.DisposeAsync();
                if (Window is not null) { Window.Close(); await WaitUntil(() => !Window.IsVisible, TimeSpan.FromSeconds(20)); }
                await File.WriteAllTextAsync(Path.Combine(Root, "report.json"), JsonSerializer.Serialize(new
                { nativeDesktop = true, realEngine = true, protocol = McpTools.ProtocolVersion, platform = RuntimeInformation.OSDescription,
                    version = typeof(MainWindow).Assembly.GetName().Version?.ToString(), completedUtc = DateTimeOffset.UtcNow,
                    calls = Calls, failures = Failures, checks = Checks }, Json));
                Environment.ExitCode = Failures == 0 ? 0 : 1; Lifetime.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(Lifetime.Token);
        return Environment.ExitCode;
    }

    private static async Task<int> RunContracts()
    {
        await Case("task-counts-all-states-and-empty", () =>
        {
            var states = Enum.GetValues<JobState>();
            var counts = McpTaskCounts.From(states.Concat([JobState.Running, JobState.Running]));
            Require(counts.Count == states.Length + 1 && counts["All"] == states.Length + 2, "Missing state keys or wrong total.");
            foreach (var state in states) Require(counts[state.ToString()] == (state == JobState.Running ? 3 : 1), "Wrong count for " + state);
            var sparse = McpTaskCounts.From([JobState.Waiting, JobState.Waiting]);
            Require(sparse["All"] == 2 && states.All(state => sparse[state.ToString()] == (state == JobState.Waiting ? 2 : 0)), "Absent states must have zero counts.");
            var empty = McpTaskCounts.From([]);
            Require(empty.Count == states.Length + 1 && empty.Values.All(value => value == 0), "Empty queue lost zero-valued counts.");
            return Task.CompletedTask;
        });
        await Case("task-page-carries-unfiltered-counts", async () =>
        {
            var counts = McpTaskCounts.From([JobState.Waiting, JobState.Running, JobState.Running, JobState.Completed]);
            var task = new McpTaskView(Guid.NewGuid(), "audio-mp3", "Test", "Running", 0, "", "", [], "", "MCP", "");
            var expected = new McpTaskPage([task], 2, 1, null) { Counts = counts };
            var workspace = new PageWorkspace(expected);
            using var cancellation = new CancellationTokenSource();
            var page = await new McpTools(workspace, new McpFiles()).ListTasks(1, 1, "Running", cancellation.Token);
            Require(workspace.Request == (1, 1, "Running", cancellation.Token), "ListTasks lost pagination, state or cancellation.");
            Require(ReferenceEquals(page, expected) && ReferenceEquals(page.Counts, counts), "ListTasks discarded the workspace page or counts.");
            var payload = JsonSerializer.SerializeToElement(page, Json);
            Require(payload.GetProperty("tasks").GetArrayLength() == 1 && payload.GetProperty("total").GetInt32() == 2, "Wrong filtered page.");
            var serializedCounts = payload.GetProperty("counts");
            foreach (var pair in counts) Require(serializedCounts.GetProperty(pair.Key).GetInt32() == pair.Value, "Task counts were lost during serialization.");
            Require(serializedCounts.GetProperty("All").GetInt32() == 4, "Counts must describe the whole queue, not the page.");
        });
        await Case("frame-strip-count-clamping", () =>
        {
            foreach (var (requested, expected) in new[] { (int.MinValue, 1), (-1, 1), (0, 1), (1, 1), (12, 12), (24, 24), (25, 24), (int.MaxValue, 24) })
                Require(FrameStrip.Times(10, requested).Length == expected, "Wrong clamped count for " + requested);
            return Task.CompletedTask;
        });
        await Case("frame-strip-dimensions-and-increasing-times", () =>
        {
            Require(FrameStrip.Width == 160 && FrameStrip.Height == 90, "Unexpected filmstrip dimensions.");
            foreach (var duration in new[] { .001, .01, .05, .1, 1, 10, 123.456 })
            foreach (var count in new[] { 1, 2, 12, 24 })
            {
                var times = FrameStrip.Times(duration, count);
                Require(times.Length == count, "Wrong sample count.");
                Require(times.All(time => double.IsFinite(time) && time >= 0 && time < duration), "Sample outside duration " + duration);
                Require(times.Zip(times.Skip(1), (left, right) => left < right).All(increasing => increasing), "Repeated or reversed sample times for duration " + duration);
            }
            Require(FrameStrip.Times(8, 4).SequenceEqual(new[] { 1d, 3d, 5d, 7d }), "Samples must be at evenly spaced cell centres.");
            return Task.CompletedTask;
        });
        await Case("frame-strip-invalid-duration", () =>
        {
            foreach (var duration in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Require(FrameStrip.Times(duration, 10).SequenceEqual(new[] { 0d }) &&
                    AvaMedia.Core.FrameStrip.Times(duration, 10).SequenceEqual(new[] { 0d }), "Invalid duration must sample zero.");
            return Task.CompletedTask;
        });
        await StripCase("frame-strip-cache-hit", async path =>
        {
            var preview = new StripPreview();
            var cache = new FrameStripCache(preview);
            var first = await cache.GetAsync(path, 8, 4);
            var second = await cache.GetAsync(path, 8, 4);
            Require(preview.Calls == 4 && ReferenceEquals(first, second), "Cache hit re-extracted frames.");
            Require(first.Select(frame => frame.Seconds).SequenceEqual(new[] { 1d, 3d, 5d, 7d }) &&
                first.Select(frame => frame.Image[0]).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Frame data or times changed.");
            Require(preview.Requests.All(request => request == (Path.GetFullPath(path), 160, 90, false, 0)), "Wrong preview arguments.");
        });
        await StripCase("frame-strip-cache-mtime-invalidation", async path =>
        {
            var preview = new StripPreview();
            var cache = new FrameStripCache(preview);
            await cache.GetAsync(path, 8, 2);
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(10));
            await cache.GetAsync(path, 8, 2);
            Require(preview.Calls == 4, "Changed source mtime reused stale frames.");
        });
        await StripCase("frame-strip-cache-key-options-and-length", async path =>
        {
            var preview = new StripPreview();
            var cache = new FrameStripCache(preview);
            await cache.GetAsync(path, 8, 1);
            await cache.GetAsync(path, 8, 2);
            await cache.GetAsync(path, 8, 1, width: 320);
            await cache.GetAsync(path, 8, 1, height: 180);
            await cache.GetAsync(path, 8, 1, videoStreamIndex: 2);
            Require(preview.Calls == 6, "Count, dimensions or stream did not distinguish cache entries.");
            var modified = File.GetLastWriteTimeUtc(path);
            await File.AppendAllTextAsync(path, "changed length");
            File.SetLastWriteTimeUtc(path, modified);
            await cache.GetAsync(path, 8, 1);
            Require(preview.Calls == 7, "Changed source length reused stale frames.");
        });
        await StripCase("frame-strip-cache-lru", async path =>
        {
            var preview = new StripPreview();
            var cache = new FrameStripCache(preview, capacity: 2);
            await cache.GetAsync(path, 8, 1, videoStreamIndex: 0);
            await cache.GetAsync(path, 8, 1, videoStreamIndex: 1);
            await cache.GetAsync(path, 8, 1, videoStreamIndex: 0);
            await cache.GetAsync(path, 8, 1, videoStreamIndex: 2);
            await cache.GetAsync(path, 8, 1, videoStreamIndex: 0);
            Require(preview.Calls == 3, "Recently used strip was evicted.");
            await cache.GetAsync(path, 8, 1, videoStreamIndex: 1);
            Require(preview.Calls == 4, "Least recently used strip was not evicted.");
        });
        await StripCase("frame-strip-cache-concurrent-dedupe", async path =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preview = new StripPreview((_, ct) => release.Task.WaitAsync(ct));
            var cache = new FrameStripCache(preview);
            var first = cache.GetAsync(path, 8, 4);
            var second = cache.GetAsync(path, 8, 4);
            try { Require(preview.Calls == 1 && !first.IsCompleted && !second.IsCompleted, "Concurrent request started another extraction."); }
            finally { release.TrySetResult(); }
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Require(preview.Calls == 4 && ReferenceEquals(results[0], results[1]), "Concurrent callers did not share their extraction.");
        });
        await StripCase("frame-strip-cache-progress", async path =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preview = new StripPreview((call, ct) => call == 2 ? release.Task.WaitAsync(ct) : Task.CompletedTask);
            var cache = new FrameStripCache(preview);
            var reports = new List<FrameStripFrame>();
            var lateReports = new List<FrameStripFrame>();
            var first = cache.GetAsync(path, 8, 4, progress: new StripProgress(frame =>
            { reports.Add(frame); if (frame.Index == 0) firstReported.TrySetResult(); }));
            Task<IReadOnlyList<FrameStripFrame>>? late = null;
            try
            {
                await firstReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!first.IsCompleted && reports.Count == 1, "Progress waited for the entire strip.");
                late = cache.GetAsync(path, 8, 4, progress: new StripProgress(frame =>
                { lateReports.Add(frame); if (frame.Index == 0) lateReported.TrySetResult(); }));
                await lateReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!late.IsCompleted, "Late caller did not join in-flight extraction.");
            }
            finally { release.TrySetResult(); }
            var frames = await first.WaitAsync(TimeSpan.FromSeconds(5));
            await late!.WaitAsync(TimeSpan.FromSeconds(5));
            Require(reports.SequenceEqual(frames) && lateReports.SequenceEqual(frames), "Progress missed, duplicated or reordered frames.");
            var hits = new List<FrameStripFrame>();
            var hit = cache.GetAsync(path, 8, 4, progress: new StripProgress(hits.Add));
            Require(hit.IsCompletedSuccessfully && hits.SequenceEqual(frames) && preview.Calls == 4, "Cache hit must report all frames immediately.");
        });
        await StripCase("frame-strip-cache-caller-cancellation", async path =>
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preview = new StripPreview((_, ct) => release.Task.WaitAsync(ct));
            var cache = new FrameStripCache(preview);
            using var cancellation = new CancellationTokenSource();
            var first = cache.GetAsync(path, 8, 4, ct: cancellation.Token);
            var second = cache.GetAsync(path, 8, 4);
            try
            {
                cancellation.Cancel();
                try { await first.WaitAsync(TimeSpan.FromSeconds(5)); Require(false, "Cancelled caller completed successfully."); }
                catch (OperationCanceledException) { }
                Require(!second.IsCompleted && preview.Calls == 1, "Cancelling one caller affected the shared extraction.");
            }
            finally { release.TrySetResult(); }
            var frames = await second.WaitAsync(TimeSpan.FromSeconds(5));
            var hit = await cache.GetAsync(path, 8, 4);
            Require(frames.Count == 4 && preview.Calls == 4 && ReferenceEquals(frames, hit), "Surviving caller or completed cache was lost.");
        });
        await StripCase("frame-strip-cache-failure-not-cached", async path =>
        {
            foreach (var failure in new Exception[] { new IOException("Expected failure"), new OperationCanceledException() })
            {
                var preview = new StripPreview((call, _) => call == 2 ? Task.FromException(failure) : Task.CompletedTask);
                var cache = new FrameStripCache(preview);
                try { await cache.GetAsync(path, 8, 3); Require(false, "Extraction failure was swallowed."); }
                catch (Exception error) when (error.GetType() == failure.GetType()) { }
                var retry = await cache.GetAsync(path, 8, 3);
                await cache.GetAsync(path, 8, 3);
                Require(preview.Calls == 5 && retry.Count == 3 && retry[0].Image[0] == 3, "Failed or cancelled partial extraction was cached.");
            }
        });
        await Case("delogo-legacy-graph", () =>
        {
            var options = new ConversionOptions { DelogoX = 10, DelogoY = 20, DelogoWidth = 30, DelogoHeight = 40 };
            Require(DelogoFilter.Regions(options).SequenceEqual(new[] { new DelogoRegion(10, 20, 30, 40) }), "Legacy region changed.");
            // Literal graph from the legacy MediaFilters implementation, including prefixed labels.
            foreach (var prefix in new[] { "", "clip" })
            {
                var expected = $"split[{prefix}original][{prefix}patch];[{prefix}patch]" +
                    "pad=iw+4:ih+4:2:2,fillborders=left=2:right=2:top=2:bottom=2:mode=smear," +
                    "delogo=x=12:y=22:w=30:h=40,crop=30:40:12:22:exact=1" +
                    $"[{prefix}repaired];[{prefix}original][{prefix}repaired]overlay=10:20";
                Require(DelogoFilter.Build(options, prefix).SequenceEqual(new[] { expected }), "Legacy delogo graph changed.");
            }
            return Task.CompletedTask;
        });
        await Case("delogo-extra-timed-regions", () =>
        {
            var options = new ConversionOptions { Start = 2, DelogoX = 10, DelogoY = 20, DelogoWidth = 30, DelogoHeight = 40,
                DelogoRegions = [new(50, 60, 20, 10, 2.5, 4.5), new(80, 90, 10, 20, 5.25, 8.75)] };
            DelogoFilter.Validate(options);
            var regions = DelogoFilter.Regions(options);
            Require(regions.Length == 3 && regions[0] == new DelogoRegion(10, 20, 30, 40) && regions.Skip(1).SequenceEqual(options.DelogoRegions), "Legacy and extra regions must retain order.");
            var filters = DelogoFilter.Build(options);
            Require(filters.Count == 3 && !filters[0].Contains("enable="), "Legacy region unexpectedly has a time limit.");
            Require(filters[1].Contains("[d1original][d1repaired]overlay=50:60:enable=between(t\\,0.5\\,2.5)"), "First timed region lost its graph labels or source-time offset.");
            Require(filters[2].Contains("[d2original][d2repaired]overlay=80:90:enable=between(t\\,3.25\\,6.75)"), "Second timed region lost its graph labels or source-time offset.");
            return Task.CompletedTask;
        });
        await Case("delogo-region-count-and-range-validation", async () =>
        {
            var options = new ConversionOptions { DelogoWidth = 10, DelogoHeight = 10,
                DelogoRegions = Enumerable.Repeat(new DelogoRegion(20, 20, 10, 10, 1, 2), 15).ToArray() };
            DelogoFilter.Validate(options);
            Require(DelogoFilter.Regions(options).Length == 16, "Sixteen regions should be accepted.");
            options.DelogoRegions = Enumerable.Repeat(new DelogoRegion(20, 20, 10, 10, 1, 2), 16).ToArray();
            await RejectArgument(() => { DelogoFilter.Validate(options); return Task.CompletedTask; }, "Seventeen regions were accepted.");
            foreach (var end in new[] { 1d, 2d })
            {
                options.DelogoRegions = [new(20, 20, 10, 10, 2, end)];
                await RejectArgument(() => { DelogoFilter.Validate(options); return Task.CompletedTask; }, "End <= Start was accepted.");
            }
            // End == 0 is the documented open-ended sentinel, not an invalid range.
            options.DelogoRegions = [new(20, 20, 10, 10, 2, 0)];
            DelogoFilter.Validate(options);
        });
        await Case("client-log-aggregation-order-and-limit", async () =>
        {
            // Keep the production type internal; exercise its public methods without starting a listener.
            var type = typeof(McpService).Assembly.GetType("AvaMedia.Mcp.McpClientLog", throwOnError: true)!;
            var log = Activator.CreateInstance(type, nonPublic: true)!;
            void Note(string address, string agent, bool mcp = true) => type.GetMethod("Note")!.Invoke(log, [IPAddress.Parse(address), agent, mcp]);
            McpClientView[] Snapshot() => (McpClientView[])type.GetMethod("Snapshot")!.Invoke(log, null)!;
            Require(Snapshot().Length == 0, "New client log must be empty.");
            Note("127.0.0.1", "agent-a");
            var first = Snapshot().Single();
            await Task.Delay(2);
            Note("127.0.0.1", "agent-b");
            Note("127.0.0.2", "agent-a");
            Note("127.0.0.1", "agent-a", false);
            await Task.Delay(2);
            Note("::ffff:127.0.0.1", "agent-a");
            var snapshot = Snapshot();
            Require(snapshot.Length == 4 && snapshot[0].Address == "127.0.0.1" && snapshot[0].Agent == "agent-a" && snapshot[0].Kind == "MCP", "Most recently seen peer must be first; address, agent and kind must distinguish peers.");
            Require(snapshot[0].Requests == 2 && snapshot.Skip(1).All(peer => peer.Requests == 1), "Requests were not aggregated per peer.");
            Require(snapshot[0].FirstSeenUtc == first.FirstSeenUtc && snapshot[0].LastSeenUtc > first.LastSeenUtc, "Repeated requests must retain first seen and advance last seen.");
            for (var index = 0; index < 21; index++)
            {
                await Task.Delay(2);
                Note("127.0.0.3", "peer-" + index);
                Require(Snapshot().Length <= 20, "Client log exceeded its limit.");
            }
            snapshot = Snapshot();
            Require(snapshot.Length == 20 && snapshot.Select(peer => peer.Agent).SequenceEqual(Enumerable.Range(1, 20).Reverse().Select(index => "peer-" + index)), "Log must evict oldest peers and return newest first.");
            Require(snapshot.Zip(snapshot.Skip(1), (left, right) => left.LastSeenUtc >= right.LastSeenUtc).All(ordered => ordered), "Client timestamps are out of order.");
        });
        await Case("segment-boundary-read", async () =>
        {
            var preview = new PreviewStub();
            using var cancellation = new CancellationTokenSource();
            var frames = await SegmentBoundaryFrames.ReadAsync(preview, "clip.mp4", 1.25, 2.75, 160, 90, 3, cancellation.Token);
            Require(frames.Length == 2 && frames[0].Edge == "start" && frames[0].Seconds == 1.25 && frames[1].Edge == "end" && frames[1].Seconds == 2.75, "Wrong boundary labels or source times.");
            Require(frames[0].Image.SequenceEqual(new byte[] { 1 }) && frames[1].Image.SequenceEqual(new byte[] { 2 }), "Boundary image bytes were lost or swapped.");
            Require(preview.Thumbnails.SequenceEqual(new[] {
                ("clip.mp4", 1.25, 160, 90, cancellation.Token, false, 3, false),
                ("clip.mp4", 2.75, 160, 90, cancellation.Token, false, 3, true) }), "Boundary preview must forward size, stream and cancellation, with an exclusive end and no padding.");
        });
        await Case("segment-boundary-nudge", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            foreach (var (frames, times) in new[] { (2, new[] { 1.08, 1.21 }), (-2, new[] { .93, .81 }) })
            {
                var preview = new PreviewStub(times);
                var result = await SegmentBoundaryFrames.NudgeAsync(preview, "clip.mp4", 1, frames, 3, cancellation.Token);
                Require(result == times[1], "Nudge must return the decoded frame timestamp.");
                Require(preview.Steps.SequenceEqual(new[] {
                    ("clip.mp4", 1d, Math.Sign(frames), cancellation.Token, 3),
                    ("clip.mp4", times[0], Math.Sign(frames), cancellation.Token, 3) }), "Nudge must chain real timestamps and forward direction, stream and cancellation.");
            }
            foreach (var count in new[] { -30, -1, 1, 30 })
            {
                var times = Enumerable.Range(1, Math.Abs(count)).Select(index => 2 + Math.Sign(count) * index * .04).ToArray();
                var preview = new PreviewStub(times);
                Require(await SegmentBoundaryFrames.NudgeAsync(preview, "clip.mp4", 2, count) == times[^1] && preview.Steps.Count == Math.Abs(count), "Valid nudge limits were not honored.");
            }
        });
        await Case("segment-boundary-invalid-inputs", async () =>
        {
            var preview = new PreviewStub();
            foreach (var (start, end) in new[] { (-1d, 2d), (1d, 1d), (2d, 1d), (double.NaN, 2d), (0d, double.PositiveInfinity) })
                await RejectArgument(() => SegmentBoundaryFrames.ReadAsync(preview, "clip.mp4", start, end, 160, 90), "Invalid segment range was accepted.");
            foreach (var count in new[] { -31, 0, 31 })
                await RejectArgument(() => SegmentBoundaryFrames.NudgeAsync(preview, "clip.mp4", 1, count), "Invalid nudge count was accepted.");
            Require(preview.Thumbnails.Count == 0 && preview.Steps.Count == 0, "Invalid arguments reached media preview.");
        });
        Console.WriteLine($"{Checks.Count - Failures} passed, {Failures} failed.");
        return Failures == 0 ? 0 : 1;
    }

    private static async Task RejectArgument(Func<Task> action, string message)
    {
        try { await action(); }
        catch (ArgumentException) { return; }
        Require(false, message);
    }

    private sealed class PageWorkspace(McpTaskPage page) : IMcpWorkspace
    {
        public (int Offset, int Limit, string? State, CancellationToken Cancellation) Request;
        public Task<McpTaskPage> ListAsync(int offset, int limit, string? state, CancellationToken ct)
        { Request = (offset, limit, state, ct); return Task.FromResult(page); }
        public IMediaEngine Engine => throw new NotSupportedException();
        public Task<McpTaskResult> GetAsync(Guid id, bool includeResult, int offset, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<McpSubmission?> FindSubmissionAsync(string requestId, string hash, CancellationToken ct) => throw new NotSupportedException();
        public Task<McpSubmission> SubmitAsync(string requestId, string hash, Func<string[], Job[]> create, bool start, CancellationToken ct) => throw new NotSupportedException();
        public Task<McpTaskView> ControlAsync(Guid id, string action, CancellationToken ct) => throw new NotSupportedException();
        public Task<McpRuntimeView> RuntimeAsync(McpResourceUpdate? update, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> LogAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<FolderClassifiedFile[]> ClassificationAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    private static Task StripCase(string name, Func<string, Task> action) => Case(name, async () =>
    {
        var path = Path.GetTempFileName();
        try { await action(path); }
        finally { File.Delete(path); }
    });

    private sealed class StripProgress(Action<FrameStripFrame> report) : IProgress<FrameStripFrame>
    {
        public void Report(FrameStripFrame value) => report(value);
    }

    private sealed class StripPreview(Func<int, CancellationToken, Task>? beforeFrame = null) : IMediaPreview
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public readonly List<(string Path, int Width, int Height, bool Pad, int Stream)> Requests = [];

        public async Task<byte[]> Thumbnail(string input, double seconds, int width = 640, int height = 360,
            CancellationToken ct = default, bool pad = true, int videoStreamIndex = 0, bool endExclusive = false)
        {
            var call = Interlocked.Increment(ref _calls);
            Requests.Add((input, width, height, pad, videoStreamIndex));
            if (beforeFrame is not null) await beforeFrame(call, ct);
            ct.ThrowIfCancellationRequested();
            return [(byte)call];
        }

        public Task<double> AdjacentFrameTime(string input, double seconds, int direction,
            CancellationToken ct = default, int videoStreamIndex = 0) => throw new NotSupportedException();
    }

    private sealed class PreviewStub(params double[] times) : IMediaPreview
    {
        public readonly List<(string Path, double Seconds, int Width, int Height, CancellationToken Cancellation, bool Pad, int Stream, bool EndExclusive)> Thumbnails = [];
        public readonly List<(string Path, double Seconds, int Direction, CancellationToken Cancellation, int Stream)> Steps = [];
        public Task<byte[]> Thumbnail(string input, double seconds, int width = 640, int height = 360,
            CancellationToken ct = default, bool pad = true, int videoStreamIndex = 0, bool endExclusive = false)
        {
            Thumbnails.Add((input, seconds, width, height, ct, pad, videoStreamIndex, endExclusive));
            return Task.FromResult(new byte[] { endExclusive ? (byte)2 : (byte)1 });
        }
        public Task<double> AdjacentFrameTime(string input, double seconds, int direction, CancellationToken ct = default, int videoStreamIndex = 0)
        {
            Steps.Add((input, seconds, direction, ct, videoStreamIndex));
            return Task.FromResult(times[Steps.Count - 1]);
        }
    }

    private static async Task Run(string portrait)
    {
        var inputs = Path.Combine(Root, "inputs"); Directory.CreateDirectory(inputs);
        var image = Path.Combine(inputs, "人物 O'Brien.jpg"); File.Copy(portrait, image);
        var video = Path.Combine(inputs, "人物 视频.mp4");
        var ffmpeg = MediaEngine.Resolve("", "ffmpeg"); var ffprobe = MediaEngine.Resolve("", "ffprobe");
        await Process(ffmpeg, "-hide_banner", "-loglevel", "error", "-loop", "1", "-i", image,
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "2", "-vf", "scale=320:-2", "-r", "12",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video);
        var originals = new[] { image, video }.ToDictionary(path => path, Hash);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var settings = new AppSettings { Mcp = new() { Enabled = true, Port = port }, OutputFolder = Path.Combine(Root, "output"),
            FFmpegPath = ffmpeg, FFprobePath = ffprobe, CloseToTray = false, MinimizeToTray = false,
            NotifyComplete = false, AutoDetectGpu = false, CheckForUpdates = false, AutoUpdate = false, SystemContextMenu = false,
            AutoDownloadRepairModel = false, EnableBetaFeatures = true, ReduceMotion = true };
        var storage = new Storage(Path.Combine(Root, "state")); storage.SaveSettings(settings);
        Window = new MainWindow(storage) { Width = 1250, Height = 850 }; Window.Show();
        var service = Service();
        await WaitUntil(() => service.Endpoint is not null || service.Status.StartsWith("启动失败"), TimeSpan.FromSeconds(30));
        Require(service.Endpoint is not null, service.Status);
        Client = await Connect(service.Endpoint!);
        await Case("latest-protocol-and-tools", async () =>
        {
            Require(Client.NegotiatedProtocolVersion == McpTools.ProtocolVersion, "Unexpected negotiated protocol.");
            var tools = await Client.ListToolsAsync();
            Require(tools.Count == 23 && tools.Select(tool => tool.Name).Distinct().Count() == 23, "Expected all 23 tools.");
            Require(tools.All(tool => !tool.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("context", out _)), "Request context leaked into tool schema.");
            await File.WriteAllTextAsync(Path.Combine(Root, "tools.json"), JsonSerializer.Serialize(tools.Select(tool => tool.ProtocolTool), Json));
            var capabilities = await Call("avamedia_capabilities"); Require(capabilities.GetProperty("features").GetArrayLength() > 30, "Missing services.");
        });
        await Case("directory-pagination-and-empty-folder", async () =>
        {
            var page = await Call("avamedia_list_files", new { folder = inputs, limit = 1 });
            Require(page.GetProperty("total").GetInt32() == 2 && page.GetProperty("nextOffset").GetInt32() == 1, "File paging failed.");
            var empty = Path.Combine(Root, "empty"); Directory.CreateDirectory(empty);
            page = await Call("avamedia_list_files", new { folder = empty }); Require(page.GetProperty("total").GetInt32() == 0, "Empty listing failed.");
        });
        await Case("media-probe", async () => { var info = await Call("avamedia_probe", new { path = video }); Require(info.GetProperty("duration").GetDouble() >= 1.9, "Wrong duration."); });
        await Case("actionable-input-errors", async () =>
        {
            var error = await Error("avamedia_create_task", new { requestId = "invalid", featureId = "audio-mp3", paths = new[] { video } });
            Require(error.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("UUID"), "UUID error was hidden.");
            error = await Error("avamedia_probe", new { path = "relative.mp4" });
            Require(error.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("绝对"), "Path error was hidden.");
            await Error("avamedia_list_tasks", new { state = "invalid" });
            await Error("avamedia_list_files", new { folder = inputs, limit = 0 });
        });
        await Case("queue-controls-ui-persistence-and-conversion", async () =>
        {
            var arguments = new { requestId = Guid.NewGuid(), featureId = "audio-mp3", paths = new[] { video }, startImmediately = false };
            var submission = await Call("avamedia_create_task", arguments); var id = Id(submission);
            Require(Id(await Call("avamedia_create_task", arguments)) == id, "Duplicate submission created new work.");
            await Error("avamedia_create_task", new { arguments.requestId, arguments.featureId, arguments.paths, startImmediately = true });
            Require(storage.LoadJobs().Single(job => job.Id == id).SubmittedBy == "MCP", "Accepted task was not persisted.");
            Require(Window.FindControl<ListBox>("JobList")!.Items.Cast<Job>().Any(job => job.Id == id), "Task missing from native queue.");
            await WaitUntil(() => Window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "MCP"), TimeSpan.FromSeconds(10));
            Require((await Control(id, "pause")).GetProperty("state").GetString() == "Paused", "Pause failed.");
            await Control(id, "resume"); var completed = await Completed(id);
            var output = completed.GetProperty("task").GetProperty("output").GetString()!;
            Require(Path.GetExtension(output) == ".mp3" && File.Exists(output), "Missing MP3 output.");
            var probe = await Call("avamedia_probe", new { path = output }); Require(probe.GetProperty("hasAudio").GetBoolean(), "Output has no audio.");
            await Process(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", output, "-f", "null", "-");
            var stopped = Id(await Call("avamedia_create_task", new { requestId = Guid.NewGuid(), featureId = "image-png", paths = new[] { image }, startImmediately = false }));
            Require((await Control(stopped, "stop")).GetProperty("state").GetString() == "Cancelled", "Stop failed.");
            await Control(stopped, "retry"); await Completed(stopped); await Control(stopped, "remove");
            await Error("avamedia_get_task", new { taskId = stopped });
        });
        await Case("feature-format-with-partial-options", async () =>
        {
            var requestId = Guid.NewGuid();
            var arguments = new { requestId, featureId = "audio-mp3", paths = new[] { video }, options = new { audioBitrate = 128 } };
            var id = Id(await Call("avamedia_create_task", arguments));
            var result = await Completed(id); Require(result.GetProperty("task").GetProperty("output").GetString()!.EndsWith(".mp3"), "Partial options replaced feature's MP3 format.");
            Require(Id(await Call("avamedia_create_task", arguments)) == id, "Partial-options retry created another task.");
            await Error("avamedia_create_task", new { requestId, arguments.featureId, arguments.paths, options = new { audioBitrate = 128, format = "mp4" } });
            id = Id(await Call("avamedia_create_task", new { requestId = Guid.NewGuid(), featureId = "audio-mp3", paths = new[] { video }, options = new { format = "wav" } }));
            Require((await Completed(id)).GetProperty("task").GetProperty("output").GetString()!.EndsWith(".wav"), "Explicit format was discarded.");
        });
        Guid[] tagged = [];
        await Case("real-image-and-video-ai-labels", async () =>
        {
            var result = await Call("avamedia_tag_media", new { requestId = Guid.NewGuid(), paths = new[] { inputs }, analysis = new { videoFrames = 2, batchSize = 1 } });
            tagged = result.GetProperty("tasks").EnumerateArray().Select(task => task.GetProperty("id").GetGuid()).ToArray(); Require(tagged.Length == 2, "Folder tagging did not create two tasks.");
            foreach (var id in tagged)
            {
                var done = await Completed(id); var labels = done.GetProperty("result");
                Require(labels.GetProperty("inferredFrames").GetInt32() > 0 && labels.GetProperty("labels").GetArrayLength() > 0, "No real model inference results.");
            }
        });
        await Case("real-ai-folder-classification", async () =>
        {
            var rule = new FolderClassificationRule("people", "人物", [new("woman", "女性", "female person") { Tags = [["1girl"]] }, new("man", "男性", "male person") { Tags = [["1boy"]] }])
            { UseAutomaticSettings = false, Threshold = .2, Margin = 0, MinimumAgreement = .6 };
            var id = Id(await Call("avamedia_classify_media", new { requestId = Guid.NewGuid(), paths = new[] { inputs }, rules = new[] { rule } }));
            var done = await Completed(id); Require(done.GetProperty("result").GetProperty("completed").GetInt32() == 2, "Classification missed files.");
            var page = await Call("avamedia_get_task", new { taskId = id, includeResult = true, limit = 1 });
            Require(page.GetProperty("nextOffset").GetInt32() == 1 && page.GetProperty("result").GetProperty("files")[0].GetProperty("decisions").GetArrayLength() == 1, "Missing classification decisions or paging.");
        });
        await Case("ai-label-to-rename-transaction-and-idempotency", async () =>
        {
            Require(tagged.Length == 2, "Tagging prerequisite failed.");
            var rename = Path.Combine(Root, "rename"); Directory.CreateDirectory(rename);
            var sources = new[] { image, video }.Select(path => { var target = Path.Combine(rename, Path.GetFileName(path)); File.Copy(path, target); return target; }).ToArray();
            var labels = (await Call("avamedia_get_task", new { taskId = tagged[0], includeResult = true })).GetProperty("result").GetProperty("labels");
            var keyword = labels[0].GetProperty("label").GetString()!;
            var preview = await Call("avamedia_preview_rename", new { paths = sources, rules = new { pattern = "{keyword}_{index}", digits = 2 }, keywords = sources.ToDictionary(path => path, _ => keyword) });
            var plan = preview.GetProperty("plan"); var originalHashes = sources.Select(Hash).ToArray();
            var arguments = new { requestId = Guid.NewGuid(), plan };
            var id = Id(await Call("avamedia_apply_rename", arguments)); var done = await Completed(id);
            Require(done.GetProperty("result").GetProperty("applied").GetBoolean(), "Rename was not committed.");
            Require(File.Exists(done.GetProperty("result").GetProperty("journal").GetString()), "Rename journal missing.");
            Require(Id(await Call("avamedia_apply_rename", arguments)) == id, "Completed rename repeated.");
            for (var i = 0; i < sources.Length; i++) Require(!File.Exists(sources[i]) && Hash(plan[i].GetProperty("target").GetString()!) == originalHashes[i], "Rename changed source bytes.");
        });
        await Case("changed-rename-source-is-rejected", async () =>
        {
            var path = Path.Combine(Root, "stale.txt"); await File.WriteAllTextAsync(path, "before");
            var plan = (await Call("avamedia_preview_rename", new { paths = new[] { path } })).GetProperty("plan");
            await File.AppendAllTextAsync(path, "changed"); await Error("avamedia_apply_rename", new { requestId = Guid.NewGuid(), plan }); Require(File.Exists(path), "Changed source was renamed.");
        });
        await Case("failed-ai-task-retry", async () =>
        {
            var broken = Path.Combine(Root, "repair.jpg"); await File.WriteAllTextAsync(broken, "broken image");
            var id = Id(await Call("avamedia_tag_media", new { requestId = Guid.NewGuid(), paths = new[] { broken } }));
            await WaitState(id, "Failed"); File.Copy(image, broken, true); await Control(id, "retry");
            Require((await Completed(id)).GetProperty("result").GetProperty("labels").GetArrayLength() > 0, "Retry did not infer labels.");
        });
        await Case("contact-sheet-validation-and-real-output", async () =>
        {
            await Error("avamedia_create_task", new { requestId = Guid.NewGuid(), featureId = "contact-sheet", paths = new[] { video }, options = new { contactSheet = new { columns = 11, rows = 1 } } });
            var id = Id(await Call("avamedia_create_task", new { requestId = Guid.NewGuid(), featureId = "contact-sheet", paths = new[] { video }, options = new { contactSheet = new { columns = 2, rows = 2, cellWidth = 160 } } }));
            var done = await Completed(id); var output = done.GetProperty("task").GetProperty("output").GetString()!;
            Require(new FileInfo(output).Length > 100, "No contact sheet.");
            await Process(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", output, "-f", "null", "-");
        });
        await Case("restart-restores-tasks-and-ai-results", async () =>
        {
            var before = await Call("avamedia_list_tasks"); var count = before.GetProperty("total").GetInt32();
            await Client.DisposeAsync(); Client = null; Window.Close(); await WaitUntil(() => !Window.IsVisible, TimeSpan.FromSeconds(20));
            Window = new MainWindow(storage); Window.Show(); await WaitUntil(() => Service().Endpoint is not null, TimeSpan.FromSeconds(20)); Client = await Connect(Service().Endpoint!);
            Require((await Call("avamedia_list_tasks")).GetProperty("total").GetInt32() == count, "Restart lost tasks.");
            foreach (var id in tagged) Require((await Call("avamedia_get_task", new { taskId = id, includeResult = true })).GetProperty("result").GetProperty("labels").GetArrayLength() > 0, "Restart lost AI results.");
        });
        await Case("open-cors-and-service-reconfiguration", async () =>
        {
            using var http = new HttpClient(); using var request = new HttpRequestMessage(HttpMethod.Options, Service().Endpoint);
            request.Headers.Add("Origin", "https://mcp-client.example"); request.Headers.Add("Access-Control-Request-Method", "POST"); request.Headers.Add("Access-Control-Request-Headers", "Mcp-Protocol-Version,Content-Type");
            using var response = await http.SendAsync(request); Require(response.IsSuccessStatusCode && response.Headers.GetValues("Access-Control-Allow-Origin").Single() == "*", "Open CORS failed.");
            await Client!.DisposeAsync(); Client = null;
            await Service().ConfigureAsync(new() { Enabled = false, Port = port }); Require(Service().Endpoint is null, "Disable kept listener alive.");
            await Service().ConfigureAsync(new() { Enabled = true, Port = port, AllowLan = true }); Client = await Connect(Service().Endpoint!);
            Require((await Call("avamedia_list_tasks")).GetProperty("total").GetInt32() > 0, "LAN listener lost workspace.");
            await Client.DisposeAsync(); Client = null;
            await Service().ConfigureAsync(new() { Enabled = true, Port = port }); Client = await Connect(Service().Endpoint!); await Call("avamedia_capabilities");
        });
        await Case("original-fixtures-unchanged", () => { Require(originals.All(pair => Hash(pair.Key) == pair.Value), "Source media changed."); return Task.CompletedTask; });
    }

    private static McpService Service() => (McpService)typeof(MainWindow).GetField("_mcp", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
    private static Task<McpClient> Connect(string endpoint) => McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new(endpoint), TransportMode = HttpTransportMode.StreamableHttp }), new() { ProtocolVersion = McpTools.ProtocolVersion });
    private static Guid Id(JsonElement submission) => submission.GetProperty("tasks")[0].GetProperty("id").GetGuid();
    private static Task<JsonElement> Control(Guid id, string action) => Call("avamedia_control_task", new { taskId = id, action });
    private static async Task WaitState(Guid id, string expected)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            var state = (await Call("avamedia_get_task", new { taskId = id })).GetProperty("task").GetProperty("state").GetString();
            if (state == expected) return; if (state is "Failed" or "Cancelled" or "Completed") throw new Exception("Unexpected state: " + state); await Task.Delay(100);
        }
        throw new TimeoutException("Expected state " + expected);
    }
    private static async Task<JsonElement> Completed(Guid id)
    {
        var until = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < until)
        {
            var result = await Call("avamedia_get_task", new { taskId = id, includeResult = true }); var task = result.GetProperty("task");
            var state = task.GetProperty("state").GetString(); if (state == "Completed") return result;
            if (state is "Failed" or "Cancelled") throw new Exception(task.GetProperty("error").GetString());
            await Task.Delay(250);
        }
        throw new TimeoutException("Task did not complete: " + id);
    }
    private static async Task<JsonElement> Call(string name, object? arguments = null, bool error = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var values = arguments is null ? null : JsonSerializer.SerializeToElement(arguments, Json).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value);
        var result = await Client!.CallToolAsync(name, values, cancellationToken: timeout.Token);
        var number = ++Calls; var payload = JsonSerializer.SerializeToElement(result, Json);
        await File.WriteAllTextAsync(Path.Combine(Root, $"call-{number:0000}-{name}.json"), payload.GetRawText());
        if (error) { Require(result.IsError == true, "Invalid request was admitted: " + name); return payload; }
        Require(result.IsError != true, name + ": " + string.Join("; ", result.Content.OfType<TextContentBlock>().Select(content => content.Text)));
        return payload.GetProperty("structuredContent").Clone();
    }
    private static Task<JsonElement> Error(string name, object arguments) => Call(name, arguments, true);
    private static async Task Case(string name, Func<Task> test)
    {
        var elapsed = Stopwatch.StartNew();
        try { await test(); Checks.Add(new { name, passed = true, milliseconds = elapsed.ElapsedMilliseconds }); Console.WriteLine("PASS " + name); }
        catch (Exception error) { Failures++; Checks.Add(new { name, passed = false, error = error.ToString(), milliseconds = elapsed.ElapsedMilliseconds }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static async Task WaitUntil(Func<bool> ready, TimeSpan timeout)
    { var until = DateTime.UtcNow + timeout; while (!ready()) { if (DateTime.UtcNow >= until) throw new TimeoutException(); await Task.Delay(50); } }
    private static async Task Process(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardError = true, UseShellExecute = false }; foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!; var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Require(process.ExitCode == 0, await error);
    }
}
