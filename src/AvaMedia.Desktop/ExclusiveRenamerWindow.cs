using static AvaMedia.Desktop.Classification.ClassifyUi;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class ExclusiveRenamerWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<Window, bool, Task>? _configure;
    private readonly Func<IEnumerable<string>, IDisposable>? _reserveFiles;
    private readonly ObservableCollection<Entry> _entries = [];
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple };
    private readonly CheckBox _recursive = new() { Classes = { "ac" }, Content = "包含子文件夹", IsChecked = true };
    private readonly ComboBox _mode = Ui.ComponentCombo(["本地 VL · Qwen3.5", "线上 VL", "文件夹与文件名规则"], "本地 VL · Qwen3.5");
    private readonly ComboBox _providers = new() { Classes = { "ac" }, MinWidth = 140, MaxWidth = 260 };
    private readonly TextBlock _status = Ui.ComponentStatus("拖入文件夹或浏览选择，生成新名称后可逐项编辑。");
    private readonly TextBlock _modelHint = Ui.ComponentText("", "caption");
    private readonly Button _generate, _rename, _undo, _stop, _manage;
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, [!StackPanel.SpacingProperty] = Resource("AcRowGap") };
    private readonly StackPanel _models = new() { Orientation = Orientation.Horizontal, [!StackPanel.SpacingProperty] = Resource("AcRowGap") };
    private readonly CancellationTokenSource _lifetime = new();
    private string _journal = "";
    private CancellationTokenSource? _operation;
    private bool _busy, _renaming, _closed, _canUndo;
    public event Action<IReadOnlyList<RenameItem>>? Renamed;

    private sealed class Entry(string path, string folderContext) : Observable
    {
        public string Path { get; set; } = path;
        public string FolderContext { get; } = folderContext;
        public string Name => System.IO.Path.GetFileName(Path);
        private bool _include = true;
        private string _newName = "", _status = "待分析", _details = path;
        private string? _nameError;
        public bool Include { get => _include; set => Set(ref _include, value); }
        public string NewName
        {
            get => _newName;
            set
            {
                if (!Set(ref _newName, value ?? "")) return;
                _nameError = null;
                if (Suggestion is not null)
                    try { VlMediaNaming.ValidateFileName(Path, _newName, Suggestion.Identifier); }
                    catch (ArgumentException error) { _nameError = error.Message; }
                Raise(nameof(Status)); Raise(nameof(Details)); Raise(nameof(Ready));
            }
        }
        public string Status { get => _nameError is null ? _status : "名称无效"; set => Set(ref _status, value); }
        public string Details { get => _nameError ?? _details; set => Set(ref _details, value); }
        public bool Ready => Suggestion is not null && _nameError is null && NewName.Length > 0;
        public VlMediaRenameSuggestion? Suggestion { get; set; }
        public void MoveTo(string path) { Path = path; Raise(nameof(Path)); Raise(nameof(Name)); Clear(); }
        public void Clear()
        {
            Suggestion = null; _nameError = null; NewName = ""; Status = "待分析"; Details = Path;
            Raise(nameof(Status)); Raise(nameof(Details)); Raise(nameof(Ready));
        }
    }
    private sealed record ProviderChoice(string Id, string Name)
    { public override string ToString() => Name; }

    public ExclusiveRenamerWindow(AppSettings settings, Func<Job, Task> submit,
        Func<Job, RenameItem[], Task<Job>> commit, Action<Job> stopTask, Func<Job, Task> viewTask, Action persist,
        Func<Window, bool, Task>? configure = null,
        Func<IEnumerable<string>, IDisposable>? reserveFiles = null)
    {
        if (!settings.ExclusiveToolUnlocked) throw new InvalidOperationException("内部工具未激活。");
        ApplyToolStyles(this);
        _settings = settings; _configure = configure; _reserveFiles = reserveFiles;
        _submit = submit; _commit = commit; _stopTask = stopTask; _viewTask = viewTask; _persist = persist;
        Title = "专属工具 · VL 媒体智能重命名"; Width = 1160; Height = 700; MinWidth = 900; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Controls.WindowArtwork.SetKind(this, "gear");
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*,Auto"), RowSpacing = 10, [!Avalonia.Controls.Control.MarginProperty] = Resource("CtWindowMargin") };
        _toolbar.Children.Add(Ui.Button("浏览文件夹…", async () =>
        {
            try { if (await Ui.Folder(this, "选择媒体文件夹") is { } folder) await ImportAsync([folder]); }
            catch (Exception error) { Report(error); }
        }));
        _toolbar.Children.Add(_recursive);
        _toolbar.Children.Add(Ui.Button("移除选中", () =>
        {
            foreach (var row in _list.SelectedItems?.OfType<Entry>().ToArray() ?? [])
            { if (_analysisJob is not null) row.Include = false; _entries.Remove(row); }
            UpdateActions();
        }));
        _toolbar.Children.Add(Ui.Button("全选", () => { foreach (var row in _entries) row.Include = true; }));
        _toolbar.Children.Add(Ui.Button("反选", () => { foreach (var row in _entries) row.Include = !row.Include; }));
        root.Children.Add(_toolbar);
        _models.Children.Add(Ui.ComponentText("分析方式")); _models.Children.Add(_mode); _models.Children.Add(_providers);
        Avalonia.Automation.AutomationProperties.SetName(_mode, "分析方式");
        Avalonia.Automation.AutomationProperties.SetName(_providers, "AI 供应商");
        _manage = Ui.Button("模型设置…", async () => await ConfigureAsync()); _models.Children.Add(_manage);
        Grid.SetRow(_models, 1); root.Children.Add(_models);
        _mode.SelectionChanged += (_, _) => RefreshMode(); RefreshProviders(); RefreshMode();
        Grid.SetRow(_modelHint, 2); root.Children.Add(_modelHint);
        var contract = Ui.ComponentText("前缀_场景_视角_装扮_动作_评级_原标识 · 保留扩展名，无法确认的特征标为未知", "caption");
        Grid.SetRow(contract, 3); root.Children.Add(contract);
        var table = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 4 };
        var header = new Grid { ColumnDefinitions = new("32,*,*,140"), [!Grid.ColumnSpacingProperty] = Resource("AcRowGap"), Classes = { "table-header" } };
        foreach (var (column, title) in new[] { (1, "原文件名"), (2, "建议新文件名（可编辑）"), (3, "状态") })
        { var label = Ui.ComponentText(title, "caption"); Grid.SetColumn(label, column); header.Children.Add(label); }
        table.Children.Add(header); _list.ItemsSource = _entries;
        _list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>()) { Setters =
            { new Setter(PaddingProperty, new Thickness(4, 5)), new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _list.ItemTemplate = new FuncDataTemplate<Entry>((_, _) =>
        {
            var row = new Grid { ColumnDefinitions = new("32,*,*,140"), [!Grid.ColumnSpacingProperty] = Resource("AcRowGap") };
            var check = new CheckBox(); check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(Entry.Include)) { Mode = BindingMode.TwoWay });
            check.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(Entry.Name))); row.Children.Add(check);
            var name = Ui.ComponentText(""); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            Localization.SetIsUserText(name, true); name.Bind(TextBlock.TextProperty, new Binding(nameof(Entry.Name)));
            name.Bind(ToolTip.TipProperty, new Binding(nameof(Entry.Path))); Grid.SetColumn(name, 1); row.Children.Add(name);
            var edit = Ui.ComponentInput(); Localization.SetIsUserText(edit, true);
            Avalonia.Automation.AutomationProperties.SetName(edit, "建议新文件名");
            edit.Bind(TextBox.TextProperty, new Binding(nameof(Entry.NewName)) { Mode = BindingMode.TwoWay });
            edit.Bind(ToolTip.TipProperty, new Binding(nameof(Entry.NewName))); Grid.SetColumn(edit, 2); row.Children.Add(edit);
            var state = Ui.ComponentText("", "caption"); state.Bind(TextBlock.TextProperty, new Binding(nameof(Entry.Status)));
            state.Bind(ToolTip.TipProperty, new Binding(nameof(Entry.Details))); Grid.SetColumn(state, 3); row.Children.Add(state);
            return row;
        });
        Grid.SetRow(_list, 1); table.Children.Add(_list); Grid.SetRow(table, 4); root.Children.Add(table);
        var empty = new AvaMedia.Controls.EmptyState { Text = Localization.Text("选择需要重命名的文件"), IsVisible = _entries.Count == 0 };
        _entries.CollectionChanged += (_, _) => empty.IsVisible = _entries.Count == 0;
        Grid.SetRow(empty, 1); table.Children.Add(empty);
        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto,Auto,Auto,Auto"), [!Grid.ColumnSpacingProperty] = Resource("AcRowGap") };
        footer.Children.Add(_status);
        _generate = Ui.Button("后台 VL 分析", async () => await GenerateAsync());
        _rename = Ui.DialogButton("批量重命名", async () => await RenameAsync()); _rename.Classes.Add("primary");
        _undo = Ui.Button("撤销上次重命名", async () => await UndoAsync());
        _stop = Ui.Button("停止", StopCurrentTask);
        _background = Ui.Button("查看后台任务", async () =>
        { if ((_renameJob ?? _analysisJob) is { } job) await _viewTask(job); });
        foreach (var (column, button) in new[] { (1, _stop), (2, _undo), (3, _background), (4, _generate), (5, Ui.DialogButton("关闭", Close)), (6, _rename) })
        { Grid.SetColumn(button, column); footer.Children.Add(button); }
        Grid.SetRow(footer, 5); root.Children.Add(footer); Content = root;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, args) =>
        { args.DragEffects = !_busy && !TaskActive(_analysisJob) && !TaskActive(_renameJob) && args.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; args.Handled = true; });
        AddHandler(DragDrop.DropEvent, async (_, args) =>
        { args.Handled = true; if (!_busy) await ImportAsync(args.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); });
        Closing += (_, args) => { if (_renaming) { args.Cancel = true; return; } _closed = true; _operation?.Cancel(); _lifetime.Cancel(); };
        Opened += (_, _) => _taskTimer.Start();
        Closed += (_, _) => { _taskTimer.Stop(); _reviewTimer.Stop(); if (_reviewDirty) _persist(); _lifetime.Dispose(); };
        _reviewTimer.Tick += (_, _) => { _reviewTimer.Stop(); if (_reviewDirty) { _reviewDirty = false; _persist(); } };
        _taskTimer.Tick += (_, _) => RefreshTask(); RefreshMode(); UpdateActions();
    }

    private void RefreshProviders()
    {
        var selected = (_providers.SelectedItem as ProviderChoice)?.Id ?? _settings.OnlineAi.DefaultProviderId;
        var providers = _settings.OnlineAi.Providers.Where(provider => provider.Enabled).Select(provider => new ProviderChoice(provider.Id, provider.Name)).ToArray();
        if (_analysisJob?.Options.VlRename is { Mode: "online" } spec && providers.All(provider => provider.Id != spec.ProviderId))
            providers = [.. providers, new ProviderChoice(spec.ProviderId, spec.ProviderName + " · " + Localization.Text("已停用"))];
        _providers.ItemsSource = providers; _providers.SelectedItem = providers.FirstOrDefault(provider => provider.Id == selected) ?? providers.FirstOrDefault();
    }
    private void RefreshMode()
    {
        _providers.IsVisible = _mode.SelectedIndex == 1;
        _manage.IsVisible = _configure is not null && _mode.SelectedIndex != 2;
        _modelHint.Text = Localization.Text(_mode.SelectedIndex switch
        {
            0 => "本地 VL：首次生成会下载 Qwen3.5 视觉模型及推理工具，处理过程可停止。",
            1 => "使用所选供应商的视觉模型；生成时将发送文件夹名、文件名及采样画面。",
            _ => "按文件夹与文件名解析六个命名字段，不调用模型。"
        }) + " " + Localization.Text("关闭窗口后任务仍继续，可在任务列表查看进度。");
        if (_generate is not null) _generate.Content = Localization.Text(_mode.SelectedIndex == 2 ? "后台规则分析" : "后台 VL 分析");
    }
    private async Task ConfigureAsync()
    {
        if (_busy || _configure is null || TaskActive(_analysisJob) || TaskActive(_renameJob)) return;
        SetBusy(true);
        try { await _configure(this, _mode.SelectedIndex == 1); if (!_closed) RefreshProviders(); }
        catch (Exception error) { Report(error); }
        finally { if (!_closed) SetBusy(false); }
    }
    private async Task ImportAsync(IEnumerable<string> paths)
    {
        if (_busy || _closed || TaskActive(_analysisJob) || TaskActive(_renameJob)) return;
        var folders = paths.Where(Directory.Exists).Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(BatchRename.PathComparer).ToArray();
        if (folders.Length == 0) { _status.Text = Localization.Text("请拖入或选择文件夹。"); return; }
        var recursive = _recursive.IsChecked == true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation; SetBusy(true);
        try
        {
            _status.Text = Localization.Text("正在扫描媒体文件…");
            var pathsFound = await Task.Run(() => VlMediaRenamer.CollectInputs(folders, recursive, operation.Token), operation.Token);
            if (_closed) return;
            var existing = _entries.Select(entry => entry.Path).ToHashSet(BatchRename.PathComparer); var added = 0;
            foreach (var input in pathsFound)
            {
                var path = input.Path;
                if (!existing.Add(path)) continue;
                AddEntry(path, input.FolderContext); added++;
            }
            Localization.SetText(_status, $"已添加 {added} 个图片或视频，共 {_entries.Count} 个。");
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = Localization.Text("扫描已停止。"); }
        catch (Exception error) { Report(error); }
        finally { _operation = null; if (!_closed) SetBusy(false); }
    }

    private RenameItem[] BuildPlan()
    {
        var selected = _entries.Where(row => row.Include).ToArray();
        if (selected.Length == 0) throw new ArgumentException("请勾选需要重命名的文件。");
        var plan = selected.Select(row =>
        {
            var suggestion = row.Suggestion ?? throw new ArgumentException("请先为所有勾选项生成预览，或取消勾选未完成项。");
            VlMediaNaming.ValidateFileName(row.Path, row.NewName, suggestion.Identifier);
            return suggestion.Item with { Target = Path.Combine(Path.GetDirectoryName(row.Path)!, row.NewName) };
        }).ToArray();
        return plan;
    }
    private async Task RenameAsync()
    {
        if (_busy || _closed || TaskActive(_analysisJob) || TaskActive(_renameJob)) return;
        if (_analysisJob is null) return;
        SetBusy(true);
        try
        {
            var plan = BuildPlan();
            _renameJob = await _commit(_analysisJob, plan);
            _journal = _renameJob.Output; _seenRename = false;
            _status.Text = Localization.Text("重命名已加入后台任务列表。");
        }
        catch (Exception error) { Report(error); }
        finally { if (!_closed) SetBusy(false); }
    }
    private async Task UndoAsync()
    {
        if (_busy || _closed || TaskActive(_analysisJob) || TaskActive(_renameJob)) return;
        SetBusy(true); _renaming = true; UpdateActions();
        try
        {
            var journal = System.Text.Json.JsonSerializer.Deserialize<BatchRename.RenameJournal>(await File.ReadAllTextAsync(_journal))
                ?? throw new InvalidDataException("恢复记录无效。");
            using var reservation = _reserveFiles?.Invoke(journal.Stages.SelectMany(stage => new[] { stage.Item.Source, stage.Item.Target }));
            var changed = await Task.Run(() => BatchRename.UndoRename(_journal)); ApplyChanges(changed); Renamed?.Invoke(changed);
            _renameUndone = true;
            Localization.SetText(_status, $"已恢复 {changed.Length} 个原文件名。");
        }
        catch (Exception error) { Report(error); }
        finally { _renaming = false; SetBusy(false); }
    }
    private void ApplyChanges(IReadOnlyList<RenameItem> changes)
    {
        foreach (var row in _entries)
            if (changes.FirstOrDefault(change => BatchRename.PathComparer.Equals(change.Source, row.Path)) is { } change) row.MoveTo(change.Target);
    }
    private void SetBusy(bool busy)
    { _busy = busy; if (!busy) _canUndo = _journal.Length > 0 && BatchRename.CanUndo(_journal); UpdateActions(); }
    private void UpdateActions()
    {
        if (_closed || _generate is null) return;
        var active = TaskActive(_analysisJob) || TaskActive(_renameJob);
        _toolbar.IsEnabled = _models.IsEnabled = _list.IsEnabled = !_busy && !active;
        _generate.IsEnabled = !_busy && !active && _entries.Any(row => row.Include);
        var included = _entries.Where(row => row.Include).ToArray();
        _rename.IsEnabled = !_busy && !active && included.Length > 0 && included.All(row => row.Ready)
            && included.Any(row => !StringComparer.Ordinal.Equals(row.Name, row.NewName));
        _undo.IsEnabled = !_busy && !active && _canUndo;
        _stop.IsEnabled = !_renaming && (_operation is not null || active);
        _background.IsEnabled = _analysisJob is not null;
    }
    private void Report(Exception error)
    { if (!_closed) _status.Text = error.Message; AppDiagnostics.Record("Exclusive VL renamer", error); }
}
