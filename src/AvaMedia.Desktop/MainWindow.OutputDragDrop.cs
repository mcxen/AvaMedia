using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private DispatcherTimer? _dropCategoryTimer;
    private string? _dropCategory;
    private bool _openingDroppedTool;
    private IDataTransfer? _dropTransfer;
    private MediaRouteSource[] _dropSources = [];
    private readonly Dictionary<string, string[]> _featureDropFiles = [];

    internal bool CanDragOutput(Job job) => !_closing && job.State == JobState.Completed && _jobs.Contains(job)
        && job.FeatureId != "folder-classification" && !job.HasInternalOutput
        && (File.Exists(job.Output) || Directory.Exists(job.Output));

    internal void SelectOutputForDrag(Job job)
    {
        // Keep an existing multi-selection intact when dragging one of its results.
        if (JobList.SelectedItems is not {} selected || selected.Contains(job)) return;
        selected.Clear(); selected.Add(job);
    }

    internal async Task DragOutputsAsync(Job job, PointerEventArgs e, Func<bool> beginDrag)
    {
        var selected = JobList.SelectedItems?.OfType<Job>().ToHashSet() ?? [];
        var outputs = (selected.Contains(job) ? _jobs.Where(selected.Contains) : [job])
            .Where(CanDragOutput).Select(item => item.Output).ToArray();
        List<IStorageFile> storageFiles = [];
        try
        {
            var paths = await Task.Run(() => ExpandOutputs(outputs));
            foreach (var path in paths)
                if (await StorageProvider.TryGetFileFromPathAsync(path) is {} file) storageFiles.Add(file);
            if (storageFiles.Count == 0)
            {
                if (!_closing) await Ui.Message(this, "输出文件", "输出文件缺失或不可访问。");
                return;
            }
            if (_closing || !beginDrag()) return;
            var transfer = new DataTransfer();
            foreach (var file in storageFiles) transfer.Add(DataTransferItem.CreateFile(file));
            // Chaining copies file references; it never moves or deletes the previous output.
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Copy);
        }
        catch (Exception error)
        {
            AppDiagnostics.Record("Drag task outputs", error);
            if (!_closing && IsVisible) await Ui.Message(this, "拖拽输出失败", error.Message);
        }
        finally
        {
            foreach (var file in storageFiles) file.Dispose();
            ResetOutputDropSession();
        }
    }

    private static string[] ExpandOutputs(IEnumerable<string> outputs)
    {
        List<string> files = [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var output in outputs)
        {
            if (File.Exists(output)) files.Add(Path.GetFullPath(output));
            else if (Directory.Exists(output))
                files.AddRange(Directory.EnumerateFiles(output, "*", options).Order(VideoFolderScanner.PathComparer));
        }
        return files.Distinct(VideoFolderScanner.PathComparer).ToArray();
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
        var indicator = new Border { Classes = { "tool-drop-indicator" }, BorderThickness = new(2),
            IsHitTestVisible = false };
        Grid.SetRowSpan(indicator, 2); content.Children.Add(indicator);
        DragDrop.SetAllowDrop(tile, true);
        tile.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var files = FeatureDropFiles(feature, e.DataTransfer);
            var accepted = CanDropOnFeature(feature, files);
            e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true;
            tile.Classes.Set("drop-target", accepted);
            tile.Classes.Set("drop-invalid", !accepted);

        }, RoutingStrategies.Bubble, handledEventsToo: true);
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

    private static void ClearFeatureDropFeedback(Button tile, Feature feature)
    {
        tile.Classes.Remove("drop-target"); tile.Classes.Remove("drop-invalid");

    }

    private void EnableCategoryDropNavigation(Controls.CategoryHeader header, string category)
    {
        if (_dropCategoryTimer is null)
        {
            _dropCategoryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _dropCategoryTimer.Tick += (_, _) =>
            {
                _dropCategoryTimer.Stop();
                if (!_closing && _dropCategory is {} target) ShowCategory(target);
            };
            AddHandler(DragDrop.DragLeaveEvent, (_, e) =>
            { if (ReferenceEquals(e.Source, this)) ResetOutputDropSession(); });
            Closed += (_, _) => ResetOutputDropSession();
        }
        DragDrop.SetAllowDrop(header, true);
        header.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DragDropEffects.None; e.Handled = true;
            if (_closing || _openingDroppedTool || !e.DataTransfer.Contains(DataFormat.File) || _dropCategory == category) return;
            _dropCategoryTimer.Stop(); _dropCategory = category;
            if (!header.IsExpanded) _dropCategoryTimer.Start();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        header.AddHandler(DragDrop.DragLeaveEvent, (_, _) => StopCategoryDropNavigation());
        header.AddHandler(DragDrop.DropEvent, (_, e) =>
        { e.DragEffects = DragDropEffects.None; e.Handled = true; ResetOutputDropSession(); }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void StopCategoryDropNavigation()
    { _dropCategoryTimer?.Stop(); _dropCategory = null; }

    private void ResetOutputDropSession()
    {
        StopCategoryDropNavigation();
        foreach (var (id, tile) in _featureButtons) ClearFeatureDropFeedback(tile, Catalog.Find(id));
        _dropTransfer = null; _dropSources = []; _featureDropFiles.Clear();
    }
}
