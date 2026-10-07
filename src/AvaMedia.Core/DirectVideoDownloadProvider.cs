namespace AvaMedia.Core;

public sealed class DirectVideoDownloadProvider(AppSettings settings) : IVideoDownloadProvider
{
    private readonly YtDlpDownloadService _engine = new(settings, extractorKeys: "AvaMediaDirect");
    public string Name => "视频直链";
    public bool CanHandle(Uri url) => DownloadLinks.MediaExtension(url.AbsoluteUri).Length > 0;

    public Task<DownloadInspection> InspectAsync(string url, DownloadOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();options.Validate();
        return Task.FromResult(new DownloadInspection([Describe(url, browser: options.Browser)]));
    }

    public static DownloadVideo Describe(string url, string title = "", double duration = 0, BrowserMediaContext? browser = null)
    {
        url = DownloadLinks.Normalize(url);
        var name = Uri.UnescapeDataString(new Uri(url).Segments.LastOrDefault() ?? "");
        if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(title)) title = "视频";
        return new(url, name, title, "", double.IsFinite(duration) ? Math.Max(0, duration) : 0, "视频直链", Browser: browser);
    }

    public Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct) => _engine.ExecuteAsync(job, progress, ct);
}
