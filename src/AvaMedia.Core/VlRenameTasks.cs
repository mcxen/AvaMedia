using System.Text.Json.Serialization;

namespace AvaMedia.Core;

public sealed record VlRenameSource(string Path, string FolderContext, long Length, DateTime LastWriteUtc);
public sealed record VlRenameReviewItem(string Source, string? Name, bool Include);
public sealed record VlRenameTaskFile(VlRenameSource Source, VlMediaRenameSuggestion? Suggestion = null, string? Error = null)
{
    public bool Pending => Suggestion is null && Error is null;
}
public sealed record VlRenameTaskResult(VlRenameTaskFile[] Files, string Model, string Backend)
{
    public int Completed => Files.Count(file => file.Suggestion is not null);
    public int Failed => Files.Count(file => file.Error is not null);
    public bool IsPartial => Files.Any(file => file.Pending || file.Error is not null);
    public RenameItem[] Plan => Files.Select(file => file.Suggestion?.Item).OfType<RenameItem>().ToArray();
}
public sealed class VlRenameTaskOptions
{
    public VlRenameSource[] Sources { get; set; } = [];
    public string Mode { get; set; } = "local";
    public string ProviderId { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public string ProviderEndpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public Guid? RenameTaskId { get; set; }
    public VlRenameReviewItem[] Review { get; set; } = [];
    public VlRenameTaskOptions Clone() { var copy = (VlRenameTaskOptions)MemberwiseClone(); copy.Sources = Sources.ToArray(); copy.Review = Review.ToArray(); return copy; }
}
public sealed partial class ConversionOptions
{
    public VlRenameTaskOptions? VlRename { get; set; }
}
public partial class Job
{
    private VlRenameTaskResult? _vlRenameResult;
    [JsonIgnore] public VlRenameTaskResult? VlRenameResult
    { get => Volatile.Read(ref _vlRenameResult); set { Volatile.Write(ref _vlRenameResult, value); Raise(); } }
}

/// <summary>Persistent analysis only. Applying a reviewed plan remains a separate transactional queue task.</summary>
public sealed class VlRenameTasks(IMediaEngine engine)
{
    public static Job CreateAnalysis(AppSettings settings, IEnumerable<VlMediaRenameInput> inputs, string mode, string? providerId = null, string? model = null)
    {
        if (!settings.ExclusiveToolUnlocked) throw new InvalidOperationException("内部工具未激活。");
        var sources = inputs.Select(input =>
        {
            var file = new FileInfo(input.Path);
            if (!file.Exists) throw new FileNotFoundException("源文件不存在。", input.Path);
            return new VlRenameSource(file.FullName, input.FolderContext, file.Length, file.LastWriteTimeUtc);
        }).ToArray();
        var spec = new VlRenameTaskOptions { Sources = sources, Mode = mode, Model = mode == "rules" ? "rules" : ModelCatalog.DefaultSummaryModelId };
        if (mode == "online")
        {
            var provider = settings.OnlineAi.Resolve(providerId);
            if (string.IsNullOrWhiteSpace(model ?? provider.EffectiveVisionModel)) throw new ArgumentException("请在设置 → 在线 AI 中配置视觉模型。");
            provider.ValidateConnection();
            spec.ProviderId = provider.Id; spec.ProviderName = provider.Name; spec.ProviderEndpoint = provider.Endpoint; spec.Model = provider.EffectiveVisionModel;
        }
        if (model is not null) spec.Model = model;
        var job = new Job { FeatureId = "batch-rename", Inputs = sources.Select(source => source.Path).ToArray(),
            Options = new() { Format = "json", VlRename = spec } };
        job.Output = AiTaskResults.InternalOutputFor(job); Validate(job); return job;
    }

    public static Job CreateCommit(RenameItem[] plan)
    {
        BatchRename.ValidateRenamePlan(plan);
        var job = new Job { FeatureId = "batch-rename", Inputs = plan.Select(item => item.Source).ToArray(),
            Options = new() { Format = "json", Rename = new(plan.ToArray()) } };
        job.Output = AiTaskResults.PathFor(job, "rename-journal"); MediaEngine.Validate(job); return job;
    }

    public static void Validate(Job job)
    {
        var spec = job.Options.VlRename ?? throw new ArgumentException("缺少 VL 分析参数。");
        if (job.FeatureId != "batch-rename" || job.Options.Rename is not null || spec.Sources.Length == 0
            || spec.Mode is not ("local" or "online" or "rules")
            || !job.Inputs.SequenceEqual(spec.Sources.Select(source => source.Path), BatchRename.PathComparer)
            || job.Inputs.Distinct(BatchRename.PathComparer).Count() != job.Inputs.Length)
            throw new ArgumentException("VL 分析任务的文件或模式无效。");
        if (spec.Sources.Any(source => !Path.IsPathFullyQualified(source.Path) || source.Length < 0
            || !ImageFormats.Supports(source.Path) && !VideoFormats.IsVideo(source.Path)))
            throw new ArgumentException("VL 分析仅支持图片和视频的绝对路径。");
        if (spec.Mode == "online" && (!Guid.TryParseExact(spec.ProviderId, "N", out _)
            || string.IsNullOrWhiteSpace(spec.Model) || spec.Model.Length > 128 || spec.Model.Any(char.IsControl)))
            throw new ArgumentException("线上 VL 供应商或模型无效。");
        if (spec.Mode == "local" && !ModelCatalog.IsSummaryVision(spec.Model) || spec.Mode == "rules" && spec.Model != "rules")
            throw new ArgumentException("VL 分析模型与所选模式不一致。");
        if (!BatchRename.PathComparer.Equals(job.Output, AiTaskResults.InternalOutputFor(job)))
            throw new ArgumentException("VL 预览结果路径无效。");
    }

    public async Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct)
    {
        Validate(job);
        if (!engine.Settings.ExclusiveToolUnlocked) throw new InvalidOperationException("内部工具未激活。");
        var spec = job.Options.VlRename!;
        var saved = job.VlRenameResult ?? await AiTaskResults.LoadAsync<VlRenameTaskResult>(job.Output, ct).ConfigureAwait(false);
        var files = spec.Sources.Select(source => saved?.Files.FirstOrDefault(file => file.Source == source
            && file.Suggestion is { Item: var item } && BatchRename.PathComparer.Equals(item.Source, source.Path)
            && item.Length == source.Length && item.LastWriteUtc == source.LastWriteUtc)
            ?? new VlRenameTaskFile(source)).ToArray();
        var result = new VlRenameTaskResult(files, spec.Model, spec.Mode == "rules" ? "文件夹与文件名规则" : "");
        var activity = new AiActivityReporter(value => job.Activity = value, spec.Model, "个文件",
            ["准备视觉模型", "分析媒体", "保存预览"]);
        activity.Stage("准备 VL 命名分析");
        await SaveAsync().ConfigureAwait(false);
        ISummaryModel? model = null;
        try
        {
            if (files.Any(file => file.Pending) && spec.Mode == "local")
            {
                var store = new ModelStore();
                foreach (var id in new[] { ModelCatalog.SummaryRuntimeId, spec.Model })
                    if (!await store.IsInstalledAsync(id, ct: ct).ConfigureAwait(false))
                        await store.DownloadAsync(id, new DownloadProgress(value =>
                        {
                            job.ProgressDetail = ModelCatalog.Find(id).Name + " · " + value.Stage;
                            activity.Stage(job.ProgressDetail, value.Received, value.Total, "字节");
                        }), ct).ConfigureAwait(false);
                model = await SharedSummaryModelLease.AcquireAsync(store, spec.Model, ct,
                    stage => { job.ProgressDetail = stage; activity.Stage(stage); }).ConfigureAwait(false);
            }
            else if (files.Any(file => file.Pending) && spec.Mode == "online")
            {
                var provider = engine.Settings.OnlineAi.Resolve(spec.ProviderId).Clone();
                if (provider.Endpoint != spec.ProviderEndpoint) throw new InvalidOperationException("线上供应商地址已改变，请重新创建 VL 分析任务。");
                model = new OnlineSummaryModel(provider, vision: true, model: spec.Model);
            }
            result = result with { Backend = model?.Backend ?? saved?.Backend ?? result.Backend };
            activity.Node("分析媒体");
            activity.Backend(result.Backend);
            var renamer = new VlMediaRenamer(engine);
            for (var index = 0; index < files.Length; index++)
            {
                await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
                var source = files[index].Source;
                job.ProgressDetail = $"{index + 1}/{files.Length} · {Path.GetFileName(source.Path)}";
                activity.Stage("分析媒体", index + 1, files.Length, "个文件", detail: source.Path);
                try
                {
                    var info = new FileInfo(source.Path);
                    if (!info.Exists || info.Length != source.Length || info.LastWriteTimeUtc != source.LastWriteUtc)
                        throw new IOException("源文件已改变或缺失，请重新添加文件并分析。");
                    var suggestion = files[index].Suggestion;
                    if (suggestion is null)
                    {
                        using var boundary = JobExecutionControl.DeferPause();
                        suggestion = await renamer.SuggestAsync(source.Path, model, ct, source.FolderContext).ConfigureAwait(false);
                    }
                    files[index] = new(source, AvoidConflict(suggestion, files));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    files[index] = new(source, Error: error.Message);
                    job.AppendLog(source.Path + "\n" + error);
                }
                await SaveAsync().ConfigureAwait(false);
                progress(100d * (result.Completed + result.Failed) / files.Length);
            }
            activity.Node("保存预览");
            job.ProgressDetail = $"预览完成：成功 {result.Completed} 个，失败 {result.Failed} 个。";
            if (result.Failed > 0) throw new IOException(job.ProgressDetail + " 可查看已有结果并重试失败项。");
            activity.Finish("VL 命名预览已保存"); progress(100);
        }
        finally
        {
            try { await SaveAsync().ConfigureAwait(false); }
            finally { if (model is not null) await model.DisposeAsync().ConfigureAwait(false); }
        }

        async Task SaveAsync()
        {
            result = result with { Files = files.ToArray() };
            job.VlRenameResult = result; job.OutputIsPartial = result.IsPartial;
            await AiTaskResults.SaveAsync(job.Output, result, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static VlMediaRenameSuggestion AvoidConflict(VlMediaRenameSuggestion suggestion, VlRenameTaskFile[] files)
    {
        var directory = Path.GetDirectoryName(suggestion.Item.Source)!;
        var reserved = Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files.Select(file => file.Suggestion).OfType<VlMediaRenameSuggestion>())
            if (BatchRename.PathComparer.Equals(Path.GetDirectoryName(item.Item.Target), directory)
                && !BatchRename.PathComparer.Equals(item.Item.Source, suggestion.Item.Source)) reserved.Add(Path.GetFileName(item.Item.Target));
        var initial = Path.GetFileName(suggestion.Item.Target); var name = initial; var number = 2;
        if (!StringComparer.OrdinalIgnoreCase.Equals(name, Path.GetFileName(suggestion.Item.Source)))
            while (reserved.Contains(name)) name = VlMediaNaming.WithConflictNumber(initial, suggestion.Identifier, number++);
        return suggestion with { Item = suggestion.Item with { Target = Path.Combine(directory, name) } };
    }
    private sealed class DownloadProgress(Action<ModelDownloadProgress> report) : IProgress<ModelDownloadProgress>
    { public void Report(ModelDownloadProgress value) => report(value); }
}
