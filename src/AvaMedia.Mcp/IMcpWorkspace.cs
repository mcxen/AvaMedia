using AvaMedia.Core;

namespace AvaMedia.Mcp;

public sealed record McpTaskView(Guid Id, string FeatureId, string Name, string State, double Progress,
    string Detail, string Error, string[] Inputs, string Output, string SubmittedBy, string SubmissionId)
{
    public bool OutputIsPartial { get; init; }
    public McpActivityView? Activity { get; init; }
}
public sealed record McpActivityView(string Stage, string Model, string Backend, string Detail,
    double? Current, double? Total, string Unit, DateTime UpdatedUtc);
public sealed record McpRuntimeView(string Version, string Platform, bool Background, string OutputFolder,
    AiResourceSettings Resources, bool NsfwEnabled, bool BetaEnabled);
public sealed record McpResourceUpdate(int? CpuReservePercent = null, int? CpuThreads = null,
    bool? MultiThread = null);
public sealed record McpSubmission(string RequestId, McpTaskView[] Tasks);
public sealed record McpTaskPage(McpTaskView[] Tasks, int Total, int Offset, int? NextOffset)
{
    /// <summary>Unfiltered task count per state name plus "All"; lets clients show filter badges without extra calls.</summary>
    public IReadOnlyDictionary<string, int>? Counts { get; init; }
}
public sealed record McpTaskResult(McpTaskView Task, object? Result, int Offset = 0, int? NextOffset = null)
{
    public bool OutputIsDirectory { get; init; }
}

/// <summary>Implemented by the desktop owner; collection access and mutations run on its dispatcher.</summary>
public interface IMcpWorkspace
{
    Task<McpTaskPage> ListAsync(int offset, int limit, string? state, CancellationToken ct);
    Task<McpTaskResult> GetAsync(Guid id, bool includeResult, int offset, int limit, CancellationToken ct);
    Task<McpSubmission?> FindSubmissionAsync(string requestId, string hash, CancellationToken ct);
    Task<McpSubmission> SubmitAsync(string requestId, string hash, Func<string[], Job[]> create, bool start, CancellationToken ct);
    Task<McpTaskView> ControlAsync(Guid id, string action, CancellationToken ct);
    Task<McpRuntimeView> RuntimeAsync(McpResourceUpdate? update, CancellationToken ct);
    Task<string> LogAsync(Guid id, CancellationToken ct);
    Task<FolderClassifiedFile[]> ClassificationAsync(Guid id, CancellationToken ct);
    Task<FolderClassificationLive> ClassificationLiveAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    Task<AiTaskProgressDto> AiProgressAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    Task<SummaryEvidence?> SummaryEvidenceAsync(Guid id, string evidenceId, CancellationToken ct) => throw new NotSupportedException();
    Task<byte[]?> AiPreviewAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    Task<FolderClassificationTaskFile> ResolveClassificationAsync(Guid id, string path, string ruleId, string categoryId, CancellationToken ct) => throw new NotSupportedException();
    IMediaEngine Engine { get; }
}
