using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class ImageViewerWindow : Window
{
    private readonly ImageViewport _viewport = new();
    private readonly ListBox _files = new();
    private readonly TextBlock _zoomNotice = new() { Classes = { "viewer-zoom" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly DispatcherTimer _zoomNoticeTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly TextBlock _empty = Ui.Text("打开图片、文件夹或压缩包");
    private readonly StackPanel _metadata = new() { Spacing = 1 };
    private readonly Border _info = new() { IsVisible = false };
    private readonly TextBlock _infoSummary = new() { Classes = { "viewer-summary" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private bool _infoOpen;
    private readonly Grid _body = new() { ColumnDefinitions = new("0,*") };
    private readonly WrapPanel _toolbar = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _chrome = new();
    private readonly Button _fileActions = Ui.Button("文件操作", () => { });
    private readonly string _temporaryFolder = Path.Combine(Path.GetTempPath(), "AvaMedia", "viewer-" + Guid.NewGuid().ToString("N"));
    private readonly Button _animationButton = Ui.Button("暂停动画", () => { });
    private readonly Button _slideshowButton = Ui.Button("幻灯片", () => { });
    private readonly Button _bookmarkButton = Ui.Button("收藏", () => { });
    private readonly Button _mapButton = Ui.Button("地图", () => { });
    private bool _spread;
    private readonly CheckBox _recursive = new() { Content = "包含子文件夹" };
    private readonly ComboBox _fit = new() { Width = 110, ItemsSource = new[] { "适应窗口", "仅缩小", "适应宽度", "原始尺寸" }, SelectedIndex = 0 };
    private readonly ComboBox _interval = new() { Width = 78, ItemsSource = new[] { "2 秒", "5 秒", "10 秒", "30 秒" }, SelectedIndex = 1 };
    private readonly DispatcherTimer _animation = new();
    private readonly DispatcherTimer _slideshow = new();
    private readonly Storage _storage = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<IEnumerable<string>, IDisposable>? _reserveFiles;
    public event Action<IReadOnlyList<RenameItem>>? Moved;
    private CancellationTokenSource? _loading, _opening;
    private string[] _sources = [];
    private ImageViewerEntry[] _entries = [];
    private readonly HashSet<string> _removedEntries = new(BatchRename.PathComparer);
    private ImageViewerDocument? _document, _secondDocument;
    private readonly List<Bitmap> _bitmaps = [];
    private Bitmap? _secondBitmap;
    private int _index = -1, _frame, _loops, _loadGeneration, _openGeneration;
    private DateTime _loadedModified;
    private bool _closed, _bindingFiles, _animationPaused, _showBookmarks;
    private WindowState _beforeFullscreen;
    private GridLength _beforeLibrary;
    private Preferences _preferences;
    public sealed class Preferences
    {
        public ImageViewerEntry[] Bookmarks { get; set; } = [];
        public string[] Recent { get; set; } = [];
        public int Interval { get; set; } = 1;
        public bool Recursive { get; set; }
        public bool Random { get; set; }
        public bool Repeat { get; set; } = true;
        public bool RightToLeft { get; set; }
        public string Editor { get; set; } = "";
        public bool LockZoom { get; set; }
    }
    public ImageViewerWindow(IEnumerable<string>? initial = null, string? selected = null,
        Func<IEnumerable<string>, IDisposable>? reserveFiles = null)
    {
        _reserveFiles = reserveFiles;
        Avalonia.Media.RenderOptions.SetBitmapInterpolationMode(_viewport, BitmapInterpolationMode.HighQuality);
        Title = "天池看图"; Width = 1120; Height = 780; MinWidth = 760; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, "image");
        _preferences = _storage.LoadToolOptions<Preferences>("image-viewer") ?? new();
        _interval.SelectedIndex = Math.Clamp(_preferences.Interval, 0, 3); _recursive.IsChecked = _preferences.Recursive;
        BuildInterface(); WireEvents();
        Opened += async (_, _) =>
        {
            if (initial is not null) await GuardAsync(() => OpenAsync(initial, selected));
            _viewport.Focus();
        };
        Activated += async (_, _) =>
        {
            if (_loading is null && CurrentEntry is { } entry && File.Exists(entry.Container)
                && File.GetLastWriteTimeUtc(entry.Container) != _loadedModified) await LoadAsync(_index);
        };
        Closing += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); _loading?.Cancel(); _opening?.Cancel();
            _animation.Stop(); _slideshow.Stop(); ReleaseImages();
            _files.ItemsSource = null;
        };
        Closed += (_, _) => { _lifetime.Dispose(); };
    }
    private void BuildInterface()
    {
        ImageViewerToolbar.Configure(_slideshowButton, "play", "幻灯片");
        ImageViewerToolbar.Configure(_bookmarkButton, "star", "收藏");
        ImageViewerToolbar.Configure(_animationButton, "pause", "暂停动画");
        ImageViewerToolbar.Configure(_fileActions, "more", "文件操作");
        _fileActions.Click += (_, _) => ShowFileActions();
        var library = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 8, Margin = new(10) };
        library.Children.Add(_files); Grid.SetRow(_recursive, 1); library.Children.Add(_recursive);
        _files.ItemTemplate = new FuncDataTemplate<ImageViewerEntry>((entry, _) =>
            entry is null ? null : new ImageLibraryItem(entry, ReadLibraryThumbnailAsync));
        var libraryPane = new Border { Child = library, Classes = { "viewer-library" } };
        _body.Children.Add(libraryPane);
        var canvas = new Grid { ClipToBounds = true };
        canvas.Bind(BackgroundProperty, new DynamicResourceExtension("UiMediaSurface"));
        _empty.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiMediaText"));
        canvas.Children.Add(_viewport);
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center;
        canvas.Children.Add(_empty);
        BuildChrome(); BuildInfo();
        canvas.Children.Add(_zoomNotice); canvas.Children.Add(_info); canvas.Children.Add(_chrome);
        _zoomNoticeTimer.Tick += (_, _) => { _zoomNoticeTimer.Stop(); _zoomNotice.IsVisible = false; };
        Grid.SetColumn(canvas, 1); _body.Children.Add(canvas); Content = _body;
    }
    private void BuildChrome()
    {
        AddTool("previous", "上一张", () => Navigate(-1));
        AddTool("next", "下一张", () => Navigate(1));
        _toolbar.Children.Add(ImageViewerToolbar.Divider());
        AddTool("zoom-out", "缩小", () => _viewport.ZoomBy(1 / 1.2));
        AddTool("zoom-in", "放大", () => _viewport.ZoomBy(1.2));
        _fit.Width = 86; _fit.Height = _fit.MinHeight = 30; _fit.Margin = new(2, 0, 2, 0);
        _fit.Classes.Add("viewer-fit");
        _toolbar.Children.Add(_fit);
        AddTool("rotate-left", "左转", () => _viewport.Rotate(-90));
        AddTool("rotate-right", "右转", () => _viewport.Rotate(90));
        _toolbar.Children.Add(ImageViewerToolbar.Divider());
        _toolbar.Children.Add(_slideshowButton);
        AddTool("fullscreen", "全屏", ToggleFullscreen);
        var view = ImageViewerToolbar.Button("more", "查看", () => { });
        view.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            void AddView(string label, Action action, bool visible = true)
            {
                if (!visible) return;
                var item = new MenuItem { Header = label };
                item.Click += (_, _) => action(); menu.Items.Add(item);
            }
            AddView(ToolTip.GetTip(_bookmarkButton) as string ?? "收藏", ToggleBookmark);
            AddView(ToolTip.GetTip(_animationButton) as string ?? "暂停动画", () => _animationButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)), _animationButton.IsVisible);
            AddView("图片列表", ToggleLibrary);
            AddView("信息", ToggleInfo);
            menu.Open(view);
        };
        _toolbar.Children.Add(view);
        _toolbar.Children.Add(_fileActions);
        var bar = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center };
        bar.Children.Add(_toolbar);
        _chrome.Child = bar; _chrome.Classes.Add("viewer-chrome");
        _chrome.HorizontalAlignment = HorizontalAlignment.Center; _chrome.VerticalAlignment = VerticalAlignment.Bottom;
        _chrome.Margin = new(16, 0, 16, 16); _chrome.MaxWidth = 760;
    }
    private void BuildInfo()
    {
        var detail = new ScrollViewer
        {
            Content = _metadata, MaxHeight = 360, IsVisible = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var card = new StackPanel { Spacing = 6 };
        card.Children.Add(_infoSummary); card.Children.Add(detail); card.Children.Add(_mapButton);
        _mapButton.HorizontalAlignment = HorizontalAlignment.Left; _mapButton.IsVisible = false;
        ImageViewerToolbar.Configure(_mapButton, null, "地图"); _mapButton.Classes.Add("viewer-map");
        _info.Child = card; _info.Classes.Add("viewer-info");
        _info.HorizontalAlignment = HorizontalAlignment.Right; _info.VerticalAlignment = VerticalAlignment.Top;
        _info.Margin = new(16); _info.MaxWidth = 300;
        // The one-line summary stays up; the rest of EXIF only appears while the pointer is over the card.
        _info.PointerEntered += (_, _) => detail.IsVisible = _metadata.Children.Count > 0;
        _info.PointerExited += (_, _) => detail.IsVisible = false;
    }
    private void AddTool(string? kind, string label, Action action) => _toolbar.Children.Add(ImageViewerToolbar.Button(kind, label, action));
    private void AddTool(string? kind, string label, Func<Task> action) =>
        _toolbar.Children.Add(ImageViewerToolbar.Button(kind, label, () => _ = GuardAsync(action)));
    private void WireEvents()
    {
        _files.SelectionChanged += async (_, _) =>
        {
            if (_bindingFiles || _files.SelectedItem is not ImageViewerEntry entry) return;
            var index = Array.FindIndex(_entries, item => item.Identity == entry.Identity);
            if (index < 0) await GuardAsync(() => OpenAsync([entry.Container], entry.Member ?? entry.Container));
            else await LoadAsync(index);
        };
        _recursive.IsCheckedChanged += async (_, _) => { _preferences.Recursive = _recursive.IsChecked == true; SavePreferences(); if (_sources.Length > 0) await GuardAsync(() => OpenAsync(_sources, CurrentEntry?.Member ?? CurrentEntry?.Container)); };
        _fit.SelectionChanged += (_, _) => _viewport.Fit(_fit.SelectedIndex switch { 1 => "down", 2 => "width", 3 => "actual", _ => "fit" });
        _viewport.ViewChanged += RefreshStatus;
        _viewport.Zoomed += ShowZoom;
        _bookmarkButton.Click += (_, _) => ToggleBookmark();
        _animationButton.Click += (_, _) =>
        {
            _animationPaused = !_animationPaused;
            if (_animationPaused) _animation.Stop();
            else
            {
                if (_document is { LoopCount: > 0 } doc && _loops >= doc.LoopCount) { _loops = _frame = 0; SetViewportFrame(); }
                ScheduleFrame();
            }
            ImageViewerToolbar.SetAction(_animationButton, _animationPaused ? "play" : "pause", _animationPaused ? "播放动画" : "暂停动画");
        };
        _animation.Tick += (_, _) => AdvanceFrame();
        _slideshowButton.Click += (_, _) => ToggleSlideshow();
        _slideshow.Tick += (_, _) => Navigate(1, slideshow: true);
        _interval.SelectionChanged += (_, _) => { SetSlideshowInterval(); _preferences.Interval = _interval.SelectedIndex; SavePreferences(); };
        _mapButton.Click += (_, _) => { if (_document?.MapUrl is { } url) OpenExternal(url); };
        KeyDown += HandleKey;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = e.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        { e.Handled = true; await GuardAsync(() => OpenAsync(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? [])); });
    }
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) await Ui.Message(this, "天池看图", error.Message); }
    }
    private void SavePreferences() => _storage.SaveToolOptions("image-viewer", _preferences);
    private static void OpenExternal(string path) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
}
