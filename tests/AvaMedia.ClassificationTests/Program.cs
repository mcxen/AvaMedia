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
// Live classification projections use the same controlled media fixtures as the classifier.
var insightDecision = new FolderClassificationDecision(outfitRule.Id, outfitRule.Name, "group-a", "蓝色上衣",
    [new("group-a", "蓝色上衣", .8, 2)], [new(0, "group-a", .9, .1), new(30, "group-b", .7, .1), new(60, "group-a", .8, .1)],
    null, 2d / 3, "服装特征一致\n背景相近") { ByOutfit = true, OutfitShootName = "拍摄组01" };
var insightMedia = videoMedia with
{
    OutfitFrames = grouped[8].Media.OutfitFrames.Select(frame => frame with { PersonPixels = 2048 }).ToArray(),
    Scenes = new("fixture", "fixture", null, .5, .04, [new("室内", "场景空间", .85, 3)], [])
};
var insightFile = new FolderClassificationTaskFile(video, new(insightMedia, [insightDecision], []), Pending: false);
var insightImage = new FolderClassificationTaskFile(photo, new(imageMedia with { SceneSkipped = true },
    [insightDecision with { Frames = [], Scores = [new("group-a", "蓝色上衣", .95, 1)] }], []), Pending: false);
var insightPending = insightFile with { Path = Fixture("review.mp4"), Result = insightFile.Result! with
    { Decisions = [insightDecision with { CategoryId = null, Scores = [new("group-b", "红色上衣", .5, 0)] }] } };
var insightFailure = new FolderClassificationTaskFile(Fixture("failed.png"), Error: "无法读取", Pending: false);
var insightQueued = new FolderClassificationTaskFile(Fixture("queued.png"));
var insightSnapshot = new FolderClassificationTaskSnapshot([insightFile, insightImage, insightPending, insightFailure, insightQueued]);
var sceneRule = FolderClassificationRule.Presets.Single(rule => rule.Id == "scenery");
var insightRules = new[] { sceneRule, outfitRule };
var stageViews = FolderClassificationInsights.Stages(insightSnapshot, insightRules, 3, "识别服装区域");
Check(stageViews[0].Done == 5 && stageViews[0].Total == 5 && stageViews[0].Fraction == 1
    && stageViews[1].Done == 4 && stageViews[2].Done == 3 && stageViews[2].Enabled
    && stageViews[3].Done == 2 && stageViews[3].Active && stageViews[4].Done == 3 && stageViews[4].Total == 0,
    "stage counts derive from stored results, errors, skipped scenes and complete outfits");
Check(!FolderClassificationInsights.Stages(insightSnapshot, [], 0, null)[2].Enabled, "scene stage disabled without semantic rules");
var visual = FolderClassificationInsights.Visual(insightFile, insightRules, false);
Check(visual.State == FolderFileState.Done && Math.Abs(visual.FrameConsistency - 2d / 3) < .000001
    && visual.InconsistentFrame == 2 && visual.Frames.Select(frame => frame.Consistent).SequenceEqual([true, false, true]), "visual reports majority agreement and first inconsistent frame");
Check(visual.Tags.Length == 8 && visual.Tags.Zip(visual.Tags.Skip(1)).All(pair => pair.First.Score >= pair.Second.Score)
    && Math.Abs(visual.OutfitColors.Sum(color => color.Share) - 1) < .000001 && visual.OutfitColors.All(color => color.Hex.Length == 7)
    && Math.Abs(visual.PersonRatio - .5) < .000001 && !visual.Reason.Contains('\n'), "visual ranks tags, normalizes colors and summarizes evidence");
Check(FolderClassificationInsights.Visual(insightImage, insightRules, false).FrameConsistency == 1
    && FolderClassificationInsights.Visual(insightPending, insightRules, false).PendingReview
    && FolderClassificationInsights.Visual(insightQueued, insightRules, true).State == FolderFileState.Processing, "image consistency and file states");
var histogram = FolderClassificationInsights.Histogram(insightSnapshot.Files, .85, 3, .5, .95);
Check(histogram.Bins.SequenceEqual([1, 0, 2]) && histogram.Grouped == 1 && histogram.Pending == 3 && histogram.Failed == 1
    && histogram.Threshold == .85, "histogram clamps endpoints, bins scores and applies threshold");
var insightGroups = FolderClassificationInsights.Groups(insightSnapshot.Files, new Dictionary<string, int> { [outfitRule.Id + "/group-a"] = 1 });
Check(insightGroups.Length == 1 && insightGroups[0].Count == 2 && insightGroups[0].Added == 1 && insightGroups[0].CoverPaths[0] == photo,
    "group diff and covers use accepted primary decisions ordered by similarity");
Check(FolderClassificationInsights.Groups(insightSnapshot.Files, new Dictionary<string, int> { [outfitRule.Id + "/group-a"] = 9 })[0].Added == 0,
    "group additions never become negative");
var beforeAfter = FolderClassificationInsights.BeforeAfter(insightSnapshot.Files, [fixtures]);
Check(beforeAfter.Images == 3 && beforeAfter.Videos == 2 && beforeAfter.Bytes == 25 && beforeAfter.Sources.Single().Files == 5
    && beforeAfter.Shoots.Single().Count == 2 && beforeAfter.PendingCount == 2, "before/after source, shoot, byte and review counts");
Check(FolderClassificationInsights.BeforeAfter([new(Path.Combine(fixtures, "missing.png"))], [fixtures]).Bytes == 0, "before/after tolerates missing media");
var resolvedInsight = FolderClassificationInsights.ResolvePending(insightPending, outfitRule.Id, "group-a", "蓝色上衣");
Check(resolvedInsight.Result!.Decisions[0] is { Manual: true, CategoryId: "group-a", CategoryName: "蓝色上衣", OutfitShootName: "拍摄组01" },
    "manual resolve sets category and preserves shoot");
try { FolderClassificationInsights.ResolvePending(insightPending, "absent", "group-a", "蓝色上衣"); throw new Exception("Missing decision accepted"); }
catch (ArgumentException) { Check(true, "manual resolve rejects missing decisions"); }
var liveJob = NewJob(insightSnapshot.Files.Select(file => file.Path).ToArray(), insightRules);
liveJob.ClassificationSnapshot = insightSnapshot;
try
{
    var saved = await FolderClassificationTaskStore.ResolveAsync(liveJob, insightPending.Path, outfitRule.Id, "group-a");
    var reloaded = await FolderClassificationTaskStore.LoadAsync(liveJob);
    Check(saved.Result!.Decisions[0].Manual && liveJob.ClassificationSnapshot.Files.Single(file => file.Path == insightPending.Path).Result!.Decisions[0].Manual
        && reloaded.Files.Single(file => file.Path == insightPending.Path).Result!.Decisions[0].Manual, "resolve persists checkpoint and publishes snapshot");
    await FolderClassificationTaskStore.SaveFileAsync(liveJob, insightPending);
    Check((await FolderClassificationTaskStore.LoadAsync(liveJob)).Files.Single(file => file.Path == insightPending.Path).Result!.Decisions[0].Manual,
        "subsequent worker checkpoint preserves live manual decision");
}
finally { FolderClassificationTaskStore.Delete(liveJob); }
var progressModel = new FolderClassificationProgress(); var applied = 0;
var lastApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
progressModel.PropertyChanged += (_, args) =>
{
    Interlocked.Increment(ref applied);
    if (args.PropertyName != "") throw new Exception("Expected a single all-properties notification");
    if (progressModel.Percent == 99) lastApplied.TrySetResult();
};
for (var index = 0; index < 100; index++) { liveJob.Progress = index; progressModel.UpdateThrottled(liveJob, action => action()); }
await lastApplied.Task.WaitAsync(TimeSpan.FromSeconds(3));
Check(applied <= 2 && progressModel.Percent == 99 && progressModel.Files.Count == 5, "throttle coalesces burst and always applies final update");
var cachedVisual = progressModel.Files[0]; progressModel.Update(liveJob);
Check(ReferenceEquals(cachedVisual, progressModel.Files[0]), "unchanged result reuses visual reference");
// A backed-up dispatcher must also retain just one callback and read the latest update.
var queuedPosts = new System.Collections.Concurrent.ConcurrentQueue<Action>();
var delayedModel = new FolderClassificationProgress();
for (var index = 0; index < 100; index++) { liveJob.Progress = index; delayedModel.UpdateThrottled(liveJob, action => queuedPosts.Enqueue(action)); }
Check(queuedPosts.Count == 1, "throttle bounds dispatcher backlog");
queuedPosts.TryDequeue(out var applyLatest); applyLatest!();
Check(delayedModel.Percent == 99, "queued dispatcher callback applies latest state");

string? thumbnailFfmpeg = null;
try { thumbnailFfmpeg = MediaEngine.Resolve("", "ffmpeg"); }
catch (FileNotFoundException) { Console.WriteLine("SKIP thumbnail fixtures: ffmpeg not found"); }
if (thumbnailFfmpeg is not null)
{
    var thumbnailRoot = Path.Combine(root, "thumbnail-fixtures"); Directory.CreateDirectory(thumbnailRoot);
    var largeImage = Path.Combine(thumbnailRoot, "large.jpg"); var shortVideo = Path.Combine(thumbnailRoot, "ten-seconds.mp4");
    async Task ThumbnailFF(params string[] arguments)
    {
        var result = await ProcessRunner.Run(thumbnailFfmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", .. arguments]);
        if (result.ExitCode != 0) throw new Exception(result.Error);
    }
    await ThumbnailFF("-f", "lavfi", "-i", "color=c=blue:s=4000x3000", "-frames:v", "1", "-threads", "1", largeImage);
    await ThumbnailFF("-f", "lavfi", "-i", "color=c=red:s=320x240:r=10", "-t", "10", "-c:v", "mpeg4", "-threads", "1", shortVideo);
    var thumbnailFolder = Path.Combine(thumbnailRoot, "cache"); var thumbnailCache = new MediaThumbnailCache(thumbnailFolder, 1);
    // Decode only the generated small JPEG header to inspect its dimensions.
    (int Width, int Height) ThumbnailSize(string path)
    {
        using var codec = SkiaSharp.SKCodec.Create(path);
        return (codec.Info.Width, codec.Info.Height);
    }
    var imageThumbnail = await thumbnailCache.GetAsync(largeImage, 160);
    var videoThumbnail = await thumbnailCache.GetAsync(shortVideo, 192, 5);
    Check(ThumbnailSize(imageThumbnail) == (160, 120) && ThumbnailSize(videoThumbnail) == (192, 144), "large JPEG and 10-second video thumbnails have requested width");
    var cacheFiles = Directory.GetFiles(thumbnailFolder); var thumbnailStamp = File.GetLastWriteTimeUtc(imageThumbnail);
    var watch = System.Diagnostics.Stopwatch.StartNew(); var cacheHit = thumbnailCache.GetAsync(largeImage, 160);
    Check(cacheHit.IsCompletedSuccessfully && await cacheHit == imageThumbnail && watch.Elapsed < TimeSpan.FromSeconds(1)
        && Directory.GetFiles(thumbnailFolder).Length == cacheFiles.Length && File.GetLastWriteTimeUtc(imageThumbnail) == thumbnailStamp, "thumbnail cache hit is immediate and writes no new file");
    var concurrentThumbnails = Enumerable.Range(0, 10).Select(_ => thumbnailCache.GetAsync(shortVideo, 208, 3)).ToArray();
    Check(concurrentThumbnails.All(task => ReferenceEquals(task, concurrentThumbnails[0])), "concurrent thumbnail requests share one generation task");
    var deduped = await Task.WhenAll(concurrentThumbnails);
    Check(deduped.Distinct().Count() == 1 && Directory.GetFiles(thumbnailFolder).Length == cacheFiles.Length + 1, "deduplicated generation writes one JPEG");
    var mosaic = await thumbnailCache.MosaicAsync([largeImage, shortVideo, largeImage], 321, 241, default);
    Check(ThumbnailSize(mosaic) == (321, 241) && await thumbnailCache.MosaicAsync([largeImage, shortVideo, largeImage], 321, 241, default) == mosaic,
        "mosaic preserves requested dimensions and caches with concurrency one");
    using var cancelledThumbnail = new CancellationTokenSource(); cancelledThumbnail.Cancel();
    try { await thumbnailCache.GetAsync(largeImage, 240, ct: cancelledThumbnail.Token); throw new Exception("Cancelled thumbnail accepted"); }
    catch (OperationCanceledException) { Check(true, "thumbnail honors cancellation"); }
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
