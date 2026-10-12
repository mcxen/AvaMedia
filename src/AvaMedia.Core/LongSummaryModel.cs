using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Give local length failures a typed meaning on the long path without changing short-video behavior.</summary>
internal sealed class LongSummaryModel(ISummaryModel inner, string identity) : ISummaryModel
{
    public string ModelId => identity;
    public string Backend => inner.Backend;
    public async Task<string> CompleteAsync(string system, string prompt, CancellationToken ct, byte[]? image = null,
        int tokens = 1024, JsonElement? schema = null, IReadOnlyList<SummaryModelImage>? images = null)
    {
        try { return await inner.CompleteAsync(system, prompt, ct, image, tokens, schema, images); }
        catch (InvalidDataException error) when (error.Message.Contains("总结超过输出长度", StringComparison.Ordinal))
        { throw new SummaryOutputLimitException(error.Message); }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
