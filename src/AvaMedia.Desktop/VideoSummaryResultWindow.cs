using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class VideoSummaryResultWindow : Window
{
    private sealed class Page(string title, string text, string extension, Func<Control> create)
    {
        public string Title { get; } = title;
        public string Text { get; } = text;
        public string Extension { get; } = extension;
        public Func<Control> Create { get; } = create;
        public Control? View { get; set; }
    }
    private readonly string _folder;
    private readonly IMediaEngine? _engine;
    private readonly string? _source;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Bitmap> _images = [];
    private readonly List<Page> _pages = [];
    private readonly SemaphoreSlim _playGate = new(1, 1);
    private readonly ListBox _navigation = new() { Classes = { "summary-nav" } };
    private readonly ContentControl _body = new() { HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch };
    private readonly TextBlock _pageTitle = Ui.Text("视频总结结果", "summary-title");
    private readonly TextBlock _notice = Ui.Text("读取结果…", "caption");
    private readonly TextBlock _fileName = Ui.Text("", "title");
    private readonly TextBlock _metadata = Ui.Text("", "caption");
    private readonly Image _cover = new();
    private readonly TextBox _search = new() { Watermark = "搜索字幕…" };
    private Button _copy = null!, _export = null!, _play = null!;
    private readonly VideoSummaryReport? _initialReport;
    private VideoSummaryReport? _report;
    private PlayerWindow? _player;
    private bool _closed;
    private int _transcriptPage = -1;

    public VideoSummaryResultWindow(string folder, IMediaEngine? engine = null, string? source = null, VideoSummaryReport? report = null)
    {
        _folder = Path.GetFullPath(folder); _engine = engine; _source = source; _initialReport = report;
        Title = "视频总结结果"; Width = 1120; Height = 800; MinWidth = 820; MinHeight = 540;
        Classes.Add("video-summary-result"); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowArtwork.SetKind(this, "document"); Content = CreateLayout();
        Localization.SetIsUserText(_fileName, true);
        Opened += async (_, _) => await LoadAsync();
        _navigation.SelectionChanged += (_, _) => SelectPage();
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.F && (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && _transcriptPage >= 0)
            { _navigation.SelectedIndex = _transcriptPage; _search.Focus(); _search.SelectAll(); args.Handled = true; }
        };
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel();
            foreach (var image in _images) image.Dispose(); _images.Clear();
        };
    }

    private async Task LoadAsync()
    {
        try
        {
            var report = _initialReport;
            if (report is null)
            {
                var path = Path.Combine(_folder, "report.json");
                if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("结果超过 32 MB，请从输出目录打开。");
                var json = await File.ReadAllTextAsync(path, _lifetime.Token);
                report = await Task.Run(() => JsonSerializer.Deserialize<VideoSummaryReport>(json)
                    ?? throw new InvalidDataException("视频总结结果无效。"), _lifetime.Token);
            }
            var subtitle = Path.Combine(_folder, "subtitles.srt");
            if (_initialReport is null && File.Exists(subtitle))
            {
                if (new FileInfo(subtitle).Length > 32 * 1024 * 1024) throw new InvalidDataException("字幕超过 32 MB，请从输出目录打开。");
                var srt = await File.ReadAllTextAsync(subtitle, _lifetime.Token);
                report = report with { Transcript = await Task.Run(() => SubtitleTranscript.Parse(srt), _lifetime.Token) };
            }
            if (_closed) return;
            _report = report; _fileName.Text = report.Source; ToolTip.SetTip(_fileName, _source ?? report.Source);
            Localization.SetText(_metadata, $"{(report.IsPartial ? Localization.Text("部分结果") + " · " : "")}{MediaTime.Format(report.Duration)} · {report.Transcript.Count} 条字幕 · {report.Frames.Count} 个采样画面");
            _play.IsEnabled = _engine is not null && File.Exists(_source);
            BuildPages(report); _navigation.ItemsSource = _pages.Select(page => page.Title).ToArray();
            _navigation.SelectedIndex = 0; _copy.IsEnabled = _export.IsEnabled = true;
            _notice.Text = "";
            if (report.Frames.FirstOrDefault() is { } first) await LoadImageAsync(_cover, first.Image);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error) { if (!_closed) { ShowNotice(error.Message, true); _body.Content = Empty("无法读取结果", error.Message); } }
    }

    private void SelectPage()
    {
        var index = _navigation.SelectedIndex;
        if (index < 0 || index >= _pages.Count) return;
        var page = _pages[index]; _pageTitle.Text = page.Title;
        _body.Content = page.View ??= page.Create(); _notice.Text = "";
    }

    private void ShowNotice(string text, bool error = false)
    { Localization.SetIsUserText(_notice, true); _notice.Text = text; _notice.Classes.Set("error", error); }

    private async Task CopyAsync()
    {
        try
        {
            if (_navigation.SelectedIndex < 0 || Clipboard is not { } clipboard) return;
            await clipboard.SetTextAsync(_pages[_navigation.SelectedIndex].Text); ShowNotice(Localization.Text("已复制"));
        }
        catch (Exception error) { ShowNotice(error.Message, true); }
    }

    private async Task ExportAsync(string text, string extension, string suffix)
    {
        try
        {
            var target = await StorageProvider.SaveFilePickerAsync(new() { Title = Localization.Text("保存结果"),
                SuggestedFileName = Path.GetFileNameWithoutExtension(_report?.Source ?? "video") + "-" + suffix + "." + extension,
                DefaultExtension = extension });
            if (target?.TryGetLocalPath() is not { } path) return;
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), _lifetime.Token);
            ShowNotice(Localization.Text("已保存"));
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error) { ShowNotice(error.Message, true); }
    }

    private async Task PlayAsync(double seconds)
    {
        try
        {
            await _playGate.WaitAsync(_lifetime.Token);
            try
            {
                if (_closed) return;
                if (_engine is null || !File.Exists(_source)) { ShowNotice(Localization.Text("源视频不存在，无法回看。"), true); return; }
                var current = _player;
                if (current is null)
                {
                    current = new PlayerWindow(_engine); _player = current;
                    current.Closed += (_, _) => { if (ReferenceEquals(_player, current)) _player = null; };
                    current.ShowForPlayback(this);
                }
                if (!VideoFolderScanner.PathComparer.Equals(current.CurrentPath, Path.GetFullPath(_source!)))
                    await current.OpenAtAsync(_source!, seconds);
                else await current.SeekAsync(seconds, true);
                if (ReferenceEquals(_player, current) && !_closed) current.Activate();
            }
            finally { _playGate.Release(); }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error) { if (!_closed) ShowNotice(error.Message, true); }
    }

    private async Task LoadImageAsync(Image target, string relative)
    {
        try
        {
            if (!relative.StartsWith("frames/", StringComparison.Ordinal) || Path.GetExtension(relative) != ".png") return;
            var path = Path.GetFullPath(Path.Combine(_folder, relative));
            if (!path.StartsWith(_folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path) || new FileInfo(path).Length > 10 * 1024 * 1024) return;
            var image = await Task.Run(() => new Bitmap(path), _lifetime.Token);
            if (_closed) { image.Dispose(); return; }
            _images.Add(image); target.Source = image;
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error) { if (!_closed) ToolTip.SetTip(target, error.Message); }
    }
}
