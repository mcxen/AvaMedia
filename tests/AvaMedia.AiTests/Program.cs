using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Simple;
using AvaMedia.Core;
using AvaMedia.Desktop;

internal static class Program
{
    public static string Root { get; private set; } = "";
    private static string InventoryPath = "";
    [STAThread]
    public static int Main(string[] args)
    {
        Root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/ai-e2e");
        InventoryPath = Path.GetFullPath(args.ElementAtOrDefault(2) ?? "artifacts/pikpak-model-validation/inventory.json");
        Directory.CreateDirectory(Root);
        if (args.FirstOrDefault() == "ui")
            return AppBuilder.Configure<AiTestApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        try { RunAsync(args.FirstOrDefault() ?? "core").GetAwaiter().GetResult(); return 0; }
        catch (Exception error) { File.WriteAllText(Path.Combine(Root, "error.txt"), error.ToString()); Console.Error.WriteLine(error); return 1; }
    }
    public static AppSettings Settings() => new()
    {
        FFmpegPath = "/opt/homebrew/bin/ffmpeg", FFprobePath = "/opt/homebrew/bin/ffprobe",
        AutoDetectGpu = true, CheckForUpdates = false, AutoUpdate = false, NotifyComplete = false,
        CloseToTray = false, MinimizeToTray = false, OutputFolder = Path.Combine(Root, "output")
    };
    private static async Task RunAsync(string mode)
    {
        var store = new ModelStore();
        if (mode == "download")
        {
            var last = -1;
            await store.DownloadAsync(ModelCatalog.EmbeddingId, new InlineProgress<ModelDownloadProgress>(value =>
            { if (value.Percent / 5 != last || value.Stage != "下载") { last = value.Percent / 5; Console.WriteLine($"{value.Stage} {value.Percent}%"); } }));
            Check(await store.IsInstalledAsync(ModelCatalog.EmbeddingId, true), "semantic download and hashes"); return;
        }
        var engine = new MediaEngine(Settings());
        var report = new List<object>();
        if (mode is "core" or "semantic")
        {
            using var inventory = JsonDocument.Parse(File.ReadAllText(InventoryPath));
            var files = inventory.RootElement.GetProperty("files").EnumerateArray()
                .Where(item => new[] { "I01", "I03", "I08", "I12", "V01", "V02" }.Contains(item.GetProperty("id").GetString()))
                .Select(item => (Id: item.GetProperty("id").GetString()!, Path: item.GetProperty("path").GetString()!)).ToArray();
            var copies = Path.Combine(Root, "media"); Directory.CreateDirectory(copies);
            foreach (var file in files) File.Copy(file.Path, Path.Combine(copies, file.Id + Path.GetExtension(file.Path)), true);
            var paths = files.Select(file => Path.Combine(copies, file.Id + Path.GetExtension(file.Path))).ToArray();
            var originalHashes = files.ToDictionary(file => file.Id, file => Hash(file.Path));
            if (mode == "semantic")
            {
                var activities = new List<AiActivity>();
                await using (var backend = await GemmaMediaEmbedding.StartAsync(store, CancellationToken.None))
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { backend.Backend, backend.FallbackReason, backend.AccelerationDetails }));
                    Check(!OperatingSystem.IsMacOS() || backend.Backend.Contains("Core ML"), "Core ML session for semantic model");
                    report.Add(new { backend.Backend, backend.FallbackReason, backend.AccelerationDetails });
                }
                var keywords = WordLibraryCatalog.BuiltIns.Single(library => library.Id == "common").Entries.Take(33)
                    .Select(word => new SemanticKeyword(word.Label, word.Description)).Append(new("长描述", new string('景', 500))).ToArray();
                await using (var matcher = await MediaKeywordMatcher.CreateAsync(engine, keywords, store, preferGpu: true,
                    progress: new InlineProgress<AiActivity>(activities.Add)))
                {
                    var result = await matcher.MatchAsync(paths.Single(path => Path.GetFileNameWithoutExtension(path) == "V01"), new(Frames: 4),
                        new InlineProgress<MediaKeywordProgress>(value => { if (value.Activity is { } activity) activities.Add(activity); }));
                    Check(result.Scores.Count == keywords.Length && result.Scores.All(score => double.IsFinite(score.Similarity)), "semantic labels and image vectors");
                    Check(activities.Any(value => value.Stage == "编码关键词" && value.Current == keywords.Length), "semantic multi-request label progress");
                    Check(activities.Any(value => value.Preview is { Length: > 0 }) && activities.Any(value => value.RecentResults.Length > 0), "semantic intermediate results");
                    report.Add(new { Semantic = result, Stages = activities.Select(value => value.Stage).Distinct().ToArray() });
                }
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await ExpectCancelled(async () => { await using var _ = await MediaKeywordMatcher.CreateAsync(engine, keywords, store, cancelled.Token); });
                using var lease = await store.AcquireAsync(ModelCatalog.EmbeddingId);
                Check(lease.Directory.Length > 0, "semantic process releases model lock");
            }
            else
            {
                Libraries();
                Check(new MediaFileRouter().Routes([new(paths[0], MediaFileKind.Image)]).All(route => !Catalog.IsBeta(route.Feature)), "Beta hidden");
                Check(new MediaFileRouter(true).Routes([new(paths[0], MediaFileKind.Image)]).Any(route => route.Feature.Id == "media-ai"), "Beta available");
                List<MediaTagResult>? cpu = null;
                foreach (var gpu in new[] { false, true })
                {
                    var updates = new List<MediaTagProgress>(); var watch = Stopwatch.StartNew();
                    var result = (await new MediaTagService(engine, store).AnalyzeAsync(paths, new(PreferGpu: gpu), new InlineProgress<MediaTagProgress>(updates.Add))).ToList();
                    Check(result.Count == paths.Length && updates.All(value => value.Error is null), $"tag analysis gpu={gpu}");
                    Check(result.All(value => value.Scores.Count == 5813 && value.Scores.All(score => double.IsFinite(score.Score))), "fixed model vocabulary");
                    Check(result.All(value => value.SampledFrames <= 8 && value.InferredFrames <= value.SampledFrames), "bounded video sampling");
                    Check(updates.Any(value => value.Activity?.Preview is { Length: > 0 }) && updates.Any(value => value.Activity?.RecentResults.Length > 0), "tag progress and intermediate results");
                    if (gpu && OperatingSystem.IsMacOS()) Check(result.All(value => value.Backend.Contains("Core ML") && value.FallbackReason is null), "Core ML session succeeds");
                    if (cpu is not null) Check(result.Zip(cpu).All(pair => pair.First.Scores.Zip(pair.Second.Scores).Max(score => Math.Abs(score.First.Score - score.Second.Score)) < .01), "CPU and Core ML score tolerance");
                    else cpu = result;
                    report.Add(new { Gpu = gpu, Seconds = watch.Elapsed.TotalSeconds, Results = result.Select(value => new { File = Path.GetFileName(value.Path), value.Backend, value.FallbackReason, value.SampledFrames, value.InferredFrames }) });
                }
                var query = MediaTagService.ParseQueries("黑长发，眼镜", WordLibraryCatalog.JoyTags);
                var matches = cpu!.Where(value => MediaTagService.MatchLabel(value, query, .4).Length > 0).ToArray();
                Check(matches.Length > 0, "real candidate matches");
                var before = matches.ToDictionary(value => value.Path, value => Hash(value.Path));
                var plan = BatchRename.PreviewRename(matches.Select(value => value.Path), new("{keyword}_{index}"),
                    matches.ToDictionary(value => value.Path, value => MediaTagService.MatchLabel(value, query, .4)));
                var journal = Path.Combine(Root, "rename.json"); BatchRename.ApplyRename(plan, journal);
                Check(plan.All(item => File.Exists(item.Target) && Hash(item.Target) == before[item.Source]), "rename actual files");
                BatchRename.UndoRename(journal); Check(before.All(item => File.Exists(item.Key) && Hash(item.Key) == item.Value), "undo preserves bytes");
                using var cancellation = new CancellationTokenSource(); var completed = new List<MediaTagResult>();
                await ExpectCancelled(() => new MediaTagService(engine, store).AnalyzeAsync(paths, new(), new InlineProgress<MediaTagProgress>(value =>
                { if (value.Result is { } result) { completed.Add(result); cancellation.Cancel(); } }), cancellation.Token));
                Check(completed.Count > 0, "cancellation preserves completed callback results");
                using (await store.AcquireAsync(ModelCatalog.JoyTagId)) { Check(true, "cancel releases tag model lock"); }
                var video = paths.Single(path => Path.GetFileNameWithoutExtension(path) == "V01");
                var personUpdates = new List<PersonClipProgress>();
                var person = await new PersonClipAnalysis(engine, store).AnalyzeAsync(video, new(), new InlineProgress<PersonClipProgress>(personUpdates.Add));
                Check(person.Segments.Count > 0 && personUpdates.Any(value => value.Activity?.Preview is { Length: > 0 }), "person intervals and previews");
                var jobs = QuickClipWorkflow.PrepareJoinedJobs([new(video, person.Info, person.Segments)], "MP4",
                    new() { VideoCodec = "libx264", AudioCodec = "aac", Width = 320, Quality = 26 }, Path.Combine(Root, "output"), false, "People");
                Directory.CreateDirectory(Path.Combine(Root, "output"));
                foreach (var job in jobs)
                {
                    await engine.Execute(job, _ => { }, CancellationToken.None);
                    var info = await engine.Probe(job.Output); var expected = person.Segments.Sum(segment => segment.End - segment.Start);
                    Check(info.HasVideo && info.Duration > 0 && Math.Abs(info.Duration - expected) < .5, "person joined export duration");
                    var decoded = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-i", job.Output, "-f", "null", "-"]);
                    Check(decoded.ExitCode == 0, "export fully decodes");
                    report.Add(new { PersonBackend = person.Backend, person.SampledFrames, person.InferredFrames, Segments = person.Segments.Count, ExportDuration = info.Duration, ExpectedDuration = expected });
                }
            }
            Check(files.All(file => Hash(file.Path) == originalHashes[file.Id]), "all original media byte-identical");
        }
        File.WriteAllText(Path.Combine(Root, mode + ".json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void Libraries()
    {
        foreach (var library in WordLibraryCatalog.BuiltIns) WordLibraryCatalog.Validate(library.Entries);
        Check(WordLibraryCatalog.JoyTags.Count == 5813, "built-in libraries validate");
        var text = string.Join('\n', Enumerable.Range(0, 2000).Select(index => $"动物\t猫{index}\tA photo of a cat.\tcat"));
        var entries = WordLibraryCatalog.ParseText(text); var libraryCopy = new WordLibrary("e2e", "测试词库", "验证来源", entries);
        var path = Path.Combine(Root, "word-libraries.json"); var first = new WordLibraryStore(path); var second = new WordLibraryStore(path);
        first.Save(libraryCopy); second.SaveSelection(WordLibraryTarget.JoyTag, entries.Select(word => new SelectedWord("e2e", word.Label)).ToArray());
        first.SaveSelection(WordLibraryTarget.Semantic, [new("ratings", "非NSFW")]);
        Check(second.Resolve(WordLibraryTarget.JoyTag).Length == 2000 && first.Resolve(WordLibraryTarget.Semantic).Length == 1, "2000-word persisted profiles");
        Check(WordLibraryCatalog.ParseText(WordLibraryCatalog.ToText(libraryCopy)).Length == 2000, "TSV roundtrip");
        var json = JsonSerializer.Deserialize<WordLibrary>(JsonSerializer.Serialize(libraryCopy))!;
        Check(json.Source == "验证来源" && json.Entries.Length == 2000, "JSON provenance roundtrip");
        first.Delete("e2e"); Check(second.Resolve(WordLibraryTarget.JoyTag).Length == 0, "deleted library selections removed");
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Check(bool passed, string name) { if (!passed) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private static async Task ExpectCancelled(Func<Task> action)
    { try { await action(); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { Check(true, "cancellation"); } }
}
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
public sealed class AiTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new SimpleTheme());
        foreach (var name in new[] { "Localization", "UiStyles", "Motion", "ControlRoles", "CategoryNavigation", "EditorStyles", "PlayerStyles" })
            Styles.Add(new StyleInclude(new Uri("avares://AvaMedia.Desktop/")) { Source = new Uri($"avares://AvaMedia.Desktop/Styles/{name}.axaml") });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var storage = new Storage(Path.Combine(Program.Root, "ui-state"));
            if (!File.Exists(Path.Combine(Program.Root, "ui-state", "settings.json"))) storage.SaveSettings(Program.Settings());
            desktop.MainWindow = new MainWindow(storage);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
