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
    public MediaRouteSource Source { get; } = source;
    public string Name => Path.GetFileName(Source.Path);
    public bool Include { get => _include; set => Set(ref _include, value); }
    public Bitmap? Preview => _preview;
    public bool HasPreview => _preview is not null;
    public bool NoPreview => !HasPreview;
    public long Bytes { get; private set; }
    internal bool Requested { get; set; }
    public string Caption => Localization.Join(" · ", new[] { KindName(Source.Kind), Bytes > 0 ? ImageCompression.Bytes(Bytes) : "", _detail }.Where(text => text.Length > 0));
    public string PreviewHint => _previewUnavailable ? Localization.Text("预览暂不可用，仍可选择工具。") : "";
    public static string KindName(MediaFileKind kind) => kind switch
    { MediaFileKind.Video => "视频", MediaFileKind.Image => "图片", MediaFileKind.Audio => "音频", MediaFileKind.Document => "文档", _ => "其他文件" };
    internal void Loaded(long bytes, string detail, Bitmap? preview, bool unavailable)
    {
        _preview?.Dispose(); _preview = preview; Bytes = bytes; _detail = detail; _previewUnavailable = unavailable; Refresh();
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
        RouteScroll.ScrollChanged += (_, _) => UpdateFlowTarget();
        LayoutUpdated += (_, _) => UpdateFlowTarget();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = e.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
        AddHandler(DragDrop.DropEvent, (_, e) => { e.Handled = true; AddFiles(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); });
        Localization.Changed += LanguageChanged;
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); Localization.Changed -= LanguageChanged;
            foreach (var entry in _entries) { entry.PropertyChanged -= EntryChanged; entry.DisposePreview(); }
        };
        AddFiles(files);
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        if (_closed) return;
        var previousCount = _entries.Count;
        var known = _entries.Select(entry => entry.Source.Path).ToHashSet(VideoFolderScanner.PathComparer);
        var kind = SelectionKinds[Math.Max(0, KindPicker.SelectedIndex)];
        _updating = true;
        try
        {
            foreach (var path in paths.Where(File.Exists).Select(Path.GetFullPath).Where(known.Add))
            {
                var entry = new MediaRouteEntry(new(path, _router.Classify(path)));
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
        var selected = _entries.Where(entry => entry.Include).Select(entry => entry.Source).ToArray();
        var routes = _router.Routes(selected);
        var previous = _active?.Feature.Id;
        _cards.Clear(); RouteGrid.Children.Clear(); RouteGrid.RowDefinitions.Clear();
        for (var index = 0; index < routes.Count; index++)
        {
            if (index % 2 == 0) RouteGrid.RowDefinitions.Add(new(GridLength.Auto));
            var route = routes[index]; var card = RouteCard(route);
            Grid.SetRow(card, index / 2); Grid.SetColumn(card, index % 2);
            _cards.Add((route, card)); RouteGrid.Children.Add(card);
        }
        _active = routes.FirstOrDefault(route => route.Feature.Id == previous) ?? routes.FirstOrDefault(route => route.Enabled);
        Localization.SetText(SourceCount, $"{selected.Length} / {_entries.Count} 项");
        Localization.SetText(RouteCount, $"{routes.Count(route => route.Enabled)} 个可用工具");
        EmptyText.IsVisible = routes.Count == 0; RouteFlow.InputCount = selected.Length;
        ActivateRoute(_active);
    }
    private Button RouteCard(MediaRouteOption route)
    {
        var button = new Button { Classes = { "route-card" }, IsEnabled = route.Enabled };
        var body = new Grid { ColumnDefinitions = new("42,*,16"), ColumnSpacing = 10 };
        var icon = new FeatureIcon { Kind = route.Feature.Icon, Label = route.Feature.Format.ToUpperInvariant(), Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(icon);
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var title = Ui.Text(route.Title); title.FontWeight = FontWeight.SemiBold; title.TextWrapping = TextWrapping.Wrap; labels.Children.Add(title);
        var description = Ui.Text(route.Enabled ? route.Description : route.DisabledReason, "caption"); description.TextWrapping = TextWrapping.Wrap; labels.Children.Add(description);
        labels.Children.Add(route.SkippedCount > 0 ? Ui.FormattedText($"接收 {route.Files.Length} 项 · 跳过 {route.SkippedCount} 项", "caption") : Ui.FormattedText($"接收 {route.Files.Length} 项", "caption"));
        Grid.SetColumn(labels, 1); body.Children.Add(labels);
        var arrow = Ui.Text("→"); arrow.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("UiFontTitle")); Grid.SetColumn(arrow, 2); body.Children.Add(arrow);
        button.Content = body;
        button.PointerEntered += (_, _) => ActivateRoute(route);
        button.GotFocus += (_, _) => ActivateRoute(route);
        button.Click += (_, _) => OpenRoute(route);
        ToolTip.SetTip(button, Localization.Text(route.Enabled ? route.Description : route.DisabledReason));
        return button;
    }
    private void ActivateRoute(MediaRouteOption? route)
    {
        _active = route;
        foreach (var card in _cards) card.Button.Classes.Set("active", card.Route == route);
        OpenButton.IsEnabled = route?.Enabled == true;
        RouteFlow.AcceptedCount = route?.Enabled == true ? route.Files.Length : 0;
        if (route is null)
        { DestinationTitle.Text = Localization.Text("先勾选文件"); DestinationSummary.Text = Localization.Text("每个工具会注明接收的文件数量。"); OpenButton.Content = Localization.Text("进入工具"); }
        else
        {
            Localization.SetText(DestinationTitle, $"将打开：{Localization.Key(route.Title)}");
            DestinationSummary.Text = route.Enabled ? route.SkippedCount > 0
                ? Localization.Format($"接收 {route.Files.Length} 项；其余 {route.SkippedCount} 项不进入此工具。")
                : Localization.Format($"所选 {route.Files.Length} 项将直接带入编辑窗口。") : Localization.Text(route.DisabledReason);
            OpenButton.Content = Localization.Format($"进入{Localization.Key(route.Title)}");
        }
        UpdateFlowTarget();
    }
    private void UpdateFlowTarget()
    {
        var card = _cards.FirstOrDefault(card => card.Route == _active).Button;
        RouteFlow.TargetY = card?.TranslatePoint(new(0, card.Bounds.Height / 2), RouteFlow)?.Y ?? double.NaN;
    }
    private void OpenRoute(MediaRouteOption route)
    { if (!_closed && route.Enabled) Close(new MediaRouteRequest(route.Feature.Id, route.Files)); }
    private async void AddClick(object? sender, RoutedEventArgs args) => AddFiles(await Ui.Pick(this, "添加到文件路由"));
    private void DeselectClick(object? sender, RoutedEventArgs args) => SetSelection(_ => false);
    private void CancelClick(object? sender, RoutedEventArgs args) => Close();
    private void OpenClick(object? sender, RoutedEventArgs args) { if (_active is { Enabled: true } route) OpenRoute(route); }

    private Control SourceCard(MediaRouteEntry entry)
    {
        var single = _entries.Count == 1;
        var grid = new Grid { ColumnDefinitions = new(single ? "*" : "96,*"), RowDefinitions = new(single ? "Auto,Auto,Auto,Auto" : "Auto,Auto,Auto"),
            ColumnSpacing = 10, RowSpacing = 5, Margin = new(0, 0, 0, 14) };
        var preview = new Grid();
        var image = new Image { Stretch = Stretch.Uniform };
        image.Bind(Image.SourceProperty, new Binding(nameof(MediaRouteEntry.Preview)));
        preview.Children.Add(image);
        var fallback = new FeatureIcon { Kind = entry.Source.Kind switch { MediaFileKind.Video => "video", MediaFileKind.Image => "image", MediaFileKind.Audio => "audio", _ => "document" },
            Label = Path.GetExtension(entry.Source.Path).TrimStart('.').ToUpperInvariant(), Width = single ? 72 : 40, Height = single ? 72 : 40,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        fallback.Bind(IsVisibleProperty, new Binding(nameof(MediaRouteEntry.NoPreview))); preview.Children.Add(fallback);
        var frame = new Border { Height = single ? 260 : 72, ClipToBounds = true, Child = preview };
        frame.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiSurfaceRaised"));
        frame.Bind(Border.CornerRadiusProperty, new DynamicResourceExtension("UiControlRadius"));
        if (!single) Grid.SetRowSpan(frame, 3); grid.Children.Add(frame);
        var name = new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold };
        Localization.SetIsUserText(name, true); ToolTip.SetTip(name, entry.Source.Path);
        var include = new CheckBox { Content = name, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        include.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(MediaRouteEntry.Include)) { Mode = BindingMode.TwoWay });
        Grid.SetRow(include, single ? 1 : 0); Grid.SetColumn(include, single ? 0 : 1); grid.Children.Add(include);
        var caption = new TextBlock { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
        caption.Bind(TextBlock.TextProperty, new Binding(nameof(MediaRouteEntry.Caption))); Grid.SetRow(caption, single ? 2 : 1); Grid.SetColumn(caption, single ? 0 : 1); grid.Children.Add(caption);
        var hint = new TextBlock { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
        hint.Bind(TextBlock.TextProperty, new Binding(nameof(MediaRouteEntry.PreviewHint))); Grid.SetRow(hint, single ? 3 : 2); Grid.SetColumn(hint, single ? 0 : 1); grid.Children.Add(hint);
        grid.DataContext = entry;
        grid.AttachedToVisualTree += (_, _) => { if (!entry.Requested) { entry.Requested = true; _ = LoadPreviewAsync(entry); } };
        return grid;
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
                try { preview = await Task.Run(() => { using var stream = File.OpenRead(entry.Source.Path); return Bitmap.DecodeToWidth(stream, 320); }, timeout.Token); }
                catch (Exception error) when (error is not OperationCanceledException)
                { using var stream = new MemoryStream(await _engine.Thumbnail(entry.Source.Path, 0, 320, 260, timeout.Token, false)); preview = new Bitmap(stream); }
            }
            else if (entry.Source.Kind == MediaFileKind.Video)
            {
                var info = await _engine.Probe(entry.Source.Path, timeout.Token);
                detail = $"{info.Width} × {info.Height} · {MediaTime.Format(info.Duration)}";
                using var stream = new MemoryStream(await _engine.Thumbnail(entry.Source.Path, Math.Min(1, Math.Max(0, info.Duration / 3)), 320, 180, timeout.Token));
                preview = new Bitmap(stream);
            }
        }
        catch (OperationCanceledException) { unavailable = true; }
        catch (Exception) { unavailable = true; }
        finally
        {
            if (acquired) _previewSlots.Release();
            if (!_closed) { entry.Loaded(bytes, detail, preview, unavailable); preview = null; }
            preview?.Dispose();
        }
    }
}
