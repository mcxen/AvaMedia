using System.Security.Cryptography;
using System.Text.Json;
using AvaMedia.Core;

internal static class RealPeopleAcceptance
{
    private sealed record Fixture(string Id, string Path, bool ExpectedNsfw, string? ExpectedPose = null);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static async Task RunAsync(string root, string inventory, AppSettings settings)
    {
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(await File.ReadAllTextAsync(inventory))!;
        Check(fixtures.Length >= 3 && fixtures.Any(file => !file.ExpectedNsfw) && fixtures.Any(file => VideoFormats.IsVideo(file.Path)),
            "真实图片、视频与普通真人对照");
        var originals = fixtures.ToDictionary(file => file.Id, file => Fingerprint(file.Path));
        var folder = Path.Combine(root, "media"); Directory.CreateDirectory(folder);
        var paths = fixtures.ToDictionary(file => file.Id, file => Path.Combine(folder, file.Id + Path.GetExtension(file.Path)));
        foreach (var fixture in fixtures)
            if (!BatchRename.PathComparer.Equals(fixture.Path, paths[fixture.Id])) File.Copy(fixture.Path, paths[fixture.Id], true);
        var library = WordLibraryCatalog.BuiltIns.Single(item => item.Id == "real-people");
        WordLibraryCatalog.Validate(library.Entries);
        Check(library.Entries.Any(entry => entry.Category == "NSFW体位")
            && library.Entries.Any(entry => entry.Tags.Contains("all_fours")), "真人中文姿态与体位词库");
        var saved = new WordLibraryStore(Path.Combine(root, "libraries.json"));
        saved.SaveSelection(WordLibraryTarget.JoyTag, library.Entries.Where(entry => entry.Supports(WordLibraryTarget.JoyTag))
            .Select(entry => new SelectedWord(library.Id, entry.Label)).ToArray());
        Check(saved.Resolve(WordLibraryTarget.JoyTag).All(entry => entry.Supports(WordLibraryTarget.JoyTag)), "词库持久化及实际模型词表匹配");

        var store = new ModelStore();
        foreach (var id in new[] { ModelCatalog.JoyTagId, ModelCatalog.NsfwId })
        {
            if (!await store.IsInstalledAsync(id, true))
                await store.DownloadAsync(id, new InlineProgress<ModelDownloadProgress>(value =>
                { if (value.Stage != "下载") Console.WriteLine($"{id}: {value.Stage} {value.Percent}%"); }),
                    sourcePreference: new(ModelSourceKind.HuggingFace));
            Check(await store.IsInstalledAsync(id, true), "模型完整下载及 SHA256 · " + id);
        }
        var updates = new List<MediaTagProgress>();
        var engine = new MediaEngine(settings);
        var options = new MediaTagOptions(VideoFrames: 8, PreferGpu: false) { RealPeopleOnly = true, RecognizeNsfw = true };
        var results = await new MediaTagService(engine, store).AnalyzeAsync(paths.Values, options,
            new InlineProgress<MediaTagProgress>(updates.Add));
        Check(results.Count == fixtures.Length && updates.All(value => value.Error is null), "生产抽帧及双模型实际推理");
        var summary = new List<object>();
        foreach (var fixture in fixtures)
        {
            var result = results.Single(item => BatchRename.PathComparer.Equals(item.Path, paths[fixture.Id]));
            var nsfw = result.Nsfw ?? throw new Exception("Missing independent NSFW classification");
            Check(result.Scores.Count == 5813 && result.RealPeopleOnly && result.Frames.All(frame => frame.Values.Length == 5813), "完整原始标签分数 · " + fixture.Id);
            Check(nsfw.Frames.Count == result.SampledFrames && nsfw.Frames.Select(frame => frame.Seconds).SequenceEqual(result.Frames.Select(frame => frame.Seconds))
                && nsfw.Frames.All(frame => double.IsFinite(frame.Score) && frame.Score is >= 0 and <= 1), "双模型采样时间及有限分数 · " + fixture.Id);
            Check(nsfw.Suspected == fixture.ExpectedNsfw, "真人 NSFW 标注对照 · " + fixture.Id);
            var labels = MediaTagText.QualifyingLabels(result, .4, .55, .03);
            Check(labels.All(label => label.Model != ModelCatalog.JoyTagId || label.Tags.All(WordLibraryCatalog.RealPeopleTags.Contains)), "过滤角色与作品标签 · " + fixture.Id);
            Check(labels.All(label => label.Label.Any(character => character is >= '\u4e00' and <= '\u9fff')), "中文结果标签 · " + fixture.Id);
            var sidecar = await MediaTagText.SaveAsync(result, labels, .4, .55, .03, CancellationToken.None);
            var body = await File.ReadAllTextAsync(sidecar);
            Check(body.Contains("真人 NSFW 分类") && body.Contains(ModelCatalog.NsfwId)
                && nsfw.Frames.All(frame => body.Contains(MediaTime.Format(frame.Seconds))), "同目录 TXT 分数与采样时间 · " + fixture.Id);
            await MediaTagText.SaveAsync(result, MediaTagText.QualifyingLabels(result, .9, .55, .03), .9, .55, .03, CancellationToken.None);
            Check(Directory.EnumerateFiles(folder, "*.ai-tags.txt").Count() <= summary.Count + 1, "更新报告替换所属文件 · " + fixture.Id);
            await MediaTagText.SaveAsync(result, labels, .4, .55, .03, CancellationToken.None);
            if (nsfw.Suspected) Check(NsfwModeration.Evaluate(result, .99).State == NsfwSignalState.Suspected, "NSFW 分类与标签阈值独立 · " + fixture.Id);
            var pose = fixture.ExpectedPose is null ? null : result.Scores.Single(score => score.Tag == fixture.ExpectedPose);
            if (pose is not null) Check(pose.Maximum >= .4, "真人姿态人工标注对照 · " + fixture.Id);
            summary.Add(new { fixture.Id, fixture.ExpectedNsfw, ActualNsfw = nsfw.Suspected, nsfw.Average, nsfw.Maximum,
                result.SampledFrames, result.InferredFrames, result.Backend, NsfwBackend = nsfw.Backend,
                fixture.ExpectedPose, PoseScore = pose?.Maximum, PoseMatched = pose is null ? (bool?)null : pose.Maximum >= .4,
                Labels = labels, Report = Path.GetFileName(sidecar) });
        }
        var jsonPath = Path.Combine(root, "results.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(results, Json));
        var restored = JsonSerializer.Deserialize<MediaTagResult[]>(await File.ReadAllTextAsync(jsonPath))!;
        Check(restored.Length == results.Count && restored.All(result => result.Nsfw?.Frames.Count == result.SampledFrames), "JSON 完整结果往返");

        var adult = fixtures.First(file => file.ExpectedNsfw && !VideoFormats.IsVideo(file.Path));
        var task = new MediaTagTaskOptions { Analysis = options };
        var job = ConversionBatch.CreateJobs(Catalog.Find("media-ai"), [paths[adult.Id]], folder,
            new ConversionOptions { Format = "txt", MediaTag = task }).Single();
        await engine.Execute(job, _ => { }, CancellationToken.None);
        Check(File.Exists(job.Output) && (await File.ReadAllTextAsync(job.Output)).Contains("真人 NSFW 分类"), "生产任务执行及报告输出");
        Check(fixtures.All(file => originals[file.Id] == Fingerprint(file.Path)), "所有原始素材字节与修改时间保持一致");
        Check(!Directory.EnumerateFiles(folder, ".avamedia-tags-*.tmp").Any(), "报告写入无临时文件残留");
        using (await store.AcquireAsync(ModelCatalog.JoyTagId)) { }
        using (await store.AcquireAsync(ModelCatalog.NsfwId)) { }
        await File.WriteAllTextAsync(Path.Combine(root, "acceptance.json"), JsonSerializer.Serialize(new
        {
            Passed = true, Date = DateTimeOffset.UtcNow, RealPeopleWords = library.Entries.Length,
            NsfwModel = ModelCatalog.Find(ModelCatalog.NsfwId), Samples = summary, QueueReport = Path.GetFileName(job.Output),
            AccuracyScope = "人工确认的 NSFW 与普通样本对照；姿态仅记录命中，不构成完整体位准确率基准"
        }, Json));
    }
    private static string Fingerprint(string path)
    {
        var file = new FileInfo(path); using var stream = File.OpenRead(path);
        return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(SHA256.HashData(stream))}";
    }
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
