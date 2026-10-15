using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private DispatcherTimer? _dropNavigationTimer;
    private Controls.CategoryHeader? _dropNavigationHeader;
    private Action? _dropNavigate;
    private bool _openingDroppedTool;
    private IDataTransfer? _dropTransfer;
    private MediaRouteSource[] _dropSources = [];
    private readonly Dictionary<string, string[]> _featureDropFiles = [];

    internal bool CanDragOutput(Job job) => !_closing && job.State == JobState.Completed && _jobs.Contains(job)
        && job.CompletionKind != JobCompletionKind.Unchanged
        && job.FeatureId != "folder-classification" && !job.HasInternalOutput
        && (File.Exists(job.Output) || Directory.Exists(job.Output));

    /// <summary>Input files can be dragged from a row in every state; online sources (download links) cannot.</summary>
    internal bool CanDragInput(Job job) => !_closing && _jobs.Contains(job) && job.FeatureId != "download"
        && job.Inputs.Any(path => File.Exists(path) || Directory.Exists(path));

    internal void SelectRowForDrag(Job job)
    {
        // Keep an existing multi-selection intact when dragging one of its rows.
        if (JobList.SelectedItems is not {} selected || selected.Contains(job)) return;
        selected.Clear(); selected.Add(job);
    }

    internal void SelectSingleRow(Job job)
    {
        if (JobList.SelectedItems is not {} selected || selected.Count == 1 && selected.Contains(job)) return;
        selected.Clear(); selected.Add(job);
    }

    private Job[] DragRows(Job job)
    {
        var selected = JobList.SelectedItems?.OfType<Job>().ToHashSet() ?? [];
        return selected.Contains(job) ? _jobs.Where(selected.Contains).ToArray() : [job];
    }

    internal Task DragOutputsAsync(Job job, PointerEventArgs e, FileDragVisual visual, Func<bool> beginDrag)
        => DragFilesAsync(e, DragRows(job).Where(CanDragOutput).Select(item => item.Output), visual, beginDrag,
            "输出文件缺失或不可访问。", "拖拽输出失败");

    internal Task DragInputsAsync(Job job, PointerEventArgs e, FileDragVisual visual, Func<bool> beginDrag)
        => DragFilesAsync(e, DragRows(job).Where(CanDragInput).SelectMany(item => item.Inputs), visual, beginDrag,
            "源文件缺失或不可访问。", "拖拽源文件失败");

    private async Task DragFilesAsync(PointerEventArgs e, IEnumerable<string> paths, FileDragVisual visual, Func<bool> beginDrag, string missing, string failed)
    {
        try
        {
            // Chaining copies file references; it never moves or deletes the dragged files.
            if (await FileDrag.RunAsync(this, e, paths, visual, () => !_closing && beginDrag()) == FileDragResult.Missing && !_closing)
                await Ui.Message(this, "拖拽文件", missing);
        }
        catch (Exception error)
        {
            AppDiagnostics.Record("Drag task files", error);
            if (!_closing && IsVisible) await Ui.Message(this, failed, error.Message);
        }
        finally { ResetOutputDropSession(); }
    }

    private string[] FeatureDropFiles(Feature feature, IDataTransfer transfer)
    {
        if (!ReferenceEquals(_dropTransfer, transfer))
        {
            _dropTransfer = transfer; _featureDropFiles.Clear();
            var router = new MediaFileRouter(_settings.EnableBetaFeatures);
            _dropSources = (transfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? [])
                .Distinct(VideoFolderScanner.PathComparer).Select(path => new MediaRouteSource(path, router.Classify(path))).ToArray();
        }
        if (_featureDropFiles.TryGetValue(feature.Id, out var files)) return files;
        var matcher = new MediaFileRouter(_settings.EnableBetaFeatures);
        files = _dropSources.Where(source => matcher.Accepts(feature, source)).Select(source => source.Path).ToArray();
        _featureDropFiles.Add(feature.Id, files);
        return files;
    }

    private bool CanDropOnFeature(Feature feature, string[] files) => !_closing && !_openingDroppedTool
        && files.Length > 0;

    private void EnableFeatureDrop(Button tile, Grid content, Feature feature)
    {
        // Solid accent outline over the whole tile (UISPEC 7.4.2): cancel the tile padding and follow its corner radius.
        var indicator = new Border { Classes = { "tool-drop-indicator" }, BorderThickness = new(2),
            IsHitTestVisible = false };
        void Fit() => indicator.Margin = new Thickness(-tile.Padding.Left, -tile.Padding.Top, -tile.Padding.Right, -tile.Padding.Bottom);
        Fit(); tile.PropertyChanged += (_, e) => { if (e.Property == Button.PaddingProperty) Fit(); };
        indicator.Bind(Border.CornerRadiusProperty, tile.GetObservable(Button.CornerRadiusProperty));
        Grid.SetRowSpan(indicator, Math.Max(2, content.RowDefinitions.Count)); Grid.SetColumnSpan(indicator, Math.Max(1, content.ColumnDefinitions.Count));
        content.Children.Add(indicator);
        DragDrop.SetAllowDrop(tile, true);
        void Feedback(object? sender, DragEventArgs e)
        {
            var files = FeatureDropFiles(feature, e.DataTransfer);
            var accepted = CanDropOnFeature(feature, files);
            e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
            tile.Classes.Set("drop-target", accepted);
            tile.Classes.Set("drop-invalid", !accepted);
            var name = FeatureNavigation.Label(feature).Replace("\n", " ");
            var label = name.StartsWith('→') ? Localization.Format($"转换为 {feature.Format.ToUpperInvariant()}") : Localization.Text(name);
            if (_closing || _openingDroppedTool) FileDrag.ClearHint(tile);
            else FileDrag.ShowHint(e, tile, accepted ? Localization.Format($"新建「{label}」任务 · {files.Length} 个文件")
                : Localization.Format($"「{label}」不支持这些文件"), !accepted);
        }
        tile.AddHandler(DragDrop.DragEnterEvent, Feedback, RoutingStrategies.Bubble, handledEventsToo: true);
        tile.AddHandler(DragDrop.DragOverEvent, Feedback, RoutingStrategies.Bubble, handledEventsToo: true);
        tile.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearFeatureDropFeedback(tile, feature));
        tile.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            e.Handled = true;
            var files = FeatureDropFiles(feature, e.DataTransfer).Where(File.Exists).ToArray();
            var skipped = _dropSources.Length - files.Length;
            var accepted = CanDropOnFeature(feature, files);
            e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
            ResetOutputDropSession();
            if (!accepted) return;
            _openingDroppedTool = true;
            // Finish the native drop before opening the tool window.
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (_closing) return;
                    if (skipped > 0) SummaryText.Text = Localization.Format($"带入 {files.Length} 个文件 · 跳过 {skipped} 个不支持的文件");
                    await Configure(feature, files);
                }
                catch (Exception error)
                {
                    AppDiagnostics.Record("Configure dropped outputs", error);
                    if (!_closing && IsVisible) await Ui.Message(this, "导入文件失败", error.Message);
                }
                finally { _openingDroppedTool = false; }
            });
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Design captures: show a tool tile's drop feedback and the drag ghost as if <paramref name="job"/>'s input were dragged onto it.</summary>
    internal void PreviewFeatureDrop(string? featureId, Job job, Avalonia.Media.IImage? thumbnail, int count)
    {
        if (count <= 0) { ResetOutputDropSession(); FileDrag.Preview(this, default, null, 0); return; }
        var router = new MediaFileRouter(_settings.EnableBetaFeatures);
        var source = new MediaRouteSource(job.Inputs.FirstOrDefault() ?? "", router.Classify(job.Inputs.FirstOrDefault() ?? ""));
        var (id, tile) = _featureButtons.FirstOrDefault(pair => (featureId is null || pair.Key == featureId) && pair.Value.IsEffectivelyVisible
            && pair.Value.Classes.Contains("feature-tree-row") && router.Accepts(Catalog.Find(pair.Key), source));
        if (tile is null) return;
        tile.Classes.Set("drop-target", true);
        var feature = Catalog.Find(id);
        var name = FeatureNavigation.Label(feature).Replace("\n", " ");
        var label = name.StartsWith('→') ? Localization.Format($"转换为 {feature.Format.ToUpperInvariant()}") : Localization.Text(name);
        var point = tile.TranslatePoint(new Point(tile.Bounds.Width * .6, tile.Bounds.Height / 2), this) ?? default;
        FileDrag.Preview(this, point, new FileDragVisual(thumbnail, FileDrag.MediaIcon(job.Inputs.FirstOrDefault() ?? "")), count,
            Localization.Format($"新建「{label}」任务 · {count} 个文件"));
    }

    private static void ClearFeatureDropFeedback(Button tile, Feature feature)
    {
        tile.Classes.Remove("drop-target"); tile.Classes.Remove("drop-invalid");
        FileDrag.ClearHint(tile);

    }

    private void EnableCategoryDropNavigation(Controls.CategoryHeader header, string category)
        => EnableDropNavigation(header, () => ShowCategory(category));

    private void EnableDropNavigation(Controls.CategoryHeader header, Action expand)
    {
        if (_dropNavigationTimer is null)
        {
            _dropNavigationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _dropNavigationTimer.Tick += (_, _) =>
            {
                _dropNavigationTimer.Stop();
                if (!_closing) _dropNavigate?.Invoke();
            };
            AddHandler(DragDrop.DragLeaveEvent, (_, e) =>
            { if (ReferenceEquals(e.Source, this)) ResetOutputDropSession(); });
            Closed += (_, _) => ResetOutputDropSession();
        }
        DragDrop.SetAllowDrop(header, true);
        header.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DragDropEffects.None; e.Handled = true;
            if (_closing || _openingDroppedTool || !e.DataTransfer.Contains(DataFormat.File) || _dropNavigationHeader == header) return;
            _dropNavigationTimer.Stop(); _dropNavigationHeader = header; _dropNavigate = expand;
            if (!header.IsExpanded) _dropNavigationTimer.Start();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        header.AddHandler(DragDrop.DragLeaveEvent, (_, _) =>
        { if (_dropNavigationHeader == header) StopDropNavigation(); });
        header.AddHandler(DragDrop.DropEvent, (_, e) =>
        { e.DragEffects = DragDropEffects.None; e.Handled = true; ResetOutputDropSession(); }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void StopDropNavigation()
    { _dropNavigationTimer?.Stop(); _dropNavigationHeader = null; _dropNavigate = null; }

    private void ResetOutputDropSession()
    {
        StopDropNavigation();
        foreach (var (id, tile) in _featureButtons)
            if (id != FeatureNavigation.ExclusiveRenamer.Id) ClearFeatureDropFeedback(tile, Catalog.Find(id));
        _dropTransfer = null; _dropSources = []; _featureDropFiles.Clear();
    }
}
