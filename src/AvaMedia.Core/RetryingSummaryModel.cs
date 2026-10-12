using System.Text.Json;

namespace AvaMedia.Core;

public sealed class SummaryOutputLimitException(string message) : IOException(message);
/// <summary>Only transport and transient status failures are retried; caller cancellation and content errors propagate.</summary>
public sealed class RetryingSummaryModel(ISummaryModel inner, Func<TimeSpan, CancellationToken, Task>? delay = null) : ISummaryModel
{
    public string ModelId => inner.ModelId;
    public string Backend => inner.Backend;
    public async Task<string> CompleteAsync(string system, string prompt, CancellationToken ct, byte[]? image = null,
        int tokens = 1024, JsonElement? schema = null, IReadOnlyList<SummaryModelImage>? images = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await inner.CompleteAsync(system, prompt, ct, image, tokens, schema, images); }
            catch (Exception error) when (attempt < 4 && !ct.IsCancellationRequested && Transient(error))
            {
                var retry = error is OnlineAiClient.ProviderHttpException http ? http.RetryAfter : null;
                var pause = retry ?? TimeSpan.FromSeconds((1 << attempt) + Random.Shared.NextDouble() * .25);
                await (delay?.Invoke(pause, ct) ?? Task.Delay(pause, ct));
            }
        }
    }
    private static bool Transient(Exception error) => error switch
    {
        OnlineAiClient.ProviderHttpException http => http.StatusCode == 429 || http.StatusCode >= 500,
        TimeoutException or HttpRequestException or TaskCanceledException => true,
        InvalidOperationException { InnerException: HttpRequestException or OperationCanceledException } => true,
        _ => false
    };
    // The owner controls the model lease; wrapping it must not release it prematurely.
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
