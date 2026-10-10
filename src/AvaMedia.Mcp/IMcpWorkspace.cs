using AvaMedia.Core;

namespace AvaMedia.Mcp;

public sealed record McpTaskView(Guid Id, string FeatureId, string Name, string State, double Progress,
    string Detail, string Error, string[] Inputs, string Output, string SubmittedBy, string SubmissionId);
public sealed record McpSubmission(string RequestId, McpTaskView[] Tasks);
public sealed record McpTaskPage(McpTaskView[] Tasks, int Total, int Offset, int? NextOffset);
public sealed record McpTaskResult(McpTaskView Task, object? Result, int Offset = 0, int? NextOffset = null);

/// <summary>Implemented by the desktop owner; collection access and mutations run on its dispatcher.</summary>
public interface IMcpWorkspace
{
    Task<McpTaskPage> ListAsync(int offset, int limit, string? state, CancellationToken ct);
    Task<McpTaskResult> GetAsync(Guid id, bool includeResult, int offset, int limit, CancellationToken ct);
    Task<McpSubmission?> FindSubmissionAsync(string requestId, string hash, CancellationToken ct);
    Task<McpSubmission> SubmitAsync(string requestId, string hash, Func<string[], Job[]> create, bool start, CancellationToken ct);
    Task<McpTaskView> ControlAsync(Guid id, string action, CancellationToken ct);
    IMediaEngine Engine { get; }
}
