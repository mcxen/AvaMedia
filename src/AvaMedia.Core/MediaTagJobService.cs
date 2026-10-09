namespace AvaMedia.Core;

/// <summary>Queue payload for AI tagging: analysis options plus TXT report thresholds.</summary>
public sealed class MediaTagTaskOptions
{
    public MediaTagOptions Analysis { get; set; } = new();
    public double Threshold { get; set; } = .4;
    public double SceneThreshold { get; set; } = .55;
    public double SceneMargin { get; set; } = .03;
    public bool WriteTextReport { get; set; } = true;
    public bool OnlyLibrary { get; set; }
    public WordCandidate[] LibraryCandidates { get; set; } = [];

    public MediaTagTaskOptions Clone()
    {
        var copy = (MediaTagTaskOptions)MemberwiseClone();
        copy.Analysis = Analysis with { };
        copy.LibraryCandidates = LibraryCandidates.ToArray();
        return copy;
    }

    public void Validate()
    {
        Analysis.Validate();
        if (!double.IsFinite(Threshold) || Threshold is < .05 or > .95) throw new ArgumentException("标签阈值须为 0.05–0.95。");
        if (!double.IsFinite(SceneThreshold) || SceneThreshold is < .05 or > .95) throw new ArgumentException("场景相似度须为 0.05–0.95。");
        if (!double.IsFinite(SceneMargin) || SceneMargin is < 0 or > .5) throw new ArgumentException("场景分差须为 0–0.5。");
        if (OnlyLibrary && LibraryCandidates.Length == 0) throw new ArgumentException("仅显示所选词库时请先选择词库标签。");
        if (LibraryCandidates.Length > 0) WordLibraryCatalog.Validate(LibraryCandidates);
    }
}

/// <summary>Runs MediaTagService (+ optional captions) as one shared-queue job per media file.</summary>
public sealed class MediaTagJobService(IMediaEngine engine, ModelStore? models = null)
{
    private readonly ModelStore _models = models ?? new();

    public static void Validate(Job job)
    {
        if (job.Inputs.Length != 1 || !MediaTagService.Supports(job.Inputs[0]))
            throw new ArgumentException("每个 AI 标签任务处理一个图片或视频。");
        if (job.InputOptions is not null) throw new ArgumentException("AI 标签任务不支持逐文件编辑参数。");
        var spec = job.Options.MediaTag ?? throw new ArgumentException("缺少 AI 标签参数。");
        spec.Validate();
        if (job.Options.Start != 0 || job.Options.End != 0 || job.Options.Speed != 1 || MediaEngine.HasFilters(job.Options))
            throw new ArgumentException("AI 标签使用源文件时间轴，请先导出需要的剪辑区间。");
    }

    public async Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct)
    {
        Validate(job);
        var spec = job.Options.MediaTag!;
        var activity = new AiActivityReporter(value => job.Activity = value, "AI 标签", "个标签",
            spec.Analysis.GenerateCaptions
                ? ["准备标签模型", "识别媒体标签", "生成画面描述", "保存报告"]
                : ["准备标签模型", "识别媒体标签", "保存报告"]);
        activity.Stage("等待 AI 标签"); job.ProgressDetail = "等待 AI 标签"; progress(0);

        var report = new InlineProgress(update =>
        {
            if (update.Activity is { } snapshot)
            {
                job.Activity = snapshot;
                if (snapshot.Stage.Length > 0) job.ProgressDetail = snapshot.Stage;
                if (snapshot.Current is { } current && snapshot.Total is > 0 and var total)
                    progress(Math.Max(job.Progress, Math.Clamp(5 + 75 * current / total, 5, 80)));
                else progress(Math.Max(job.Progress, 8));
            }
            else if (update.PreviewResult is not null) progress(Math.Max(job.Progress, 40));
            else if (update.Result is not null) progress(85);
        });

        var results = await new MediaTagService(engine, _models)
            .AnalyzeAsync([job.Inputs[0]], spec.Analysis, report, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (results.Count == 0) throw new InvalidDataException("未能完成标签分析。");
        var result = results[0];
        activity.Node("保存报告");
        job.ProgressDetail = "保存报告"; progress(90);
        var labels = MediaTagText.QualifyingLabels(result, spec.Threshold, spec.SceneThreshold, spec.SceneMargin,
            spec.OnlyLibrary, spec.LibraryCandidates);
        activity.Result(labels.Count == 0 ? "未命中标签" : string.Join(" · ", labels.Take(8).Select(label => label.Label)), labels.Count);
        if (spec.WriteTextReport)
        {
            var reportPath = await MediaTagText.SaveAsync(result, labels, spec.Threshold, spec.SceneThreshold, spec.SceneMargin, ct)
                .ConfigureAwait(false);
            job.Output = reportPath;
            job.ProgressDetail = Path.GetFileName(reportPath);
        }
        else
        {
            // Persist a minimal machine-readable sidecar when TXT is disabled, still not touching source media.
            var folder = Path.GetDirectoryName(result.Path)!;
            var destination = MediaEngine.UniqueOutput(folder, Path.GetFileNameWithoutExtension(result.Path) + ".ai-tags", "json",
                [result.Path, job.Output]);
            await File.WriteAllTextAsync(destination, System.Text.Json.JsonSerializer.Serialize(new
            {
                result.Path, result.Backend, Labels = labels, result.RealPeopleOnly, result.Nsfw, result.Caption, result.CaptionModel, result.CaptionError, result.SceneError, result.SceneSkipped
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
            job.Output = destination;
            job.ProgressDetail = Path.GetFileName(destination);
        }
        if (result.SceneError is not null) job.ProgressDetail = (job.ProgressDetail.Length > 0 ? job.ProgressDetail + " · " : "")
            + (result.SceneSkipped ? "未下载语义模型，已跳过场景" : "语义识别失败");
        if (result.CaptionError is not null) job.ProgressDetail = (job.ProgressDetail.Length > 0 ? job.ProgressDetail + " · " : "") + "画面描述失败";
        activity.Finish("标签分析完成"); progress(100);
    }

    private sealed class InlineProgress(Action<MediaTagProgress> report) : IProgress<MediaTagProgress>
    { public void Report(MediaTagProgress value) => report(value); }
}
