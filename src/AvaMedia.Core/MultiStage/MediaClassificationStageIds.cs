namespace AvaMedia.Core;

/// <summary>Observed work categories; classification revisits analysis, planning and writing per file.</summary>
public static class MediaClassificationStageIds
{
    public const string ScanFiles = "scan-files";
    public const string PrepareModel = "prepare-model";
    public const string AnalyzeItems = "analyze-items";
    public const string PlanClassification = "plan-classification";
    public const string WriteResults = "write-results";

    public static IReadOnlyList<TaskStageDescriptor> All { get; } = Array.AsReadOnly(new TaskStageDescriptor[]
    {
        new(ScanFiles, "检查文件", "校验输入、读取已有检查点并确认待处理文件"),
        new(PrepareModel, "准备模型", "下载并加载本次分析需要的模型"),
        new(AnalyzeItems, "逐项分析", "读取媒体信息、识别标签及可选画面描述和服装特征"),
        new(PlanClassification, "归类规划", "应用分类规则并按相似服装分组"),
        new(WriteResults, "写入结果", "保存分析报告或分类检查点")
    });

    public static IReadOnlyList<TaskStageDescriptor> For(Job job) => job.FeatureId == "media-ai" ? TagStages : All;
    private static readonly IReadOnlyList<TaskStageDescriptor> TagStages = Array.AsReadOnly(
        All.Where(stage => stage.Id != PlanClassification).ToArray());
}
