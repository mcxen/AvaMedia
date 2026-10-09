using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private readonly Button _advanced = new() { Content = "高级设置…" };
    private readonly CheckBox _realPeople = new() { Content = "真人素材", IsChecked = true, Name = "MediaAiRealPeople" };
    private readonly Button _chooseTagGroups = new() { Content = "标签组…" };
    private readonly Button _copy = new() { Content = "复制标签" };
    private readonly Button _export = new() { Content = "导出分析结果…" };
    private readonly CheckBox _selectAll = new() { Content = "全选" };
    private readonly CheckBox _showScores = new() { Content = "显示标签分数" };
    private readonly TextBlock _fileCount = Ui.Text("", "caption");
    private readonly TextBlock _empty = Ui.Text("拖入图片或视频", "caption");
    private readonly StackPanel _settingsPanel = new() { Spacing = 12 };
    private readonly Expander _batchActions = new() { Header = "批量操作", IsExpanded = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private bool _updatingSelection;
    private Window? _settingsOwner;
    private Control? _videoFramesSetting;
    private readonly Grid _workspaceBody = new() { ColumnDefinitions = new("248,*"), ColumnSpacing = 12 };
    private Control? _resultPane;

    private void BuildInterface()
    {
        ConfigureWorkbenchScrollbars();
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new(16), RowSpacing = 10 };
        _imports.Children.Add(Ui.Button("添加文件…", async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Localization.Text("选择图片或视频"), AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(Localization.Text("图片和视频"))
                { Patterns = VideoFormats.InputExtensions.Concat(new[] { "jpg", "jpeg", "png", "webp", "bmp", "tif", "tiff", "gif", "ico", "avif", "heic", "heif" }).Select(extension => "*." + extension).ToArray() }, FilePickerFileTypes.All] });
            AddPaths(files.Select(file => file.TryGetLocalPath()).OfType<string>());
        }));
        _imports.Children.Add(Ui.Button("添加文件夹…", async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new() { AllowMultiple = true });
            await AddFoldersAsync(folders.Select(folder => folder.TryGetLocalPath()).OfType<string>());
        }));
        _imports.Children.Add(Ui.Button("从列表移除", async () => await RemoveCheckedAsync()));
        foreach (var button in _imports.Children) button.Margin = new(0, 0, 6, 6);
        _imports.Margin = new(0, 0, 10, 0);
        var toolbar = new WrapPanel(); toolbar.Children.Add(_imports);
        var workbenchActions = new List<Control> { _analyze, _stop };
        if (_enqueue is not null) workbenchActions.Add(_enqueueQueue);
        if (_showQueue is not null) workbenchActions.Add(_viewQueue);
        workbenchActions.Add(_chooseTagGroups); workbenchActions.Add(_advanced);
        toolbar.Children.Add(WorkbenchActions(workbenchActions.ToArray())); root.Children.Add(toolbar);
        _list.ItemsSource = _entries;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _list.ItemTemplate = new FuncDataTemplate<MediaFileEntry>((entry, _) =>
        {
            if (entry is null) return new TextBlock();
            var row = new Grid { ColumnDefinitions = new("24,*"), ColumnSpacing = 6, Margin = new(0, 5) };
            var check = new CheckBox { VerticalAlignment = VerticalAlignment.Top };
            check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(MediaFileEntry.Include)) { Mode = BindingMode.TwoWay }); row.Children.Add(check);
            var content = new StackPanel { Spacing = 4 };
            var name = Ui.Text(""); name.FontWeight = FontWeight.SemiBold; name.TextTrimming = TextTrimming.CharacterEllipsis;
            Localization.SetIsUserText(name, true); name.Bind(TextBlock.TextProperty, new Binding(nameof(MediaFileEntry.Name))); content.Children.Add(name);
            var status = Ui.Text("", "caption"); status.Bind(TextBlock.TextProperty, new Binding(nameof(MediaFileEntry.Status))); content.Children.Add(status);
            var summary = Ui.Text("", "caption"); summary.MaxLines = 2; summary.TextWrapping = TextWrapping.Wrap; summary.TextTrimming = TextTrimming.CharacterEllipsis;
            Localization.SetIsUserText(summary, true); summary.Bind(TextBlock.TextProperty, new Binding(nameof(MediaFileEntry.Details))); content.Children.Add(summary);
            Grid.SetColumn(content, 1); row.Children.Add(content); return row;
        });
        var files = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 8 };
        var selection = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        selection.Children.Add(_selectAll); Grid.SetColumn(_fileCount, 1); _fileCount.HorizontalAlignment = HorizontalAlignment.Right; selection.Children.Add(_fileCount); files.Children.Add(selection);
        var fileBody = new Grid(); fileBody.Children.Add(_list);
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center; fileBody.Children.Add(_empty);
        Grid.SetRow(fileBody, 1); files.Children.Add(fileBody);
        _batchActions.Content = WorkbenchActions(_saveTxt, _export, _rename, _undo);
        Grid.SetRow(_batchActions, 2); files.Children.Add(_batchActions);
        var body = _workspaceBody;
        var filePanel = ChartPanel(files); filePanel.VerticalAlignment = VerticalAlignment.Stretch;
        body.Children.Add(filePanel); var result = BuildResultPane(); _resultPane = result; Grid.SetColumn(result, 1); body.Children.Add(result); Grid.SetRow(body, 1); root.Children.Add(body);
        Grid.SetRow(_activity, 2); root.Children.Add(_activity);
        var state = new StackPanel { Spacing = 3 }; state.Children.Add(_status); state.Children.Add(_modelStatus);
        state.Children.Add(WorkbenchActions(_warmStatus, _warmRetry));
        _warmStatus.IsVisible = false;
        _warmRetry.Click += async (_, _) => await PrepareModelsAsync(reset: true);
        Grid.SetRow(state, 3); root.Children.Add(state); Content = root;
        _activity.Update(null); _undo.IsVisible = CanUndo();
        _analyze.Click += async (_, _) => await AnalyzeAsync();
        _enqueueQueue.Click += async (_, _) => await EnqueueSelectedAsync();
        _viewQueue.Click += (_, _) => ShowQueuedTasks();
        _rename.Click += async (_, _) => await OpenRenameDialogAsync(); _undo.Click += async (_, _) => await RenameAsync(true);
        _stop.Click += (_, _) => _operation?.Cancel(); _export.Click += async (_, _) => await ExportAsync();
        _copy.Click += async (_, _) =>
        {
            if (_list.SelectedItem is not MediaFileEntry entry || !TryDisplayedResult(entry.Path, out var value) || Clipboard is null) return;
            try
            {
                await Clipboard.SetTextAsync(string.Join("，", ResultTags(value, search: true).Select(tag => tag.Label)));
                _status.Text = Localization.Text("标签已复制");
            }
            catch (Exception error) { await Ui.Message(this, "复制失败", error.Message); }
        };
        _advanced.Click += async (_, _) => await OpenAdvancedAsync();
        _chooseTagGroups.Click += async (_, _) => await OpenTagGroupsAsync();
        _selectAll.IsCheckedChanged += (_, _) =>
        {
            if (_updatingSelection) return;
            _updatingSelection = true;
            foreach (var entry in _entries) entry.Include = _selectAll.IsChecked == true;
            _updatingSelection = false; UpdateActions();
        };
        _list.SelectionChanged += async (_, _) => { RenderSelectedResult(); await RefreshSelectedPreviewAsync(); };
        _tagSearch.TextChanged += (_, _) => RenderSelectedResult();
        _videoFramesSetting = AddSettingRow("视频采样帧数", _frames); AddSettingRow("类别分差", _sceneMargin);
        _settingsPanel.Children.Add(_autoTxt);
        _settingsPanel.Children.Add(_librarySummary);
        _settingsPanel.Children.Add(_realPeople);
        _settingsPanel.Children.Add(_generateCaptions); _settingsPanel.Children.Add(_sceneTags); _settingsPanel.Children.Add(_gpu); _settingsPanel.Children.Add(_reuse); _settingsPanel.Children.Add(_recursive); _settingsPanel.Children.Add(_showScores); _settingsPanel.Children.Add(_onlyLibrary);
        ToolTip.SetTip(_generateCaptions, "使用 AI 供应商中配置的视觉模型。");
        _realPeople.IsCheckedChanged += async (_, _) => { if (!_closed && !_busy) { RefreshDisplayedResults(); await RefreshModelAsync(); } };
        _sceneTags.IsCheckedChanged += async (_, _) => { if (!_closed && !_busy) { RenderSelectedResult(); await RefreshModelAsync(); } };
        _gpu.IsCheckedChanged += async (_, _) => { if (!_closed && !_busy) await RefreshModelAsync(); };
        _showScores.IsCheckedChanged += (_, _) => RenderSelectedResult();
        _onlyLibrary.IsCheckedChanged += (_, _) => RefreshDisplayedResults();
        _settingsPanel.Children.Add(Ui.Button("模型管理…", async () => await ManageModelsAsync(_settingsOwner ?? this)));
    }
    private async Task RemoveCheckedAsync()
    {
        var count = _entries.Count(entry => entry.Include);
        if (count == 0 || _busy || _closed) return;
        if (!await Ui.Confirm(this, "从列表移除", Localization.Format($"从列表移除勾选的 {count} 个文件？这些文件的分析结果和手动编辑的标签将一并清除，源文件不会被删除。"), "从列表移除")
            || _busy || _closed) return;
        foreach (var entry in _entries.Where(entry => entry.Include).ToArray()) { _results.Remove(entry.Path); _liveResults.Remove(entry.Path); _traces.Remove(entry.Path); _positions.Remove(entry.Path); _reportSources.Remove(entry.Path); _editedTags.Remove(entry.Path); _entries.Remove(entry); }
        if (_list.SelectedItem is null) _list.SelectedItem = _entries.FirstOrDefault();
        RenderSelectedResult(); UpdateActions();
    }
    private void ConfigureWorkbenchScrollbars()
    {
        Resources["ScrollBarThickness"] = 10d;
        Styles.Add(new Style(selector => selector.OfType<ScrollBar>().Class(":vertical"))
        { Setters = { new Setter(ScrollBar.WidthProperty, 10d), new Setter(ScrollBar.MinWidthProperty, 10d) } });
        Styles.Add(new Style(selector => selector.OfType<ScrollBar>().Class(":vertical").Template().OfType<Thumb>().Name("thumb"))
        { Setters = { new Setter(Thumb.WidthProperty, 10d), new Setter(Thumb.MinWidthProperty, 0d) } });
        Styles.Add(new Style(selector => selector.OfType<ScrollBar>().Class(":vertical").Template().OfType<RepeatButton>().Class("repeat"))
        { Setters = { new Setter(RepeatButton.WidthProperty, 10d), new Setter(RepeatButton.HeightProperty, 10d) } });
    }
    private static WrapPanel WorkbenchActions(params Control[] controls)
    {
        var row = new WrapPanel();
        foreach (var control in controls)
        {
            control.Margin = new(0, 0, 6, 6); control.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(control);
        }
        return row;
    }
    private Control AddSettingRow(string label, Control control)
    {
        control=Ui.Parameter(control,label);
        var row = new Grid { ColumnDefinitions = new("140,*"), ColumnSpacing = 12 };
        row.Children.Add(Ui.Text(label)); Grid.SetColumn(control, 1); row.Children.Add(control); _settingsPanel.Children.Add(row);
        return row;
    }
    private async Task OpenAdvancedAsync()
    {
        if (_busy) return;
        var window = new Window { Title = "AI 标签 · 高级设置", Width = 480, Height = 520, MinWidth = 420, MinHeight = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 12, Margin = new(20) };
        var scroll = new ScrollViewer { Content = _settingsPanel }; root.Children.Add(scroll);
        var close = Ui.DialogButton("关闭", window.Close); close.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetRow(close, 1); root.Children.Add(close); window.Content = root;
        window.Closed += (_, _) =>
        {
            scroll.Content = null;
            ToolInputs.CommitNumber(_sceneMargin); ToolInputs.CommitNumber(_frames, integer: true);
            try { SavePreferences(); } catch (Exception error) { _status.Text = error.Message; }
        };
        _settingsOwner = window;
        try { await window.ShowDialog(this); }
        finally { _settingsOwner = null; }
        if (!_closed) RefreshDisplayedResults();
    }
    private void RefreshDisplayedResults()
    {
        foreach (var entry in _entries) if (_results.TryGetValue(entry.Path, out var result)) ShowResult(entry, result);
        RenderSelectedResult(); UpdateActions();
    }
    private void UpdateActions()
    {
        if (_updatingSelection) return;
        var included = _entries.Count(entry => entry.Include);
        var hasFiles = _entries.Count > 0;
        _workspaceBody.ColumnDefinitions[0].Width = hasFiles ? new GridLength(248) : new GridLength(1, GridUnitType.Star);
        _workspaceBody.ColumnDefinitions[1].Width = hasFiles ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _workspaceBody.ColumnSpacing = hasFiles ? 12 : 0;
        if (_resultPane is not null) _resultPane.IsVisible = hasFiles;
        _selectAll.IsVisible = _fileCount.IsVisible = hasFiles;
        var hasVideo = _entries.Any(entry => entry.Include && VideoFormats.IsVideo(entry.Path));
        if (_videoFramesSetting is not null) _videoFramesSetting.IsVisible = hasVideo;
        _reuse.IsVisible = hasVideo;
        _warmRetry.IsEnabled = !_busy && _warmRequest is null;
        _updatingSelection = true; _selectAll.IsChecked = _entries.Count > 0 && included == _entries.Count; _updatingSelection = false;
        _fileCount.Text = Localization.Format($"勾选 {included} / {_entries.Count}");
        _empty.IsVisible = _entries.Count == 0; _selectAll.IsEnabled = !_busy && _entries.Count > 0;
        _imports.IsEnabled = _advanced.IsEnabled = _chooseTagGroups.IsEnabled = !_busy && !_writingTxt;
        _analyze.IsEnabled = !_busy && !_writingTxt && included > 0;
        _enqueueQueue.IsEnabled = !_busy && !_writingTxt && included > 0 && _enqueue is not null;
        _viewQueue.IsEnabled = _showQueue is not null;
        _rename.IsEnabled = !_busy && !_writingTxt && _canRename() && _entries.Any(entry => entry.Include && _results.TryGetValue(entry.Path, out var result) && ResultTags(result).Any());
        _copy.IsVisible = _results.Count + _liveResults.Count > 0;
        _export.IsVisible = _rename.IsVisible = _results.Count > 0;
        _export.IsEnabled = !_busy && _entries.Any(entry => entry.Include && _results.ContainsKey(entry.Path)); _undo.IsEnabled = !_busy && !_writingTxt && _canRename();
        _copy.IsEnabled = _list.SelectedItem is MediaFileEntry selected && TryDisplayedResult(selected.Path, out var value) && ResultTags(value, search: true).Any();
        _saveTxt.IsEnabled = !_busy && !_writingTxt && _entries.Any(entry => entry.Include && _results.ContainsKey(entry.Path));
        _saveTxt.IsVisible = _results.Count > 0;
        _batchActions.IsVisible = _results.Count > 0 || _undo.IsVisible;
        _saveTxt.Content = Localization.Text(_writingTxt ? "正在生成 TXT…" : "生成同目录 TXT");
        // One primary call to action: Analyze, replaced in place by Stop while an analysis runs.
        _stop.IsVisible = _busy && _operation is not null; _analyze.IsVisible = !_stop.IsVisible;
    }
}
