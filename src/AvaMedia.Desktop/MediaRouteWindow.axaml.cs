using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed record MediaRouteRequest(string FeatureId, string[] Files);
public sealed class MediaRouteEntry(MediaRouteSource source) : Observable
{
    private bool _include = true;
    private Bitmap? _preview;
    private string _detail = "";
    private bool _previewUnavailable;
    private string _disposition = "";
    public MediaRouteSource Source { get; } = source;
    public string Name => Path.GetFileName(Source.Path);
    public bool Include { get => _include; set => Set(ref _include, value); }
    public Bitmap? Preview => _preview;
    public bool HasPreview => _preview is not null;
    public bool NoPreview => !HasPreview;
    public string Disposition => Localization.Text(_disposition);
    internal void SetDisposition(string value) => Set(ref _disposition, value, nameof(Disposition));
    public long Bytes { get; private set; }
    internal bool Requested { get; set; }
    public string Caption => Localization.Join(" · ", new[] { KindName(Source.Kind), Bytes > 0 ? ImageCompression.Bytes(Bytes) : "", _detail }.Where(text => text.Length > 0));
    public string PreviewHint => _previewUnavailable ? Localization.Text("预览暂不可用，仍可选择工具。") : "";
    public static string KindName(MediaFileKind kind) => kind switch
    { MediaFileKind.Video => "视频", MediaFileKind.Image => "图片", MediaFileKind.Audio => "音频", MediaFileKind.Document => "文档", _ => "其他文件" };
    internal void Loaded(long bytes, string detail, Bitmap? preview, bool unavailable)
    {
        var old = _preview; _preview = preview; Bytes = bytes; _detail = detail; _previewUnavailable = unavailable;
        try { Refresh(); }
        finally { old?.Dispose(); }
    }
    internal void Refresh() => Raise(string.Empty);
    internal void DisposePreview() { var old = _preview; _preview = null; Refresh(); old?.Dispose(); }
}

public partial class MediaRouteWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly IMediaFileRouter _router;
    private readonly ObservableCollection<MediaRouteEntry> _entries = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _previewSlots = new(2);
    private readonly List<(MediaRouteOption Route, Button Button)> _cards = [];
    private MediaRouteOption? _active;
    private bool _updating;
    private bool _closed;
    private MediaRouteRequest? _request;
    private static readonly MediaFileKind?[] SelectionKinds = [null, MediaFileKind.Video, MediaFileKind.Image, MediaFileKind.Audio, MediaFileKind.Document, MediaFileKind.Other];

    public MediaRouteWindow() : this(new MediaEngine(new()), []) { }
    public MediaRouteWindow(IMediaEngine engine, IEnumerable<string> files, IMediaFileRouter? router = null)
    {
        InitializeComponent(); _engine = engine; _router = router ?? new MediaFileRouter();
        WindowArtwork.SetKind(this, "split");
        SourceList.ItemsSource = _entries;
        SourceList.ItemTemplate = new FuncDataTemplate<MediaRouteEntry>((entry, _) => SourceCard(entry!));
        KindPicker.ItemsSource = new[] { "全部文件", "仅视频", "仅图片", "仅音频", "仅文档", "其他文件" };
        KindPicker.SelectedIndex = 0;
        KindPicker.SelectionChanged += (_, _) => SelectKind();
        ToolSearch.TextChanged += (_, _) => BuildRouteSections();
        ToolSearch.KeyDown += SearchKeyDown;
        AddHandler(KeyDownEvent, RoutingKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        RouteScroll.ScrollChanged += (_, _) => UpdateFlowTarget();
        SourceList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => UpdateFlowTarget());
        RouteScroll.SizeChanged += (_, _) => ArrangeRouteSections();
        RouteScroll.PropertyChanged += (_, change) => { if (change.Property == ScrollViewer.ViewportProperty) ArrangeRouteSections(); };
        LayoutUpdated += (_, _) => UpdateFlowTarget();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = e.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, (_, e) => { e.Handled = true; AddFiles(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); }, RoutingStrategies.Bubble, handledEventsToo: true);
        Localization.Changed += LanguageChanged;
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); Localization.Changed -= LanguageChanged;
            foreach (var entry in _entries) { entry.PropertyChanged -= EntryChanged; entry.DisposePreview(); }
        };
        AddFiles(files);
    }

    public async Task<MediaRouteRequest?> ShowForRoutingAsync(Window owner)
    {
        var completion = new TaskCompletionSource<MediaRouteRequest?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(object? sender, EventArgs args) => completion.TrySetResult(_request);
        Closed += OnClosed;
        try
        {
            // Keep the owner available so later drops into either window join this draft.
            Show(owner);
            return await completion.Task;
        }
        finally { Closed -= OnClosed; }
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        try { AddFilesCore(paths); }
        catch (Exception error)
        {
            AppDiagnostics.Record("Add routing files", error);
            if (!_closed) DestinationSummary.Text = Localization.Format($"导入失败：{error.Message}");
        }
    }
    private void AddFilesCore(IEnumerable<string> paths)
    {
        if (_closed) return;
        var previousCount = _entries.Count;
        var known = _entries.Select(entry => entry.Source.Path).ToHashSet(VideoFolderScanner.PathComparer);
        var kind = SelectionKinds[Math.Max(0, KindPicker.SelectedIndex)];
        // Normalize and classify the whole import before changing the live collection.
        var added = paths.Where(File.Exists).Select(Path.GetFullPath).Where(known.Add)
            .Select(path => new MediaRouteEntry(new(path, _router.Classify(path)))).ToArray();
        _updating = true;
        try
        {
            foreach (var entry in added)
            {
                entry.Include = kind is null || entry.Source.Kind == kind;
                entry.PropertyChanged += EntryChanged; _entries.Add(entry);
            }
        }
        finally { _updating = false; }
        if (previousCount != _entries.Count) { SourceList.ItemsSource = null; SourceList.ItemsSource = _entries; }
        RefreshRoutes();
    }
    private void EntryChanged(object? sender, PropertyChangedEventArgs args)
    { if (!_updating && args.PropertyName == nameof(MediaRouteEntry.Include)) RefreshRoutes(); }
    private void SelectKind()
    {
        if (KindPicker.SelectedIndex < 0) return;
        var kind = SelectionKinds[KindPicker.SelectedIndex];
        SetSelection(entry => kind is null || entry.Source.Kind == kind);
    }
    private void SetSelection(Func<MediaRouteEntry, bool> select)
    {
        _updating = true;
        try { foreach (var entry in _entries) entry.Include = select(entry); }
        finally { _updating = false; }
        RefreshRoutes();
    }
    private void LanguageChanged(object? sender, EventArgs args)
    { foreach (var entry in _entries) entry.Refresh(); RefreshRoutes(); }

    private void RefreshRoutes()
    {
        _selected = _entries.Where(entry => entry.Include).Select(entry => entry.Source).ToArray();
        _routes = _router.Routes(_selected);
        Localization.SetText(SourceCount, $"{_selected.Length} / {_entries.Count} 项");
        BuildRouteSections();
    }
    private void OpenRoute(MediaRouteOption route)
    { if (!_closed && route.Enabled) { _request = new(route.Feature.Id, route.Files); Close(_request); } }
    private async void AddClick(object? sender, RoutedEventArgs args)
    {
        try { AddFiles(await Ui.Pick(this, "添加到文件路由")); }
        catch (Exception error)
        {
            AppDiagnostics.Record("Pick routing files", error);
            if (!_closed) DestinationSummary.Text = Localization.Format($"导入失败：{error.Message}");
        }
    }
    private void DeselectClick(object? sender, RoutedEventArgs args) => SetSelection(_ => false);
    private void CancelClick(object? sender, RoutedEventArgs args) => Close();
    private void OpenClick(object? sender, RoutedEventArgs args) { if (_active is { Enabled: true } route) OpenRoute(route); }

    private Control SourceCard(MediaRouteEntry entry)
    {
        var single = _entries.Count == 1;
        var grid = new Grid { RowDefinitions = new(single ? "Auto,Auto,Auto,Auto" : "Auto,Auto,Auto") };
        grid.Bind(Grid.ColumnSpacingProperty, new DynamicResourceExtension("UiSpacingSmall"));
        if (single) grid.ColumnDefinitions.Add(new(1, GridUnitType.Star));
        else
        {
            var previewColumn = new ColumnDefinition();
            previewColumn.Bind(ColumnDefinition.WidthProperty, new DynamicResourceExtension("UiRouteThumbnailWidth"));
            grid.ColumnDefinitions.Add(previewColumn); grid.ColumnDefinitions.Add(new(1, GridUnitType.Star));
        }
        var preview = new Grid();
        var image = new Image { Stretch = Stretch.Uniform };
        image.Bind(Image.SourceProperty, new Binding(nameof(MediaRouteEntry.Preview))); preview.Children.Add(image);
        var fallback = new FeatureIcon { Kind = entry.Source.Kind switch { MediaFileKind.Video => "video", MediaFileKind.Image => "image", MediaFileKind.Audio => "audio", _ => "document" },
            Label = Path.GetExtension(entry.Source.Path).TrimStart('.').ToUpperInvariant(),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        fallback.Bind(WidthProperty, new DynamicResourceExtension(single ? "UiFeatureIconHeight" : "UiIconLarge"));
        fallback.Bind(HeightProperty, new DynamicResourceExtension(single ? "UiFeatureIconHeight" : "UiIconLarge"));
        fallback.Bind(IsVisibleProperty, new Binding(nameof(MediaRouteEntry.NoPreview))); preview.Children.Add(fallback);
        var frame = new Border { ClipToBounds = true, Child = preview };
        frame.Bind(HeightProperty, new DynamicResourceExtension(single ? "UiRoutePreviewHeight" : "UiRouteThumbnailHeight"));
        frame.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiSurfaceRaised"));
        frame.Bind(Border.CornerRadiusProperty, new DynamicResourceExtension("UiControlRadius"));
        if (!single) Grid.SetRowSpan(frame, 3); grid.Children.Add(frame);
        var name = new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold };
        Localization.SetIsUserText(name, true); ToolTip.SetTip(name, entry.Source.Path);
        var include = new CheckBox { Content = name, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        include.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(MediaRouteEntry.Include)) { Mode = BindingMode.TwoWay });
        var titleRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 4 }; titleRow.Children.Add(include);
        var disposition = Ui.Text("", "caption"); disposition.Classes.Add("route-disposition"); disposition.VerticalAlignment = VerticalAlignment.Center;
        disposition.Bind(TextBlock.TextProperty, new Binding(nameof(MediaRouteEntry.Disposition)));
        Grid.SetColumn(disposition, 1); titleRow.Children.Add(disposition);
        Grid.SetRow(titleRow, single ? 1 : 0); Grid.SetColumn(titleRow, single ? 0 : 1); grid.Children.Add(titleRow);
        var caption = new TextBlock { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
        caption.Bind(TextBlock.TextProperty, new Binding(nameof(MediaRouteEntry.Caption))); Grid.SetRow(caption, single ? 2 : 1); Grid.SetColumn(caption, single ? 0 : 1); grid.Children.Add(caption);
        var hint = new TextBlock { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
        hint.Bind(TextBlock.TextProperty, new Binding(nameof(MediaRouteEntry.PreviewHint))); Grid.SetRow(hint, single ? 3 : 2); Grid.SetColumn(hint, single ? 0 : 1); grid.Children.Add(hint);
        var row = new Border { Child = grid, Classes = { "route-source" }, DataContext = entry };
        row.AttachedToVisualTree += (_, _) =>
        {
            _sourceRows[entry] = (row, disposition); UpdateSourceFeedback();
            if (!entry.Requested) { entry.Requested = true; _ = ObservePreviewAsync(entry); }
        };
        row.DetachedFromVisualTree += (_, _) =>
        { if (_sourceRows.TryGetValue(entry, out var existing) && existing.Row == row) _sourceRows.Remove(entry); };
        return row;
    }
    private async Task ObservePreviewAsync(MediaRouteEntry entry)
    {
        try { await LoadPreviewAsync(entry); }
        catch (Exception error) { AppDiagnostics.Record("Apply routing preview", error); }
    }
    private async Task LoadPreviewAsync(MediaRouteEntry entry)
    {
        Bitmap? preview = null; long bytes = 0; var detail = ""; var unavailable = false; var acquired = false;
        try
        {
            await _previewSlots.WaitAsync(_lifetime.Token); acquired = true;
            bytes = await Task.Run(() => new FileInfo(entry.Source.Path).Length, _lifetime.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            if (entry.Source.Kind == MediaFileKind.Image)
            {
                var extension = Path.GetExtension(entry.Source.Path).ToLowerInvariant();
                if (extension is ".heic" or ".heif" || OperatingSystem.IsMacOS() && extension is ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff")
                {
                    var bytesPreview = await _engine.Thumbnail(entry.Source.Path, 0, 320, 260, timeout.Token, false);
                    preview = await Task.Run(() => { using var stream = new MemoryStream(bytesPreview); return new Bitmap(stream); }, timeout.Token);
                }
                else
                {
                    try { preview = await Task.Run(() => { using var stream = File.OpenRead(entry.Source.Path); return Bitmap.DecodeToWidth(stream, 320); }, timeout.Token); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        var bytesPreview = await _engine.Thumbnail(entry.Source.Path, 0, 320, 260, timeout.Token, false);
                        preview = await Task.Run(() => { using var stream = new MemoryStream(bytesPreview); return new Bitmap(stream); }, timeout.Token);
                    }
                }
            }
            else if (entry.Source.Kind == MediaFileKind.Video)
            {
                var info = await _engine.Probe(entry.Source.Path, timeout.Token);
                detail = $"{info.Width} × {info.Height} · {MediaTime.Format(info.Duration)}";
                var frame = await _engine.Thumbnail(entry.Source.Path, Math.Min(1, Math.Max(0, info.Duration / 3)), 320, 180, timeout.Token);
                preview = await Task.Run(() => { using var stream = new MemoryStream(frame); return new Bitmap(stream); }, timeout.Token);
            }
        }
        catch (OperationCanceledException) { unavailable = true; }
        catch (Exception error) { AppDiagnostics.Record("Load routing preview", error); unavailable = true; }
        finally
        {
            if (acquired) _previewSlots.Release();
            try
            {
                if (!_closed)
                {
                    // Ownership transfers before binding notifications, which can throw.
                    var owned = preview; preview = null;
                    entry.Loaded(bytes, detail, owned, unavailable);
                }
            }
            finally { preview?.Dispose(); }
        }
    }
}
