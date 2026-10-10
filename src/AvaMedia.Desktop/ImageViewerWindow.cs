using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
    private readonly TextBlock _status = Ui.Text("", "caption");
    private readonly TextBlock _empty = Ui.Text("打开图片、文件夹或压缩包");
    private readonly TextBox _search = Ui.Input("");
    private readonly StackPanel _metadata = new() { Spacing = 6 };
    private readonly Grid _body = new() { ColumnDefinitions = new("210,*,0"), ColumnSpacing = 8 };
    private readonly WrapPanel _toolbar = new();
    private readonly WrapPanel _navigation = new();
    private readonly Button _fileActions = Ui.Button("文件操作", () => { });
    private readonly string _temporaryFolder = Path.Combine(Path.GetTempPath(), "AvaMedia", "viewer-" + Guid.NewGuid().ToString("N"));
    private readonly Button _animationButton = Ui.Button("暂停动画", () => { });
    private readonly Button _slideshowButton = Ui.Button("幻灯片", () => { });
    private readonly Button _bookmarkButton = Ui.Button("收藏", () => { });
    private readonly Button _mapButton = Ui.Button("地图", () => { });
    private readonly CheckBox _spread = new() { Content = "双页" };
    private readonly CheckBox _recursive = new() { Content = "包含子文件夹" };
    private readonly ComboBox _fit = new() { Width = 110, ItemsSource = new[] { "适应窗口", "仅缩小", "适应宽度", "原始尺寸" }, SelectedIndex = 0 };
    private readonly ComboBox _interval = new() { Width = 78, ItemsSource = new[] { "2 秒", "5 秒", "10 秒", "30 秒" }, SelectedIndex = 1 };
    private readonly DispatcherTimer _animation = new();
    private readonly DispatcherTimer _slideshow = new();
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    private readonly Dictionary<string, byte[]> _thumbnailCache = [];
    private readonly Queue<string> _thumbnailOrder = [];
    private readonly Storage _storage = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _loading, _opening;
    private string[] _sources = [];
    private ImageViewerEntry[] _entries = [];
    private ImageViewerDocument? _document, _secondDocument;
    private readonly List<Bitmap> _bitmaps = [];
    private Bitmap? _secondBitmap;
    private int _index = -1, _frame, _loops, _loadGeneration, _openGeneration;
    private DateTime _loadedModified;
    private bool _closed, _bindingFiles, _animationPaused, _showBookmarks;
    private WindowState _beforeFullscreen;
    private GridLength _beforeLibrary, _beforeInfo;
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
    public ImageViewerWindow(IEnumerable<string>? initial = null, string? selected = null)
    {
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
            _files.ItemsSource = null; _thumbnailCache.Clear(); _thumbnailOrder.Clear();
        };
        Closed += (_, _) => { _lifetime.Dispose(); };
    }
    private void BuildInterface()
    {
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 8, Margin = new(10) };
        AddToolbar("打开…", PickAsync); AddToolbar("文件夹…", async () => { if (await Ui.Folder(this, "打开图片文件夹") is { } path) await OpenAsync([path]); });
        AddToolbar("最近", ShowRecentAsync); AddToolbar("收藏夹", () => { _showBookmarks = !_showBookmarks; RenderFiles(); return Task.CompletedTask; });
        _fileActions.Margin = new(0, 0, 6, 4); _toolbar.Children.Add(_fileActions);
        _fileActions.Click += (_, _) => ShowFileActions();
        AddToolbar("信息", () => { ToggleInfo(); return Task.CompletedTask; });
        AddToolbar("图片列表", () => { ToggleLibrary(); return Task.CompletedTask; });
        AddToolbar("全屏", () => { ToggleFullscreen(); return Task.CompletedTask; });
        AddToolbar("效果…", ShowEffectsAsync); AddToolbar("帮助", HelpAsync); root.Children.Add(_toolbar);
        _search.Watermark = Localization.Text("搜索图片"); Localization.SetIsUserText(_search, true);
        var library = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 6 };
        library.Children.Add(_search); Grid.SetRow(_files, 1); library.Children.Add(_files); Grid.SetRow(_recursive, 2); library.Children.Add(_recursive);
        _files.ItemTemplate = new FuncDataTemplate<ImageViewerEntry>((entry, _) =>
            entry is null ? null : new ImageLibraryItem(entry, ReadLibraryThumbnailAsync));
        _body.Children.Add(library);
        var canvas = new Grid { ClipToBounds = true };
        canvas.Bind(BackgroundProperty, new DynamicResourceExtension("UiSurface"));
        canvas.Children.Add(_viewport); _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center;
        canvas.Children.Add(_empty); Grid.SetColumn(canvas, 1); _body.Children.Add(canvas);
        var info = new ScrollViewer { Content = _metadata, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetColumn(info, 2); _body.Children.Add(info); Grid.SetRow(_body, 1); root.Children.Add(_body);
        var bottom = new StackPanel { Spacing = 5 };
        AddNavigation("上一张", () => Navigate(-1)); AddNavigation("下一张", () => Navigate(1));
        AddNavigation("−", () => _viewport.ZoomBy(1 / 1.2)); AddNavigation("+", () => _viewport.ZoomBy(1.2)); _navigation.Children.Add(_fit);
        AddNavigation("左转", () => _viewport.Rotate(-90)); AddNavigation("右转", () => _viewport.Rotate(90)); AddNavigation("镜像", _viewport.Flip);
        _navigation.Children.Add(_spread); _navigation.Children.Add(_bookmarkButton);
        _navigation.Children.Add(_animationButton); _navigation.Children.Add(_slideshowButton); _navigation.Children.Add(_interval);
        var menuButton = Ui.Button("播放设置", ShowPlaybackSettings); _navigation.Children.Add(menuButton);
        bottom.Children.Add(_navigation); bottom.Children.Add(_status); Grid.SetRow(bottom, 2); root.Children.Add(bottom); Content = root;
    }
    private void AddToolbar(string label, Func<Task> action)
    {
        var button = Ui.Button(label, async () => await GuardAsync(action)); button.Margin = new(0, 0, 6, 4); _toolbar.Children.Add(button);
    }
    private void AddNavigation(string label, Action action)
    {
        var button = Ui.Button(label, action); button.Margin = new(0, 0, 5, 0); _navigation.Children.Add(button);
    }
    private void WireEvents()
    {
        _files.SelectionChanged += async (_, _) =>
        {
            if (_bindingFiles || _files.SelectedItem is not ImageViewerEntry entry) return;
            var index = Array.FindIndex(_entries, item => item.Identity == entry.Identity);
            if (index < 0) await GuardAsync(() => OpenAsync([entry.Container], entry.Member ?? entry.Container));
            else await LoadAsync(index);
        };
        _search.TextChanged += (_, _) => RenderFiles();
        _recursive.IsCheckedChanged += async (_, _) => { _preferences.Recursive = _recursive.IsChecked == true; SavePreferences(); if (_sources.Length > 0) await GuardAsync(() => OpenAsync(_sources, CurrentEntry?.Member ?? CurrentEntry?.Container)); };
        _fit.SelectionChanged += (_, _) => _viewport.Fit(_fit.SelectedIndex switch { 1 => "down", 2 => "width", 3 => "actual", _ => "fit" });
        _viewport.ViewChanged += RefreshStatus;
        _spread.IsCheckedChanged += async (_, _) => { if (_index >= 0) await LoadAsync(_index); };
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
            _animationButton.Content = Localization.Text(_animationPaused ? "播放动画" : "暂停动画");
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
