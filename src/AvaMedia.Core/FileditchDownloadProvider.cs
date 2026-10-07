namespace AvaMedia.Core;

public sealed class FileditchDownloadProvider(AppSettings settings) : IVideoDownloadProvider
{
    private readonly YtDlpDownloadService _engine = new(settings, extractorNames: "avamedia:direct");
    public string Name => "Fileditch";
    public bool CanHandle(Uri url) => DownloadLinks.IsFileditchPage(url.AbsoluteUri);

    public async Task<DownloadInspection> InspectAsync(string url, DownloadOptions options, CancellationToken ct = default)
    {
        url = DownloadLinks.Normalize(url);options.Validate();
        return await new BrowserVideoCapture().ResolveFileditchAsync(url, options.Browser?.Endpoint ?? options.CdpEndpoint, ct);
    }

    public async Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct)
    {
        var options = job.Options.Download ?? new();
        options.Validate();
        job.ProgressDetail = "正在解析 Fileditch 媒体地址";
        var inspection = await new BrowserVideoCapture().ResolveFileditchAsync(job.Inputs.Single(), options.Browser?.Endpoint ?? options.CdpEndpoint, ct, refresh: true);
        var video = inspection.Videos.Single();
        // Keep the stable player page in the queue and staging identity. Renew the CDN signature for every attempt.
        await _engine.ExecuteResolvedAsync(job, video.Url, options with { Browser = video.Browser }, progress, ct);
    }
}
