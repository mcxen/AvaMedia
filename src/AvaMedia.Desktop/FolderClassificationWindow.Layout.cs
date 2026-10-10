using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow
{
    private readonly ListBox _files = new() { SelectionMode = SelectionMode.Single, Padding = new(0) };
    private readonly ListBox _ruleList = new() { MinHeight = 100, MaxHeight = 210 };
    private readonly ComboBox _savedSelector = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _saveRule = new() { Content = "保存分类" };
    private readonly Button _setDefault = new() { Content = "设为默认" };
    private readonly Button _restoreDefault = new() { Content = "载入默认" };
    private readonly Button _useSaved = new() { Content = "使用" };
    private readonly Button _deleteSaved = new() { Content = "删除" };
    private readonly TextBlock _defaultSummary = Ui.Text("", "caption");
    private readonly WrapPanel _imports = new();
    private readonly StackPanel _settingsPanel = new() { Spacing = 10 };
    private readonly StackPanel _rulesPanel = new() { Spacing = 8 };
    private readonly StackPanel _details = new() { Spacing = 8 };
    private readonly CheckBox _recursive = new() { Content = "包含子文件夹" };
    private readonly CheckBox _splitTypes = new() { Content = "按视频 / 图片分目录" };
    private readonly CheckBox _writeText = new() { Content = "为每个文件保存标签 TXT" };
    private readonly CheckBox _gpu = new() { Content = "优先使用 GPU" };
    private readonly TextBox _output = new() { IsReadOnly = true };
    private readonly NumericUpDown _frames = new() { Minimum = 1, Maximum = 32, Increment = 1, FormatString = "0" };
    private readonly NumericUpDown _tagThreshold = new() { Minimum = 0, Maximum = 1, Increment = .05m, FormatString = "0.00" };
    private readonly ComboBox _mode = Ui.Combo(["复制到分类目录", "移动到分类目录"], "复制到分类目录");
    private readonly TextBlock _status = Ui.Status("选择文件夹或拖入媒体");
    private readonly TextBlock _scanErrors = Ui.Text("", "caption");
    private readonly AiActivityView _activity = new() { Collapsible = true, DetailHeight = 170 };
    private readonly StackPanel _videoSettings = new() { Spacing = 6, IsVisible = false };
    private readonly Button _analyze = new() { Content = "后台运行分类", Classes = { "primary" } };
    private readonly Button _retry = new() { Content = "重试未完成" };
    private readonly Button _preview = new() { Content = "预览分类目录" };
    private readonly Button _organize = new() { Content = "执行整理", Classes = { "primary" } };
    private readonly Button _undo = new() { Content = "撤销上次整理" };
    private readonly Button _export = new() { Content = "导出分类结果…" };
    private readonly Button _pause = new() { Content = "暂停任务", IsVisible = false };
    private readonly Button _stop = new() { Content = "停止", IsVisible = false };

    private void BuildInterface()
    {
        StableLayout.Reserve(_pause, "暂停任务", "继续任务");
        StableLayout.Reserve(_allFilter, "全部 888888", "All 888888");
        StableLayout.Reserve(_pendingFilter, "待分析 888888", "Pending 888888");
        _mode.ItemsSource = new[] { "复制到分类目录", "移动到分类目录" }.Select(Localization.Text).ToArray(); _mode.SelectedIndex = 0;
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new(12), RowSpacing = 8 };
        AddImport("添加文件夹…", async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new() { AllowMultiple = true });
            await ImportPathsAsync(folders.Select(folder => folder.TryGetLocalPath()).OfType<string>());
        });
        AddImport("添加图片 / 视频…", async () => await ImportPathsAsync(await Ui.Pick(this, "选择图片或视频")));
        AddImport("重新扫描", ScanAsync);
        AddImport("清空列表", () =>
        { _inputs.Clear(); _entries.Clear(); _results.Clear(); _hiddenPrivateResults.Clear(); _analysisPending.Clear(); InvalidatePlan(); RenderDetails(); return Task.CompletedTask; });
        AddImport("全选", () => { SelectEntries(_ => true); return Task.CompletedTask; });
        AddImport("取消全选", () => { SelectEntries(_ => false); return Task.CompletedTask; });
        AddImport("仅选已完成", () => { SelectEntries(entry => _results.ContainsKey(entry.Path)); return Task.CompletedTask; });
        _recursive.Margin = new(8, 0, 0, 0); _imports.Children.Add(_recursive);
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        header.Children.Add(_imports);
        var tasks = new WrapPanel();
        foreach (var (label, action) in new (string, Action)[] { ("新建分类任务", _newTask), ("查看任务列表", _showTasks) })
        { var button = Ui.Button(label, action); button.Margin = new(6, 0, 0, 6); tasks.Children.Add(button); }
        Grid.SetColumn(tasks, 1); header.Children.Add(tasks); root.Children.Add(header);
        var body = new Grid { ColumnDefinitions = new("228,*,260"), ColumnSpacing = 12 };
        _settingsPanel.Children.Add(Ui.Text("分类规则", "settingsHeading"));
        _rulesPanel.Children.Add(Ui.Text("选择需要的分组，最多同时使用 8 组。", "caption"));
        _ruleList.ItemsSource = _rules;
        _ruleList.ItemTemplate = new FuncDataTemplate<FolderClassificationRule>((rule, _) =>
        {
            var text = UserText(rule is null ? "" : rule.ByOutfit ? rule.Name : rule.Name + " · " + string.Join(" / ", rule.Categories.Select(category => category.Name)));
            text.TextTrimming = TextTrimming.CharacterEllipsis; return text;
        });
        _rulesPanel.Children.Add(_ruleList); _ruleList.SelectedItem = _rules.FirstOrDefault();
        var ruleActions = new WrapPanel();
        var outfits = Ui.Button("按相似服装", async () => await GuardAsync(UseOutfitGroupingAsync));
        outfits.Margin = new(0, 0, 6, 6); ruleActions.Children.Add(outfits);
        var selectPresets = Ui.Button("选择预设分组…", async () => await GuardAsync(SelectPresetsAsync));
        selectPresets.Margin = new(0, 0, 6, 6); ruleActions.Children.Add(selectPresets);
        foreach (var (label, action) in new (string, Func<Task>)[] {
            ("添加分类组…", () => EditRuleAsync(null)), ("编辑…", () => EditRuleAsync(_ruleList.SelectedItem as FolderClassificationRule)),
            ("移除", () => { if (_ruleList.SelectedItem is FolderClassificationRule rule) { _rules.Remove(rule); InvalidateAnalysis(); SavePreferences(); } return Task.CompletedTask; }) })
        { var button = Ui.Button(label, async () => await GuardAsync(action)); button.Margin = new(0, 0, 6, 6); ruleActions.Children.Add(button); }
        _rulesPanel.Children.Add(ruleActions); _settingsPanel.Children.Add(_rulesPanel);
        var savedActions = new WrapPanel();
        foreach (var button in new[] { _saveRule, _setDefault, _restoreDefault })
        { button.Margin = new(0, 0, 6, 6); savedActions.Children.Add(button); }
        _rulesPanel.Children.Add(savedActions); _rulesPanel.Children.Add(_defaultSummary);
        _rulesPanel.Children.Add(Ui.Text("已保存分类", "caption"));
        _savedSelector.ItemsSource = _savedRules;
        _savedSelector.ItemTemplate = new FuncDataTemplate<FolderClassificationRule>((rule, _) =>
        { var name = Ui.Text(rule?.Name ?? "", "caption"); Localization.SetIsUserText(name, true); return name; });
        var savedRow = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 5 };
        savedRow.Children.Add(_savedSelector); Grid.SetColumn(_useSaved, 1); savedRow.Children.Add(_useSaved);
        Grid.SetColumn(_deleteSaved, 2); savedRow.Children.Add(_deleteSaved); _rulesPanel.Children.Add(savedRow);

        _saveRule.Click += async (_, _) => await GuardAsync(SaveSelectedRuleAsync);
        _setDefault.Click += async (_, _) => await GuardAsync(SetDefaultRulesAsync);
        _restoreDefault.Click += (_, _) => RestoreDefaultRules();
        _useSaved.Click += async (_, _) => await GuardAsync(UseSavedRuleAsync);
        _deleteSaved.Click += async (_, _) => await GuardAsync(DeleteSavedRuleAsync);
        _ruleList.SelectionChanged += (_, _) => RefreshRuleActions();
        _savedSelector.SelectionChanged += (_, _) => RefreshRuleActions();
        var analysisSettings = new StackPanel { Spacing = 8 };
        _videoSettings.Children.Add(Ui.Text("视频采样帧数", "caption")); _videoSettings.Children.Add(Ui.Adjust(_frames));
        analysisSettings.Children.Add(_videoSettings);
        analysisSettings.Children.Add(Ui.Text("标签阈值", "caption")); analysisSettings.Children.Add(Ui.Adjust(_tagThreshold));
        analysisSettings.Children.Add(_gpu);
        analysisSettings.Children.Add(Ui.Button("模型管理…", async () => await GuardAsync(() => _manageModels(this))));
        _settingsPanel.Children.Add(new Expander { Header = "分析设置", Content = analysisSettings, HorizontalAlignment = HorizontalAlignment.Stretch });
        _settingsPanel.Children.Add(Ui.Text("分类目录", "settingsHeading"));
        Localization.SetIsUserText(_output, true); _settingsPanel.Children.Add(_output);
        _settingsPanel.Children.Add(Ui.Button("选择分类目录…", async () => await GuardAsync(async () =>
        { if (await Ui.Folder(this, "选择分类目录") is { } folder) _output.Text = folder; })));
        _settingsPanel.Children.Add(_splitTypes); _settingsPanel.Children.Add(_writeText); _settingsPanel.Children.Add(_mode);
        body.Children.Add(new ScrollViewer { Content = _settingsPanel });
        var board = BuildBoard(); Grid.SetColumn(board, 1); body.Children.Add(board);
        var inspector = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 10 };
        inspector.Children.Add(_coverPanel);
        var detailsScroll = new ScrollViewer { Content = _details }; Grid.SetRow(detailsScroll, 1); inspector.Children.Add(detailsScroll);
        Grid.SetColumn(inspector, 2); body.Children.Add(inspector);
        Grid.SetRow(body, 1); root.Children.Add(body);
        Grid.SetRow(_activity, 2); root.Children.Add(_activity);
        var footer = new StackPanel { Spacing = 6 };
        _scanErrors.IsVisible = false; _scanErrors.MaxHeight = 60; footer.Children.Add(new ScrollViewer { Content = _scanErrors, MaxHeight = 60 });
        footer.Children.Add(_status);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { _pause, _stop, _undo, _export, _retry, _analyze, _preview, _organize })
        { button.Margin = new(6, 0, 0, 6); actions.Children.Add(button); }
        actions.Children.Add(Ui.DialogButton("关闭", Close)); footer.Children.Add(actions); Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        SizeChanged += (_, _) =>
        {
            _activity.DetailHeight = Math.Clamp(Bounds.Height * .22, 90, 180);
            _selectedCover.Height = Math.Clamp(Bounds.Height * .22, 100, 180);
            if (Bounds.Height < 640) _activity.DetailsExpanded = false;
        };
        _analyze.Click += async (_, _) => await GuardAsync(() => AnalyzeAsync(false));
        _retry.Click += async (_, _) => await GuardAsync(() => AnalyzeAsync(true));
        _preview.Click += async (_, _) => await GuardAsync(PreviewAsync);
        _organize.Click += async (_, _) => await GuardAsync(OrganizeAsync);
        _undo.Click += async (_, _) => await GuardAsync(UndoAsync);
        _export.Click += async (_, _) => await GuardAsync(ExportAsync);
        _pause.Click += async (_, _) =>
        {
            if (_taskJob is not { } job) return;
            if (job.State == JobState.Paused) await _resumeTask(job); else _pauseTask?.Invoke(job);
        };
        _stop.Click += (_, _) => { if (TaskActive && _taskJob is { } task) _stopTask(task); else _operation?.Cancel(); };
        _output.TextChanged += (_, _) => InvalidatePlan();
        _splitTypes.IsCheckedChanged += (_, _) => InvalidatePlan(); _writeText.IsCheckedChanged += (_, _) => InvalidatePlan();
        _frames.ValueChanged += (_, _) => InvalidateAnalysis(); _tagThreshold.ValueChanged += (_, _) => Reclassify();
        _recursive.IsCheckedChanged += async (_, _) => await ScanAsync();
        RenderBoard();
    }

    private void AddImport(string label, Func<Task> action)
    { var button = Ui.Button(label, async () => await GuardAsync(action)); button.Margin = new(0, 0, 6, 6); _imports.Children.Add(button); }
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); if (!_closed && !_busy) await SaveTaskViewAsync(); }
        catch (OperationCanceledException) { if (!_closed) _status.Text = Localization.Text("已停止"); }
        catch (Exception error) { if (!_closed) await Ui.Message(this, Catalog.Find("folder-classification").Label, error.Message); }
    }

}
