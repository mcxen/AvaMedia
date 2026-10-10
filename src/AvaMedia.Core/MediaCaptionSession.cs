namespace AvaMedia.Core;

/// <summary>One description session per analysis batch; local weights are loaded once and never downloaded here.</summary>
internal sealed class MediaCaptionSession(ModelStore store, OnlineAiSettings providers, MediaTagOptions options,
    Action<string> status, Action<string> backend) : IAsyncDisposable
{
    private ISummaryModel? _model;

    public async Task<(string Caption, string Model)> GenerateAsync(IReadOnlyList<byte[]> frames, CancellationToken ct,
        IReadOnlyList<double>? frameSeconds = null, double videoDurationSeconds = 0, SummaryModelTool? frameTool = null)
    {
        if (_model is null)
        {
            _model = options.CaptionLocalModelId is { } id
                ? await LocalSummaryModelCache.AcquireAsync(store, id, ct, status).ConfigureAwait(false)
                : new OnlineSummaryModel(MediaCaptionService.PrepareProvider(providers.Resolve(options.CaptionProviderId)), vision: true);
        }
        backend(_model.Backend);
        return await MediaCaptionService.GenerateAsync(null, frames, options, ct, _model,
            frameSeconds, videoDurationSeconds, frameTool,
            (current, total) => status($"描述采样画面 {current}/{total}")).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_model is not null) await _model.DisposeAsync().ConfigureAwait(false);
    }
}
