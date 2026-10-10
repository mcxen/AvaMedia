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
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// Explicitly invoked acceptance: native MainWindow, official HTTP MCP client and real MediaEngine.
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
            Require(tools.Count == 11 && tools.Select(tool => tool.Name).Distinct().Count() == 11, "Expected all 11 tools.");
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
