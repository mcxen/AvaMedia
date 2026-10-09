using Avalonia;
using Avalonia.Controls;
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
    private readonly Button _copy = new() { Content = "复制标签" };
    private readonly Button _export = new() { Content = "导出分析结果…" };
    private readonly CheckBox _selectAll = new() { Content = "全选" };
    private readonly CheckBox _showScores = new() { Content = "显示标签分数" };
    private readonly TextBlock _fileCount = Ui.Text("", "caption");
    private readonly TextBlock _empty = Ui.Text("拖入图片或视频", "caption");
    private readonly StackPanel _settingsPanel = new() { Spacing = 12 };
    private bool _updatingSelection;
    private Window? _settingsOwner;

    private void BuildInterface()
    {
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new(20), RowSpacing = 12 };
        var toolbar = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
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
        _imports.Children.Add(Ui.Button("移除勾选", () =>
        {
            foreach (var entry in _entries.Where(entry => entry.Include).ToArray()) { _results.Remove(entry.Path); _entries.Remove(entry); }
            if (_list.SelectedItem is null) _list.SelectedItem = _entries.FirstOrDefault();
            RenderSelectedResult(); UpdateActions();
        }));
        toolbar.Children.Add(_imports); Grid.SetColumn(_advanced, 1); toolbar.Children.Add(_advanced); root.Children.Add(toolbar);
        _list.ItemsSource = _entries;
        _list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _list.ItemTemplate = new FuncDataTemplate<MediaFileEntry>((entry, _) =>
        {
            if (entry is null) return new TextBlock();
            var row = new Grid { ColumnDefinitions = new("28,*"), ColumnSpacing = 8, Margin = new(0, 7) };
            var check = new CheckBox { VerticalAlignment = VerticalAlignment.Top };
            check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(MediaFileEntry.Include)) { Mode = BindingMode.TwoWay }); row.Children.Add(check);
            var content = new StackPanel { Spacing = 4 };
            var name = Ui.Text(""); name.FontWeight = FontWeight.SemiBold; name.TextTrimming = TextTrimming.CharacterEllipsis;
            Localization.SetIsUserText(name, true); name.Bind(TextBlock.TextProperty, new Binding(nameof(MediaFileEntry.Name))); content.Children.Add(name);
            var status = Ui.Text("", "caption"); status.Bind(TextBlock.TextProperty, new Binding(nameof(MediaFileEntry.Status))); content.Children.Add(status);
            var summary = Ui.Text("", "caption"); summary.MaxLines = 2; summary.TextTrimming = TextTrimming.CharacterEllipsis;
            Localization.SetIsUserText(summary, true); summary.Bind(TextBlock.TextProperty, new Binding(nameof(MediaFileEntry.Details))); content.Children.Add(summary);
            Grid.SetColumn(content, 1); row.Children.Add(content); return row;
        });
        var files = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8 };
        var selection = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        selection.Children.Add(_selectAll); Grid.SetColumn(_fileCount, 1); _fileCount.HorizontalAlignment = HorizontalAlignment.Right; selection.Children.Add(_fileCount); files.Children.Add(selection);
        var fileBody = new Grid(); fileBody.Children.Add(_list);
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center; fileBody.Children.Add(_empty);
        Grid.SetRow(fileBody, 1); files.Children.Add(fileBody);
        var body = new Grid { ColumnDefinitions = new("300,*"), ColumnSpacing = 18 };
        body.Children.Add(files); var result = BuildResultPane(); Grid.SetColumn(result, 1); body.Children.Add(result); Grid.SetRow(body, 1); root.Children.Add(body);
        Grid.SetRow(_activity, 2); root.Children.Add(_activity);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        var state = new StackPanel { Spacing = 4 }; state.Children.Add(_status); state.Children.Add(_modelStatus); footer.Children.Add(state);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_copy); actions.Children.Add(_export); actions.Children.Add(_rename); actions.Children.Add(_undo); actions.Children.Add(_stop); actions.Children.Add(_analyze);
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        _activity.Update(null); _undo.IsVisible = CanUndo();
        _analyze.Click += async (_, _) => await AnalyzeAsync();
        _rename.Click += async (_, _) => await OpenRenameDialogAsync(); _undo.Click += async (_, _) => await RenameAsync(true);
        _stop.Click += (_, _) => _operation?.Cancel(); _export.Click += async (_, _) => await ExportAsync();
        _copy.Click += async (_, _) =>
        {
            if (_list.SelectedItem is not MediaFileEntry entry || !_results.TryGetValue(entry.Path, out var value) || Clipboard is null) return;
            try
            {
                await Clipboard.SetTextAsync(string.Join("，", ResultTags(value, search: true).Select(tag => tag.Label)));
                _status.Text = Localization.Text("标签已复制");
            }
            catch (Exception error) { await Ui.Message(this, "复制失败", error.Message); }
        };
        _advanced.Click += async (_, _) => await OpenAdvancedAsync();
        _selectAll.IsCheckedChanged += (_, _) =>
        {
            if (_updatingSelection) return;
            _updatingSelection = true;
            foreach (var entry in _entries) entry.Include = _selectAll.IsChecked == true;
            _updatingSelection = false; UpdateActions();
        };
        _list.SelectionChanged += async (_, _) => { RenderSelectedResult(); await RefreshSelectedPreviewAsync(); };
        _tagSearch.TextChanged += (_, _) => RenderSelectedResult();
        AddSettingRow("标签阈值", _threshold); AddSettingRow("视频采样帧数", _frames);
        _settingsPanel.Children.Add(_gpu); _settingsPanel.Children.Add(_reuse); _settingsPanel.Children.Add(_recursive); _settingsPanel.Children.Add(_showScores); _settingsPanel.Children.Add(_onlyLibrary);
        _settingsPanel.Children.Add(Ui.Button("选择词库 / 类别…", async () =>
        {
            await new WordLibraryWindow(WordLibraryTarget.JoyTag).ShowDialog(_settingsOwner ?? this);
            if (_closed) return;
            ReloadWordCandidates(); RefreshDisplayedResults();
        }));
        _settingsPanel.Children.Add(_librarySummary);
        _settingsPanel.Children.Add(Ui.Button("模型管理…", async () => await ManageModelsAsync(_settingsOwner ?? this)));
    }
    private void AddSettingRow(string label, Control control)
    {
        var row = new Grid { ColumnDefinitions = new("140,*"), ColumnSpacing = 12 };
        row.Children.Add(Ui.Text(label)); Grid.SetColumn(control, 1); row.Children.Add(control); _settingsPanel.Children.Add(row);
    }
    private async Task OpenAdvancedAsync()
    {
        if (_busy) return;
        var window = new Window { Title = "AI 标签 · 高级设置", Width = 480, Height = 520, MinWidth = 420, MinHeight = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 12, Margin = new(20) };
        var scroll = new ScrollViewer { Content = _settingsPanel }; root.Children.Add(scroll);
        var close = Ui.DialogButton("关闭", window.Close); close.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetRow(close, 1); root.Children.Add(close); window.Content = root;
        window.Closing += (_, args) =>
        {
            try { Number(_threshold); var frames = Number(_frames); if (frames != Math.Truncate(frames)) throw new ArgumentException("采样帧数须为整数。"); }
            catch (Exception error) { args.Cancel = true; _ = Ui.Message(window, "参数错误", error.Message); }
        };
        window.Closed += (_, _) => scroll.Content = null;
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
        _updatingSelection = true; _selectAll.IsChecked = _entries.Count > 0 && included == _entries.Count; _updatingSelection = false;
        _fileCount.Text = Localization.Format($"勾选 {included} / {_entries.Count}");
        _empty.IsVisible = _entries.Count == 0; _selectAll.IsEnabled = !_busy && _entries.Count > 0;
        _imports.IsEnabled = _advanced.IsEnabled = _list.IsEnabled = !_busy;
        _analyze.IsEnabled = !_busy && included > 0; _analyze.Content = Localization.Text(!_modelReady ? "下载模型并分析" : "开始分析");
        _rename.IsEnabled = !_busy && _entries.Any(entry => entry.Include && _results.TryGetValue(entry.Path, out var result) && ResultTags(result).Any());
        _copy.IsVisible = _export.IsVisible = _rename.IsVisible = _results.Count > 0;
        _export.IsEnabled = !_busy && _results.Count > 0; _undo.IsEnabled = !_busy;
        _copy.IsEnabled = !_busy && _list.SelectedItem is MediaFileEntry selected && _results.TryGetValue(selected.Path, out var value) && ResultTags(value, search: true).Any();
        _stop.IsVisible = _busy && _operation is not null;
    }
}
