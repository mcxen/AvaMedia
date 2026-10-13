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

public sealed class ExclusiveRenamerWindow : Window
{
    private readonly IMediaEngine _engine;
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
    private readonly string _journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "exclusive-vl-rename.json");
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

    public ExclusiveRenamerWindow(IMediaEngine engine, AppSettings settings, Func<Window, bool, Task>? configure = null,
        Func<IEnumerable<string>, IDisposable>? reserveFiles = null)
    {
        if (!settings.ExclusiveToolUnlocked) throw new InvalidOperationException("内部工具未激活。");
        ApplyToolStyles(this);
        _engine = engine; _settings = settings; _configure = configure; _reserveFiles = reserveFiles;
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
        { foreach (var row in _list.SelectedItems?.OfType<Entry>().ToArray() ?? []) _entries.Remove(row); UpdateActions(); }));
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
        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto,Auto,Auto"), [!Grid.ColumnSpacingProperty] = Resource("AcRowGap") };
        footer.Children.Add(_status);
        _generate = Ui.Button("激活 VL 并生成预览", async () => await GenerateAsync());
        _rename = Ui.DialogButton("批量重命名", async () => await RenameAsync()); _rename.Classes.Add("primary");
        _undo = Ui.Button("撤销上次重命名", async () => await UndoAsync());
        _stop = Ui.Button("停止", () => _operation?.Cancel());
        foreach (var (column, button) in new[] { (1, _stop), (2, _undo), (3, _generate), (4, Ui.DialogButton("关闭", Close)), (5, _rename) })
        { Grid.SetColumn(button, column); footer.Children.Add(button); }
        Grid.SetRow(footer, 5); root.Children.Add(footer); Content = root;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, args) =>
        { args.DragEffects = !_busy && args.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; args.Handled = true; });
        AddHandler(DragDrop.DropEvent, async (_, args) =>
        { args.Handled = true; if (!_busy) await ImportAsync(args.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); });
        Closing += (_, args) => { if (_renaming) { args.Cancel = true; return; } _closed = true; _operation?.Cancel(); _lifetime.Cancel(); };
        Closed += (_, _) => _lifetime.Dispose(); _canUndo = BatchRename.CanUndo(_journal); RefreshMode(); UpdateActions();
    }

    private void RefreshProviders()
    {
        var selected = (_providers.SelectedItem as ProviderChoice)?.Id ?? _settings.OnlineAi.DefaultProviderId;
        var providers = _settings.OnlineAi.Providers.Where(provider => provider.Enabled).Select(provider => new ProviderChoice(provider.Id, provider.Name)).ToArray();
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
        });
        if (_generate is not null) _generate.Content = Localization.Text(_mode.SelectedIndex == 2 ? "按规则生成预览" : "激活 VL 并生成预览");
    }
    private async Task ConfigureAsync()
    {
        if (_busy || _configure is null) return;
        SetBusy(true);
        try { await _configure(this, _mode.SelectedIndex == 1); if (!_closed) RefreshProviders(); }
        catch (Exception error) { Report(error); }
        finally { if (!_closed) SetBusy(false); }
    }
    private async Task ImportAsync(IEnumerable<string> paths)
    {
        if (_busy || _closed) return;
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
                var entry = new Entry(path, input.FolderContext);
                entry.PropertyChanged += (_, args) => { if (args.PropertyName is nameof(Entry.Include) or nameof(Entry.Ready)) UpdateActions(); };
                _entries.Add(entry); added++;
            }
            Localization.SetText(_status, $"已添加 {added} 个图片或视频，共 {_entries.Count} 个。");
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = Localization.Text("扫描已停止。"); }
        catch (Exception error) { Report(error); }
        finally { _operation = null; if (!_closed) SetBusy(false); }
    }

    private async Task GenerateAsync()
    {
        if (_busy || _closed) return;
        var selected = _entries.Where(entry => entry.Include).ToArray(); if (selected.Length == 0) return;
        var mode = _mode.SelectedIndex; var providerId = (_providers.SelectedItem as ProviderChoice)?.Id;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation; SetBusy(true);
        foreach (var row in selected) row.Clear();
        ISummaryModel? model = null; var completed = 0; var failed = 0;
        try
        {
            if (mode == 0)
            {
                var store = new ModelStore();
                foreach (var id in new[] { ModelCatalog.SummaryRuntimeId, ModelCatalog.SummaryQwen35Id })
                    if (!await store.IsInstalledAsync(id, ct: operation.Token))
                        await store.DownloadAsync(id, new Progress<ModelDownloadProgress>(progress =>
                        { if (!_closed && _operation == operation) _status.Text = ModelCatalog.Find(id).Name + $" · {Localization.Text(progress.Stage)} {progress.Percent:0}%"; }), operation.Token);
                model = await LocalSummaryModelCache.AcquireAsync(store, ModelCatalog.SummaryQwen35Id, operation.Token,
                    stage => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!_closed && _operation == operation) _status.Text = Localization.Text(stage); }));
            }
            else if (mode == 1)
            {
                if (providerId is null) throw new ArgumentException("请先配置线上 VL 供应商。");
                var provider = _settings.OnlineAi.Resolve(providerId);
                if (string.IsNullOrWhiteSpace(provider.EffectiveVisionModel)) throw new ArgumentException("请在 AI 供应商中配置视觉模型。");
                model = new OnlineSummaryModel(provider, vision: true);
            }
            var renamer = new VlMediaRenamer(_engine);
            foreach (var row in selected)
            {
                operation.Token.ThrowIfCancellationRequested();
                row.Status = "分析中"; Localization.SetText(_status, $"{completed + failed + 1}/{selected.Length} · {row.Name}");
                try
                {
                    var suggestion = await renamer.SuggestAsync(row.Path, model, operation.Token, row.FolderContext);
                    if (_closed) return;
                    row.Suggestion = suggestion; row.NewName = Path.GetFileName(suggestion.Item.Target);
                    row.Details = row.Path + "\n" + suggestion.Details; row.Status = mode == 2 ? "规则预览" : "VL 已分析"; completed++;
                }
                catch (OperationCanceledException) { row.Status = "已停止"; throw; }
                catch (Exception error) { row.Status = "分析失败"; row.Details = error.Message; failed++; }
            }
            AvoidConflicts(selected);
            Localization.SetText(_status, $"预览完成：成功 {completed} 个，失败 {failed} 个。确认建议名称后执行重命名。");
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                try { AvoidConflicts(selected); _status.Text = Localization.Text("已停止，已完成的预览已保留。未完成项可取消勾选。"); }
                catch (Exception error) { Report(error); }
            }
        }
        catch (Exception error) { Report(error); }
        finally
        {
            try { if (model is not null) await model.DisposeAsync(); }
            finally { _operation = null; if (!_closed) SetBusy(false); }
        }
    }

    private static void AvoidConflicts(IEnumerable<Entry> rows)
    {
        foreach (var group in rows.Where(row => row.Suggestion is not null).GroupBy(row => Path.GetDirectoryName(row.Path)!, BatchRename.PathComparer))
        {
            // Conservative casing also prevents collisions on the usual case-insensitive macOS volumes.
            var reserved = Directory.EnumerateFileSystemEntries(group.Key).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in group)
            {
                var initial = row.NewName; var next = 2;
                if (StringComparer.OrdinalIgnoreCase.Equals(initial, row.Name)) continue;
                while (reserved.Contains(row.NewName))
                    row.NewName = VlMediaNaming.WithConflictNumber(initial, row.Suggestion!.Identifier, next++);
                reserved.Add(row.NewName);
            }
        }
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
        if (_busy || _closed) return;
        SetBusy(true); _renaming = true; UpdateActions();
        try
        {
            var plan = BuildPlan();
            using var reservation = _reserveFiles?.Invoke(plan.SelectMany(item => new[] { item.Source, item.Target }));
            var changed = await Task.Run(() => BatchRename.ApplyRename(plan, _journal));
            ApplyChanges(changed); Renamed?.Invoke(changed);
            Localization.SetText(_status, $"已重命名 {changed.Length} 个文件，可撤销上次操作。");
        }
        catch (Exception error) { Report(error); }
        finally { _renaming = false; SetBusy(false); }
    }
    private async Task UndoAsync()
    {
        if (_busy || _closed) return;
        SetBusy(true); _renaming = true; UpdateActions();
        try
        {
            var journal = System.Text.Json.JsonSerializer.Deserialize<BatchRename.RenameJournal>(await File.ReadAllTextAsync(_journal))
                ?? throw new InvalidDataException("恢复记录无效。");
            using var reservation = _reserveFiles?.Invoke(journal.Stages.SelectMany(stage => new[] { stage.Item.Source, stage.Item.Target }));
            var changed = await Task.Run(() => BatchRename.UndoRename(_journal)); ApplyChanges(changed); Renamed?.Invoke(changed);
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
    { _busy = busy; _toolbar.IsEnabled = _models.IsEnabled = _list.IsEnabled = !busy; if (!busy) _canUndo = BatchRename.CanUndo(_journal); UpdateActions(); }
    private void UpdateActions()
    {
        if (_closed || _generate is null) return;
        _generate.IsEnabled = !_busy && _entries.Any(row => row.Include);
        var included = _entries.Where(row => row.Include).ToArray();
        _rename.IsEnabled = !_busy && included.Length > 0 && included.All(row => row.Ready)
            && included.Any(row => !StringComparer.Ordinal.Equals(row.Name, row.NewName));
        _undo.IsEnabled = !_busy && _canUndo; _stop.IsEnabled = _busy && !_renaming && _operation is not null;
    }
    private void Report(Exception error)
    { if (!_closed) _status.Text = error.Message; AppDiagnostics.Record("Exclusive VL renamer", error); }
}
