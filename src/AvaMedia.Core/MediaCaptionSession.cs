namespace AvaMedia.Core;

/// <summary>One description session per analysis batch; local weights are loaded once and never downloaded here.</summary>
internal sealed class MediaCaptionSession(ModelStore store, OnlineAiSettings providers, MediaTagOptions options,
    Action<string> status, Action<string> backend) : IAsyncDisposable
{
    private LocalSummaryModel? _local;
    private OnlineAiOptions? _provider;

    public async Task<(string Caption, string Model)> GenerateAsync(IReadOnlyList<byte[]> frames, CancellationToken ct,
        IReadOnlyList<double>? frameSeconds = null, double videoDurationSeconds = 0, OnlineSummaryTool? frameTool = null)
    {
        if (options.CaptionLocalModelId is { } id)
        {
            if (_local is null)
            {
                if (!await store.IsInstalledAsync(id, ct: ct).ConfigureAwait(false)
                    || !await store.IsInstalledAsync(ModelCatalog.SummaryRuntimeId, ct: ct).ConfigureAwait(false))
                    throw new InvalidOperationException("请先在模型管理下载画面描述模型和本地推理工具。");
                _local = await LocalSummaryModel.StartAsync(store, id, options.PreferGpu, ct, status).ConfigureAwait(false);
            }
        }
        else _provider ??= MediaCaptionService.PrepareProvider(providers.Resolve(options.CaptionProviderId));
        backend(_local?.Backend ?? "线上 API");
        return await MediaCaptionService.GenerateAsync(_provider, frames, options, ct, _local,
            frameSeconds, videoDurationSeconds, frameTool,
            (current, total) => status($"描述采样画面 {current}/{total}")).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_local is not null) await _local.DisposeAsync().ConfigureAwait(false);
    }
}
