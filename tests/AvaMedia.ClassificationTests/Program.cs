using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Simple;
using AvaMedia.Core;
using AvaMedia.Desktop;

var root = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "artifacts/classification-ablation");
var mode = args.ElementAtOrDefault(1) ?? "optimized";
var fixtures = Path.Combine(root, "fixtures"); Directory.CreateDirectory(fixtures);
var output = Path.Combine(root, mode); Directory.CreateDirectory(output);
var checks = new List<string>();
var snapshots = new Dictionary<string, object>();
var stamp = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks.Add(name); Console.WriteLine("PASS " + name);
}
string Fixture(string name)
{
    var path = Path.Combine(fixtures, name);
    if (!File.Exists(path)) { File.WriteAllBytes(path, [1, 2, 3, 4, 5]); File.SetLastWriteTimeUtc(path, stamp); }
    return path;
}
var photo = Fixture("photo.png"); var video = Fixture("video.mp4");
var tags = WordLibraryCatalog.JoyTags.Order(StringComparer.Ordinal).ToArray();
MediaTagResult Media(string path, params (string Tag, double Score)[] selected)
{
    var values = tags.Select(tag => selected.FirstOrDefault(item => item.Tag == tag).Score).ToArray();
    var file = new FileInfo(path);
    return new(path, tags.Select((tag, index) => new MediaTagScore(tag, values[index], values[index])).ToArray(),
        VideoFormats.IsVideo(path) ? 3 : 1, 1, "controlled-fixture", file.Length, file.LastWriteTimeUtc)
    {
        DurationSeconds = VideoFormats.IsVideo(path) ? 120 : 0,
        Frames = (VideoFormats.IsVideo(path) ? new[] { 0d, 30d, 60d } : [0d])
            .Select(time => new MediaTagFrame(time, []) { Values = values.Select(value => (float)value).ToArray() }).ToArray()
    };
}
var imageMedia = Media(photo, ("standing", .9), ("shirt", .9), ("blue_shirt", .9), ("solo", .9), ("1girl", .9));
var videoMedia = Media(video, ("standing", .9), ("shirt", .9), ("blue_shirt", .9), ("solo", .9));
var excluded = Path.Combine(fixtures, "excluded"); Directory.CreateDirectory(excluded);
File.WriteAllText(Path.Combine(excluded, "hidden.png"), "excluded");
var link = Path.Combine(fixtures, "cycle"); if (!Directory.Exists(link)) Directory.CreateSymbolicLink(link, fixtures);
var scanned = FolderClassification.Scan([fixtures, photo], true, excluded, default);
Check(scanned.Files.Contains(photo) && scanned.Files.Contains(video) && !scanned.Files.Any(path => path.Contains("excluded") || path.Contains("cycle")), "scan excludes output and symbolic-link cycles");
foreach (var rule in FolderClassificationRule.Presets)
{
    FolderClassification.ValidateRules([rule]);
    var media = videoMedia with
    {
        Scenes = new("fixture", "fixture", null, .5, .04, [], videoMedia.Frames.Select(frame => new MediaSceneFrame(frame.Seconds, [])
        { Candidates = rule.Categories.Select((category, index) => new MediaSceneMatch(rule.Label(category.Id), "fixture", index == 0 ? .9 : .05, .85)).ToArray() }).ToArray())
    };
    var classified = FolderClassification.Classify(media, [rule], .5, true);
    snapshots[rule.Id] = classified;
    var manual = classified with { Decisions = classified.Decisions.Select(decision => decision with { Manual = true, CategoryId = "chosen", CategoryName = "人工选择" }).ToArray() };
    Check(FolderClassification.KeepManual(classified, manual).Decisions.Single().CategoryId == "chosen", rule.Id + " manual choice retained");
    Check(!FolderClassification.KeepManual(classified with { Media = media with { Length = media.Length + 1 } }, manual).Decisions.Single().Manual,
        rule.Id + " changed source invalidates manual choice");
}
try { FolderClassification.ValidateRules([new("invalid", "bad/name", [])]); throw new Exception("invalid rules accepted"); }
catch (ArgumentException) { Check(true, "invalid rules still rejected at settings boundary"); }
var pose = FolderClassificationRule.Presets.Single(rule => rule.Id == "body-pose");
var truncated = videoMedia with { Frames = [new(0, []) { Values = [0] }] };
Check(FolderClassification.Classify(truncated, [pose], .5).Decisions.Single().NeedsReview, "incomplete video evidence remains review");
var privateRule = FolderClassificationRule.Presets.Single(rule => rule.Id == "nipple-visibility");
Check(FolderClassification.Classify(imageMedia, [privateRule], .5).Decisions.Count == 0, "private rule hidden when disabled");
var outfitRule = FolderClassificationRule.Presets.Single(rule => rule.ByOutfit);
double[] Axis(int length, int index) { var values = new double[length]; values[index] = 1; return values; }
var outfits = Enumerable.Range(0, 9).Select(index =>
{
    var source = index == 8 ? video : Fixture($"outfit-{index}.png");
    var color = index < 4 ? 3 : 2;
    var media = Media(source, ("shirt", .95), (color == 3 ? "blue_shirt" : "red_shirt", .95));
    media = media with { OutfitFrames = media.Frames.Select(frame => new OutfitAppearanceFrame(frame.Seconds, 4096, .9, Axis(12, color))
    {
        Encoder = OutfitAppearanceService.Encoder, Embedding = Axis(384, color), BackgroundColors = Axis(12, 8),
        BackgroundEmbedding = Axis(384, index == 7 ? 10 : 8),
        BackgroundRegions = Enumerable.Range(0, 4).SelectMany(_ => Axis(384, index == 7 ? 10 : 8)).ToArray(),
        PersonPixels = 8000, BackgroundPixels = 8000
    }).ToArray() };
    if (index == 6) media = media with { OutfitFrames = [] };
    return FolderClassification.Classify(media, [outfitRule], .5);
}).ToArray();
var grouped = FolderOutfitClassification.Apply(outfits, [outfitRule]);
snapshots["outfit-groups"] = grouped;
Check(grouped[0].Decisions[0].CategoryId == grouped[1].Decisions[0].CategoryId, "same outfit joins same group");
Check(grouped[0].Decisions[0].CategoryId != grouped[4].Decisions[0].CategoryId, "conflicting outfit colors remain separate");
Check(grouped[6].Decisions[0].NeedsReview, "missing outfit evidence remains review");
snapshots["outfit-manual"] = FolderOutfitClassification.Apply(grouped.Select((file, index) => index == 0
    ? file with { Decisions = file.Decisions.Select(decision => decision with { Manual = true, CategoryId = "manual", CategoryName = "人工服装" }).ToArray() } : file), [outfitRule]);
var destinations = FolderOrganization.DestinationFolders(grouped, Path.Combine(root, "destinations"), false);
snapshots["destinations"] = destinations;
var storeJob = new Job { Inputs = grouped.Select(file => file.Media.Path).ToArray(), Options = new() { FolderClassification = new() { Rules = [outfitRule] } } };
try
{
    foreach (var file in grouped) await FolderClassificationTaskStore.SaveFileAsync(storeJob, new(file.Media.Path, file, Pending: false));
    var loaded = await FolderClassificationTaskStore.LoadAsync(storeJob);
    Check(JsonSerializer.Serialize(loaded.Files.Select(file => file.Result)) == JsonSerializer.Serialize(grouped), "checkpoint save/load preserves complete decisions and titles");
}
finally { FolderClassificationTaskStore.Delete(storeJob); }
foreach (var move in new[] { false, true })
{
    var source = Fixture(move ? "move.png" : "copy.png");
    var file = FolderClassification.Classify(Media(source), [], .5);
    var hash = SHA256.HashData(File.ReadAllBytes(source));
    var folder = Path.Combine(output, move ? "move" : "copy", Guid.NewGuid().ToString("N"));
    var plan = FolderOrganization.Preview([file], folder, true, true);
    var journal = Path.Combine(folder, "journal.json");
    var result = await FolderOrganization.ExecuteAsync(plan, move, journal, null, default);
    Check(result.Completed == 1 && result.Errors.Length == 0 && File.Exists(source) != move, (move ? "move" : "copy") + " executes with expected source ownership");
    Check(SHA256.HashData(File.ReadAllBytes(plan[0].Target)).SequenceEqual(hash), "organized content hash unchanged");
    if (!move)
    {
        var collision = FolderOrganization.Preview([file], folder, true, true);
        Check(collision[0].Target != plan[0].Target, "preview resolves destination collisions");
    }
    var undo = await FolderOrganization.UndoAsync(journal, null, default);
    Check(undo.Completed == 1 && File.Exists(source) && !File.Exists(plan[0].Target)
        && !File.Exists(plan[0].ReportPath) && SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(hash), "undo restores original and removes owned outputs");
}
var pairCalls = 0;
var clusterMethod = typeof(FolderOutfitClassification).GetMethod("Cluster", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(string));
var clusters = (List<List<string>>)clusterMethod.Invoke(null, [new[] { "a", "b", "c" },
    (Func<string, string, double>)((a, b) => { pairCalls++; return a == "a" && b == "b" || a == "b" && b == "a" ? .2 : .9; }), .8, CancellationToken.None])!;
Check(JsonSerializer.Serialize(clusters) == "[[\"a\",\"c\"],[\"b\"]]", "pair-score reuse preserves complete-link grouping");
if (mode != "baseline") Check(pairCalls == 3, "each symmetric pair is evaluated once across cluster passes");
var durationRule = FolderClassificationRule.Presets.Single(rule => rule.ByDuration);
var engine = new MetadataEngine();
var job = NewJob([photo, video], [durationRule]);
Job NewJob(string[] inputs, FolderClassificationRule[] rules)
{
    var value = new Job { FeatureId = "folder-classification", Inputs = inputs, Options = new() { FolderClassification = new() { Rules = rules } } };
    value.Output = FolderClassificationTaskStore.Folder(value); return value;
}
try
{
    using var cancel = new CancellationTokenSource();
    try { await new FolderClassificationJobService(engine).ExecuteAsync(job, _ => { if (job.ClassificationSnapshot?.Completed == 1) cancel.Cancel(); }, cancel.Token); }
    catch (OperationCanceledException) { }
    Check(job.ClassificationSnapshot?.Completed == 1, "interruption retains completed file checkpoint");
    await new FolderClassificationJobService(engine).ExecuteAsync(job, _ => { }, default);
    Check(job.ClassificationSnapshot?.Completed == 2 && engine.Probes == 1, "resume skips completed files and only probes pending video");
    await new FolderClassificationJobService(engine).ExecuteAsync(job, _ => { }, default);
    Check(engine.Probes == 1, "completed job reuses source-validated results");
}
finally { FolderClassificationTaskStore.Delete(job); }
if (mode != "baseline")
{
    var typeJob = NewJob([photo, video], []); var typeEngine = new MetadataEngine();
    try
    {
        await new FolderClassificationJobService(typeEngine).ExecuteAsync(typeJob, _ => { }, default);
        Check(typeJob.ClassificationSnapshot?.Completed == 2 && typeEngine.Probes == 0, "type-only classification needs neither AI nor probing");
    }
    finally { FolderClassificationTaskStore.Delete(typeJob); }
}
// Headless control state only: no native-window or pixel acceptance is claimed.
AppBuilder.Configure<ClassificationTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
var window = new FolderClassificationWindow(engine, null, () => true, _ => Task.CompletedTask, _ => { }, _ => Task.CompletedTask,
    _ => { }, () => { }, () => { });
var pendingMethod = typeof(FolderClassificationWindow).GetMethod("OutfitPending", BindingFlags.NonPublic | BindingFlags.Instance)!;
var counted = new CountingFrames(grouped[0].Media.OutfitFrames);
var countedResult = grouped[0] with { Media = grouped[0].Media with { OutfitFrames = counted } };
for (var index = 0; index < 5; index++) Check(!(bool)pendingMethod.Invoke(window, [countedResult, outfitRule])!, "complete outfit remains available on repaint " + index);
if (mode != "baseline") Check(counted.Enumerations == 1, "five repaints validate outfit vectors only once");
var changedResult = countedResult with { Media = countedResult.Media with { OutfitFrames = [] } };
Check((bool)pendingMethod.Invoke(window, [changedResult, outfitRule])!, "new media snapshot invalidates outfit completeness cache");
File.WriteAllText(Path.Combine(output, "snapshots.json"), JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { Checks = checks, OutfitValidationPasses = counted.Enumerations, ClusterSimilarityCalls = pairCalls }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Completed {checks.Count} scoped checks; outfit validation passes: {counted.Enumerations}; cluster similarity calls: {pairCalls}");

sealed class MetadataEngine : IMediaEngine
{
    public int Probes;
    public AppSettings Settings { get; } = new();
    public string FFmpeg => throw new InvalidOperationException("Metadata path must not start FFmpeg");
    public string FFprobe => throw new InvalidOperationException("Fake probe only");
    public Task<MediaInfo> Probe(string path, CancellationToken ct = default, int videoStreamIndex = 0, int audioStreamIndex = 0)
    { Probes++; return Task.FromResult(new MediaInfo(120, 640, 360, false, true, "", "fixture")); }
    public Task<byte[]> Thumbnail(string input, double seconds, int width = 640, int height = 360, CancellationToken ct = default, bool pad = true, int videoStreamIndex = 0, bool endExclusive = false) => throw new InvalidOperationException("Metadata path must not decode frames");
    public Task<double> AdjacentFrameTime(string input, double seconds, int direction, CancellationToken ct = default, int videoStreamIndex = 0) => throw new NotSupportedException();
    public Task Execute(Job job, Action<double> progress, CancellationToken ct) => throw new NotSupportedException();
}
sealed class CountingFrames(IReadOnlyList<OutfitAppearanceFrame> frames) : IReadOnlyList<OutfitAppearanceFrame>
{
    public int Enumerations;
    public int Count => frames.Count;
    public OutfitAppearanceFrame this[int index] => frames[index];
    public IEnumerator<OutfitAppearanceFrame> GetEnumerator() { Enumerations++; return frames.GetEnumerator(); }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
sealed class ClassificationTestApp : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());
}
