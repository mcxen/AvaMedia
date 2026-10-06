namespace AvaMedia.Core;

public interface IVideoDownloadProvider : IVideoDownloadService
{
    string Name { get; }
    bool CanHandle(Uri url);
    Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct);
}

public sealed class VideoDownloadService : IVideoDownloadService
{
    private readonly IReadOnlyList<IVideoDownloadProvider> _providers;

    public VideoDownloadService(AppSettings settings) : this([
        new FileHostDownloadProvider(settings, "Bunkr", "AvaMediaBunkr,AvaMediaBunkrAlbum"),
        new FileHostDownloadProvider(settings, "Pixeldrain", "AvaMediaPixeldrain,AvaMediaPixeldrainList"),
        new YtDlpDownloadService(settings)]) { }

    public VideoDownloadService(IEnumerable<IVideoDownloadProvider> providers)
    {
        _providers = providers.ToArray();
        if (_providers.Count == 0) throw new ArgumentException("至少需要一个下载服务。", nameof(providers));
    }

    public IVideoDownloadProvider Resolve(string url)
    {
        var uri = new Uri(DownloadLinks.Normalize(url));
        return _providers.FirstOrDefault(provider => provider.CanHandle(uri))
            ?? throw new InvalidOperationException("没有支持此链接的下载服务。");
    }

    public Task<DownloadInspection> InspectAsync(string url, DownloadOptions options, CancellationToken ct = default)
        => Resolve(url).InspectAsync(url, options, ct);

    public Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct)
        => Resolve(job.Inputs.Single()).ExecuteAsync(job, progress, ct);

    private sealed class FileHostDownloadProvider : IVideoDownloadProvider
    {
        private readonly YtDlpDownloadService _engine;
        public string Name { get; }

        public FileHostDownloadProvider(AppSettings settings, string name, string extractors)
        { Name = name; _engine = new YtDlpDownloadService(settings, extractorKeys: extractors); }

        public bool CanHandle(Uri url) => DownloadLinks.Platform(url.AbsoluteUri) == Name;
        public Task<DownloadInspection> InspectAsync(string url, DownloadOptions options, CancellationToken ct = default)
            => _engine.InspectAsync(url, options, ct);
        public Task ExecuteAsync(Job job, Action<double> progress, CancellationToken ct)
            => _engine.ExecuteAsync(job, progress, ct);
    }
}
