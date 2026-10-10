using System.Text.Json;

namespace AvaMedia.Core;

public sealed record SummaryModelImage(string Label, byte[] Png);
public sealed record SummaryModelTool(string Name, string Description, JsonElement Parameters,
    Func<JsonElement, CancellationToken, Task<SummaryModelToolResult>> ExecuteAsync);
public sealed record SummaryModelToolResult(string Text, IReadOnlyList<SummaryModelImage>? Images = null);

public interface ISummaryModel : IAsyncDisposable
{
    string ModelId { get; }
    string Backend { get; }
    Task<string> CompleteAsync(string system, string prompt, CancellationToken ct, byte[]? image = null,
        int tokens = 1024, JsonElement? schema = null, IReadOnlyList<SummaryModelImage>? images = null);
}

public interface ISummaryToolModel : ISummaryModel
{
    Task<string> CompleteWithToolsAsync(string system, string prompt, IReadOnlyList<SummaryModelImage> images,
        IReadOnlyList<SummaryModelTool> tools, CancellationToken ct, int tokens = 2048);
}
