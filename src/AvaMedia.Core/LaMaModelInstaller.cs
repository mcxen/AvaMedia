namespace AvaMedia.Core;

/// <summary>LaMa uses the shared, hash-verified model store; YuNet remains embedded.</summary>
public sealed class LaMaModelInstaller(string? directory = null)
{
    public const string FileName = "inpainting_lama_2025jan.onnx";
    public const long FileSize = 92591623;
    public const string Sha256 = "7df918ac3921d3daf0aae1d219776cf0dc4e4935f035af81841b40adcf74fdf2";
    public const string HubUrl = "https://huggingface.co/opencv/inpainting_lama/resolve/main/" + FileName;
    public const string FallbackUrl = "https://lz.qaiu.top/parser?url=https://share.feijipan.com/s/zwwYN8FN";
    private readonly ModelStore _store = new(directory);
    public string ModelPath => _store.FileFor(ModelCatalog.LamaId, FileName);
    public Task<bool> IsInstalledAsync(CancellationToken ct = default) => _store.IsInstalledAsync(ModelCatalog.LamaId, true, ct);
    public async Task<string> EnsureInstalledAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await _store.DownloadAsync(ModelCatalog.LamaId, progress is null ? null : new Progress<ModelDownloadProgress>(value => progress.Report(value.Percent)), ct).ConfigureAwait(false);
        progress?.Report(100);
        return ModelPath;
    }
}
