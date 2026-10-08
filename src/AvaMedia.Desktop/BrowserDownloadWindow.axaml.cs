using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class BrowserDownloadWindow : Window
{
    private readonly ObservableCollection<DownloadEntry> _entries = [];
    private readonly Dictionary<string, DownloadEntry> _media = new(StringComparer.Ordinal);
    private readonly HashSet<string> _discardedMedia = new(StringComparer.Ordinal);
    private readonly string _token = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly bool _single;
    private string _script = "", _pageUrl = "", _title = "", _agent = "";
    private string _encryptedPage = "";
    private bool _ready, _closed, _saving, _selecting, _scanning;

    public BrowserDownloadWindow() : this("", false) { }
    public BrowserDownloadWindow(string url, bool single)
    {
        InitializeComponent();
        _single = single;
        SelectAll.IsVisible = !single;
        AddressInput.Text = url;
        MediaList.ItemsSource = _entries;
        using var stream = typeof(BrowserDownloadWindow).Assembly.GetManifestResourceStream("AvaMedia.Desktop.MediaSniffer.js")!;
        using var reader = new StreamReader(stream);
        _script = reader.ReadToEnd().Replace("__AVAMEDIA_TOKEN__", _token, StringComparison.Ordinal);
        Browser.EnvironmentRequested += EnvironmentRequested;
        Browser.AdapterCreated += AdapterCreated;
        Browser.NavigationStarted += NavigationStarted;
        Browser.NavigationCompleted += NavigationCompleted;
        Browser.NewWindowRequested += NewWindowRequested;
        Browser.WebResourceRequested += ResourceRequested;
        Browser.WebMessageReceived += MessageReceived;
        _scanTimer.Tick += async (_, _) => await ScanAsync();
        Closed += (_, _) => { _closed = true; _scanTimer.Stop(); _lifetime.Cancel(); };
        AutomationProperties.SetName(AddressInput, "网站地址");
        AutomationProperties.SetName(MediaList, "嗅探到的视频");
        if (OperatingSystem.IsWindows() && !WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2).IsInstalled)
        {
            Browser.IsVisible = false;
            NavigateButton.IsEnabled = ReloadButton.IsEnabled = false;
            InstallRuntimeButton.IsVisible = true;
            BrowserStatus.Text = "需要安装 Microsoft Edge WebView2 Runtime。";
        }
        else if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            Browser.IsVisible = false;
            NavigateButton.IsEnabled = ReloadButton.IsEnabled = false;
            BrowserStatus.Text = "浏览器嗅探当前支持 macOS 和 Windows。";
        }
    }

    private void EnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        // A dedicated profile keeps this browser's login state separate from other applications.
        if (args is WindowsWebView2EnvironmentRequestedEventArgs windows)
            windows.UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "WebView", "Profile");
        // WKWebView's default persistent store is scoped to this application's bundle.
    }

    private async void AdapterCreated(object? sender, WebViewAdapterEventArgs args)
    {
        try
        {
            await WebViewDocumentScript.InstallAsync(args.TryGetPlatformHandle(), _script);
            if (_closed) return;
            _ready = true;
            _scanTimer.Start();
            if (!string.IsNullOrWhiteSpace(AddressInput.Text)) Navigate();
            else BrowserStatus.Text = "输入网站地址并打开视频";
        }
        catch (Exception ex) { if (!_closed) SetError(ex.Message); }
    }

    private void Navigate()
    {
        if (!_ready || _saving) return;
        try
        {
            var input = AddressInput.Text?.Trim() ?? "";
            if (!input.Contains("://", StringComparison.Ordinal)) input = "https://" + input;
            var url = DownloadLinks.NormalizePageUrl(input);
            _pageUrl = url;
            _encryptedPage = "";
            _media.Clear(); _discardedMedia.Clear(); _entries.Clear(); RefreshSelection();
            BrowserStatus.Text = "正在打开网站";
            Browser.Navigate(new Uri(url));
        }
        catch (Exception ex) { SetError(ex.Message); }
    }
    private void NavigateClick(object? sender, RoutedEventArgs e) => Navigate();
    private void AddressKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Navigate(); } }
    private void BackClick(object? sender, RoutedEventArgs e) { if (!_saving) Browser.GoBack(); }
    private void ForwardClick(object? sender, RoutedEventArgs e) { if (!_saving) Browser.GoForward(); }
    private void ReloadClick(object? sender, RoutedEventArgs e) { if (!_saving) Browser.Refresh(); }
    private void CancelClick(object? sender, RoutedEventArgs e) => Close();
    private void InstallRuntimeClick(object? sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/#download-section") { UseShellExecute = true }); }
        catch (Exception ex) { SetError(ex.Message); }
    }
    private void NavigationStarted(object? sender, WebViewNavigationStartingEventArgs args)
    {
        // Block external app schemes; frame navigations must not clear the main-page results.
        args.Cancel = args.Request?.Scheme is not ("http" or "https" or "about");
    }
    private async void NavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
    {
        if (_closed) return;
        BackButton.IsEnabled = Browser.CanGoBack;
        ForwardButton.IsEnabled = Browser.CanGoForward;
        if (args.Request?.Scheme is "http" or "https") AddressInput.Text = args.Request.AbsoluteUri;
        if (!args.IsSuccess) { SetError("网站未能打开，请刷新后重试。"); return; }
        await ScanAsync();
    }
    private void NewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (args.Request?.Scheme is "http" or "https") { AddressInput.Text = args.Request.AbsoluteUri; Dispatcher.UIThread.Post(Navigate); }
    }

    private async Task ScanAsync()
    {
        if (!_ready || _closed || _scanning || _saving) return;
        _scanning = true;
        try
        {
            // Reinstall after navigation if needed, then collect cached and delayed media requests.
            await Browser.InvokeScript(_script + "; window.__avaMediaScan?.();").WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception) { /* Navigation can replace the JS execution context. */ }
        finally { _scanning = false; }
    }

    private void ResourceRequested(object? sender, WebResourceRequestedEventArgs args)
    {
        // WKWebView exposes navigation requests here, including HTML pages named *.webm.
        // Its media subresources are confirmed by the injected script instead.
        if (OperatingSystem.IsMacOS()) return;
        if (_closed || _saving || args.Request.Method != System.Net.Http.HttpMethod.Get) return;
        var url = args.Request.Uri.AbsoluteUri;
        var extension = DownloadLinks.MediaExtension(url);
        if (extension.Length == 0 || extension is "ts" or "m2ts") return;
        string Header(string name) => args.Request.Headers.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? "";
        if (Header("Accept").Contains("text/html", StringComparison.OrdinalIgnoreCase)) return;
        AddMedia(url, extension, 0, Header("Referer"), Header("User-Agent"), Header("Origin"));
    }

    private void MessageReceived(object? sender, WebMessageReceivedEventArgs args)
    {
        if (_closed || _saving || args.Body is not { Length: > 0 and < 200000 }) return;
        try
        {
            using var document = JsonDocument.Parse(args.Body);
            var root = document.RootElement;
            if (Text(root, "token") != _token) return;
            var page = Text(root, "page");
            if (Uri.TryCreate(page, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                page = DownloadLinks.NormalizePageUrl(page);
                if (_pageUrl != page) { _entries.Clear(); _media.Clear(); _discardedMedia.Clear(); }
                _pageUrl = page;
                AddressInput.Text = uri.AbsoluteUri;
            }
            var title = Text(root, "title");
            if (title.Length > 0 && title != _title)
                foreach (var entry in _entries) entry.Complete(entry.Video! with { Title = title }, entry.IsChecked);
            _title = title; _agent = Text(root, "userAgent");
            if (root.TryGetProperty("discard", out var discard) && discard.ValueKind == JsonValueKind.Array)
                foreach (var item in discard.EnumerateArray().Take(1000))
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } fragment)
                    {
                        if (_discardedMedia.Count < 2000) _discardedMedia.Add(fragment);
                        if (_media.Remove(fragment, out var entry)) _entries.Remove(entry);
                    }
            if (_encryptedPage != _pageUrl) BrowserStatus.Text = "正在嗅探视频";
            if (root.TryGetProperty("encrypted", out var encrypted) && encrypted.ValueKind == JsonValueKind.True)
            {
                _encryptedPage = _pageUrl;
                _entries.Clear(); _media.Clear(); RefreshSelection();
                SetError("此页面包含 DRM 加密媒体，无法下载。"); return;
            }
            if (root.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
                foreach (var item in media.EnumerateArray().Take(100))
                {
                    var url = Text(item, "url");
                    var extension = DownloadLinks.MediaExtension(url, Text(item, "mime"));
                    AddMedia(url, extension, item.TryGetProperty("duration", out var d) && d.TryGetDouble(out var seconds) ? seconds : 0,
                        Text(item, "referer"), _agent, Text(item, "origin"));
                }
            RefreshSelection();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException) { /* Ignore unrelated or malformed website messages. */ }
    }

    private void AddMedia(string url, string extension, double duration, string referer, string agent, string origin)
    {
        if (extension.Length == 0 || extension is "ts" or "m2ts" || _pageUrl.Length == 0 || _encryptedPage == _pageUrl) return;
        try
        {
            url = DownloadLinks.Normalize(url);
            if (_discardedMedia.Contains(url)) return;
            var context = new WebViewMediaContext(url, _pageUrl, referer.Length > 0 ? referer : _pageUrl,
                agent.Length > 0 ? agent : _agent, origin, extension);
            context.Validate();
            var video = DirectVideoDownloadProvider.Describe(url, _title, duration) with
                { Platform = "内嵌浏览器", SourceUrl = _pageUrl, WebView = context };
            if (_media.TryGetValue(url, out var existing))
            {
                var old = existing.Video!;
                context = context with { Origin = context.Origin.Length > 0 ? context.Origin : old.WebView!.Origin };
                existing.Complete(video with { WebView = context, Duration = Math.Max(old.Duration, video.Duration) }, existing.IsChecked);
                return;
            }
            if (_entries.Count >= 100) return;
            var entry = new DownloadEntry(url);
            entry.Complete(video);
            entry.IsChecked = _entries.Count == 0;
            entry.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(DownloadEntry.IsChecked) || _selecting) return;
                if (_single && entry.IsChecked)
                {
                    _selecting = true;
                    foreach (var other in _entries.Where(other => other != entry)) other.IsChecked = false;
                    _selecting = false;
                }
                RefreshSelection();
            };
            _media.Add(url, entry); _entries.Add(entry); RefreshSelection();
        }
        catch (ArgumentException) { }
    }

    private void SelectAllChanged(object? sender, RoutedEventArgs e)
    {
        if (_selecting || _saving) return;
        _selecting = true;
        foreach (var entry in _entries) entry.IsChecked = SelectAll.IsChecked == true;
        _selecting = false; RefreshSelection();
    }
    private void RefreshSelection()
    {
        var selected = _entries.Count(entry => entry.IsChecked);
        Localization.SetText(MediaCount, $"发现 { _entries.Count } 个视频");
        EmptyMedia.IsVisible = _entries.Count == 0;
        UseMediaButton.IsEnabled = !_saving && selected > 0;
        _selecting = true;
        SelectAll.IsChecked = selected == 0 ? false : selected == _entries.Count ? true : null;
        _selecting = false;
    }
    private async void UseMediaClick(object? sender, RoutedEventArgs e)
    {
        if (_saving) return;
        var videos = _entries.Where(entry => entry.IsChecked).Select(entry => entry.Video!).ToArray();
        if (videos.Length == 0) return;
        _saving = true; RefreshSelection();
        Browser.IsEnabled = AddressInput.IsEnabled = NavigateButton.IsEnabled = ReloadButton.IsEnabled = MediaList.IsEnabled = SelectAll.IsEnabled = false;
        BrowserStatus.Text = "正在保存浏览器登录态";
        try
        {
            var cookies = Browser.TryGetCookieManager() ?? throw new InvalidOperationException("无法读取浏览器登录态，请重新打开浏览器。");
            var snapshot = await WebViewCookieStore.SaveAsync(await cookies.GetCookiesAsync().WaitAsync(TimeSpan.FromSeconds(10), _lifetime.Token), _lifetime.Token);
            if (!_closed) Close(videos.Select(video => video with { WebView = video.WebView! with { CookieSnapshotId = snapshot } }).ToArray());
            else File.Delete(WebViewCookieStore.PathFor(snapshot));
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _saving = false;
                Browser.IsEnabled = AddressInput.IsEnabled = NavigateButton.IsEnabled = ReloadButton.IsEnabled = MediaList.IsEnabled = SelectAll.IsEnabled = true;
                SetError(ex.Message); RefreshSelection();
            }
        }
    }
    private void SetError(string message) => BrowserStatus.Text = DownloadDiagnostics.Redact(message);
    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
