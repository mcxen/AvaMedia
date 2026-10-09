using Avalonia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

/// <summary>The optional semantic model is never fetched implicitly; every UI path asks first and shows the catalog size.</summary>
internal static class SemanticModelConsent
{
    public static DownloadableModel Model => ModelCatalog.Find(ModelCatalog.EmbeddingId);
    public static long Megabytes(long bytes) => (long)Math.Ceiling(bytes / 1_000_000d);

    public static async Task<bool> IsInstalledAsync(CancellationToken ct = default)
        => await new ModelStore().IsInstalledAsync(ModelCatalog.EmbeddingId, ct: ct);

    /// <returns>true when the user agreed to download; false when declined or unsupported on this platform.</returns>
    public static async Task<bool> ConfirmAsync(Window owner, string feature)
    {
        var model = Model;
        if (!model.Supported) return false;
        var remaining = Math.Max(0, model.DownloadSize - new ModelStore().DownloadedBytes(model.Id));
        return await Ui.Confirm(owner, "需要语义模型",
            Localization.Format($"{Localization.Key(feature)}需要下载约 {Megabytes(remaining)} MB 的语义模型，是否下载？"), "下载");
    }

    public static Task DownloadAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
        => Task.Run(() => new ModelStore().DownloadAsync(ModelCatalog.EmbeddingId, progress, ct), ct);
}
