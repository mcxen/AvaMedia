using System.Text.Json;

namespace AvaMedia.Core;

public interface ISummaryModel : IAsyncDisposable
{
    string Backend { get; }
    Task<string> CompleteAsync(string system, string prompt, CancellationToken ct, byte[]? image = null,
        int tokens = 1024, JsonElement? schema = null, IReadOnlyList<SummaryModelImage>? images = null);
}
