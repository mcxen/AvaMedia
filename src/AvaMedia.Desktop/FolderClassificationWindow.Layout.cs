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
    private readonly TextBox _output = Ui.Input();
    private readonly NumericUpDown _frames = new() { Minimum = 1, Maximum = 32, Increment = 1, FormatString = "0" };
    private readonly NumericUpDown _tagThreshold = new() { Minimum = 0, Maximum = 1, Increment = .05m, FormatString = "0.00" };
    private readonly ComboBox _mode = Ui.Combo(["复制到分类目录", "移动到分类目录"], "复制到分类目录");
    private readonly TextBlock _status = Ui.Text("选择文件夹或拖入媒体", "caption");
    private readonly TextBlock _scanErrors = Ui.Text("", "caption");
    private readonly AiActivityView _activity = new();
    private readonly Button _analyze = new() { Content = "自动标签与分类", Classes = { "primary" } };
    private readonly Button _retry = new() { Content = "重试未完成" };
    private readonly Button _preview = new() { Content = "预览分类目录" };
    private readonly Button _organize = new() { Content = "执行整理", Classes = { "primary" } };
    private readonly Button _undo = new() { Content = "撤销上次整理" };
    private readonly Button _export = new() { Content = "导出分类结果…" };
    private readonly Button _stop = new() { Content = "停止", IsVisible = false };

    private void BuildInterface()
    {
        _mode.ItemsSource = new[] { "复制到分类目录", "移动到分类目录" }.Select(Localization.Text).ToArray(); _mode.SelectedIndex = 0;
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new(16), RowSpacing = 10 };
        AddImport("添加文件夹…", async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new() { AllowMultiple = true });
            await ImportPathsAsync(folders.Select(folder => folder.TryGetLocalPath()).OfType<string>());
        });
        AddImport("添加图片 / 视频…", async () => await ImportPathsAsync(await Ui.Pick(this, "选择图片或视频")));
        AddImport("重新扫描", ScanAsync);
        AddImport("清空列表", () =>
        { _inputs.Clear(); _entries.Clear(); _results.Clear(); InvalidatePlan(); RenderDetails(); return Task.CompletedTask; });
        AddImport("全选", () => { SelectEntries(_ => true); return Task.CompletedTask; });
        AddImport("仅选已完成", () => { SelectEntries(entry => _results.ContainsKey(entry.Path)); return Task.CompletedTask; });
        _recursive.Margin = new(8, 0, 0, 0); _imports.Children.Add(_recursive); root.Children.Add(_imports);
        var body = new Grid { ColumnDefinitions = new("280,*,280"), ColumnSpacing = 12 };
        _settingsPanel.Children.Add(Ui.Text("分类规则", "settingsHeading"));
        _rulesPanel.Children.Add(Ui.Text("多条规则按顺序生成子目录。", "caption"));
        _ruleList.ItemsSource = _rules;
        _ruleList.ItemTemplate = new FuncDataTemplate<FolderClassificationRule>((rule, _) => Ui.Text(rule?.Name ?? "", "caption"));
        _rulesPanel.Children.Add(_ruleList);
        var ruleActions = new WrapPanel();
        foreach (var preset in FolderClassificationRule.Presets)
        {
            var button = Ui.Button(preset.Name, async () => await GuardAsync(() => AddPresetAsync(preset)));
            button.Margin = new(0, 0, 6, 6); ruleActions.Children.Add(button);
        }
        foreach (var (label, action) in new (string, Func<Task>)[] {
            ("自定义…", () => EditRuleAsync(null)), ("编辑…", () => EditRuleAsync(_ruleList.SelectedItem as FolderClassificationRule)),
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
        ToolTip.SetTip(_setDefault, "将当前分类规则设为下次打开时的默认分类");
        ToolTip.SetTip(_deleteSaved, "删除已保存分类");
        _saveRule.Click += async (_, _) => await GuardAsync(SaveSelectedRuleAsync);
        _setDefault.Click += async (_, _) => await GuardAsync(SetDefaultRulesAsync);
        _restoreDefault.Click += (_, _) => RestoreDefaultRules();
        _useSaved.Click += async (_, _) => await GuardAsync(UseSavedRuleAsync);
        _deleteSaved.Click += async (_, _) => await GuardAsync(DeleteSavedRuleAsync);
        _ruleList.SelectionChanged += (_, _) => RefreshRuleActions();
        _savedSelector.SelectionChanged += (_, _) => RefreshRuleActions();
        _settingsPanel.Children.Add(Ui.Text("视频采样帧数", "caption")); _settingsPanel.Children.Add(_frames);
        _settingsPanel.Children.Add(Ui.Text("标签阈值", "caption")); _settingsPanel.Children.Add(_tagThreshold);
        _settingsPanel.Children.Add(_gpu);
        _settingsPanel.Children.Add(Ui.Button("模型管理…", async () => await GuardAsync(() => _manageModels(this))));
        _settingsPanel.Children.Add(Ui.Text("分类目录", "settingsHeading"));
        Localization.SetIsUserText(_output, true); _settingsPanel.Children.Add(_output);
        _settingsPanel.Children.Add(Ui.Button("选择分类目录…", async () => await GuardAsync(async () =>
        { if (await Ui.Folder(this, "选择分类目录") is { } folder) _output.Text = folder; })));
        _settingsPanel.Children.Add(_splitTypes); _settingsPanel.Children.Add(_writeText); _settingsPanel.Children.Add(_mode);
        _settingsPanel.Children.Add(Ui.Text("空镜指不含可见人物的场景。视频按采样画面分类。", "caption"));
        body.Children.Add(new ScrollViewer { Content = _settingsPanel });
        var table = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 4 };
        var header = new Grid { ColumnDefinitions = new("30,*,100"), Classes = { "table-header" } };
        var name = Ui.Text("文件 / 标签 / 分类目录", "caption"); Grid.SetColumn(name, 1); header.Children.Add(name);
        var status = Ui.Text("状态", "caption"); Grid.SetColumn(status, 2); header.Children.Add(status); table.Children.Add(header);
        _files.ItemsSource = _entries;
        _files.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(PaddingProperty, new Thickness(6)), new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _files.ItemTemplate = new FuncDataTemplate<MediaFileEntry>((entry, _) =>
        {
            var row = new Grid { ColumnDefinitions = new("30,*,100"), RowDefinitions = new("Auto,Auto,Auto"), RowSpacing = 3 };
            var check = new CheckBox { MinHeight = 22 };
            check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(MediaFileEntry.Include)) { Mode = BindingMode.TwoWay }); row.Children.Add(check);
            foreach (var (property, line) in new[] { (nameof(MediaFileEntry.Name), 0), (nameof(MediaFileEntry.Details), 1), (nameof(MediaFileEntry.NewName), 2) })
            {
                var text = Ui.Text("", "caption"); Localization.SetIsUserText(text, true);
                text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.Bind(TextBlock.TextProperty, new Binding(property)); text.Bind(ToolTip.TipProperty, new Binding(line == 0 ? nameof(MediaFileEntry.Path) : property));
                Grid.SetColumn(text, 1); Grid.SetRow(text, line); if (line > 0) Grid.SetColumnSpan(text, 2); row.Children.Add(text);
            }
            var state = Ui.Text("", "caption"); state.Bind(TextBlock.TextProperty, new Binding(nameof(MediaFileEntry.Status)));
            Grid.SetColumn(state, 2); row.Children.Add(state); return row;
        });
        Grid.SetRow(_files, 1); table.Children.Add(_files); Grid.SetColumn(table, 1); body.Children.Add(table);
        var detailsScroll = new ScrollViewer { Content = _details }; Grid.SetColumn(detailsScroll, 2); body.Children.Add(detailsScroll);
        Grid.SetRow(body, 1); root.Children.Add(body);
        Grid.SetRow(_activity, 2); root.Children.Add(_activity);
        var footer = new StackPanel { Spacing = 6 };
        _scanErrors.IsVisible = false; _scanErrors.MaxHeight = 60; footer.Children.Add(new ScrollViewer { Content = _scanErrors, MaxHeight = 60 });
        footer.Children.Add(_status);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in new[] { _stop, _undo, _export, _retry, _analyze, _preview, _organize })
        { button.Margin = new(6, 0, 0, 6); actions.Children.Add(button); }
        actions.Children.Add(Ui.DialogButton("关闭", Close)); footer.Children.Add(actions); Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        _analyze.Click += async (_, _) => await GuardAsync(() => AnalyzeAsync(false));
        _retry.Click += async (_, _) => await GuardAsync(() => AnalyzeAsync(true));
        _preview.Click += async (_, _) => await GuardAsync(PreviewAsync);
        _organize.Click += async (_, _) => await GuardAsync(OrganizeAsync);
        _undo.Click += async (_, _) => await GuardAsync(UndoAsync);
        _export.Click += async (_, _) => await GuardAsync(ExportAsync);
        _stop.Click += (_, _) => _operation?.Cancel();
        _output.TextChanged += (_, _) => InvalidatePlan();
        _splitTypes.IsCheckedChanged += (_, _) => InvalidatePlan(); _writeText.IsCheckedChanged += (_, _) => InvalidatePlan();
        _frames.ValueChanged += (_, _) => InvalidateAnalysis(); _tagThreshold.ValueChanged += (_, _) => Reclassify();
        _recursive.IsCheckedChanged += async (_, _) => await ScanAsync();
        RenderDetails();
    }

    private void AddImport(string label, Func<Task> action)
    { var button = Ui.Button(label, async () => await GuardAsync(action)); button.Margin = new(0, 0, 6, 6); _imports.Children.Add(button); }
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (!_closed) _status.Text = Localization.Text("已停止"); }
        catch (Exception error) { if (!_closed) await Ui.Message(this, "文件夹分类", error.Message); }
    }

    private void RenderDetails()
    {
        _details.Children.Clear();
        if (_files.SelectedItem is not MediaFileEntry entry) { _details.Children.Add(Ui.Text("选择文件查看分类结果", "caption")); return; }
        var title = Ui.Text(entry.Name, "settingsHeading"); Localization.SetIsUserText(title, true); ToolTip.SetTip(title, entry.Path); _details.Children.Add(title);
        var path = Ui.Text(entry.Path, "caption"); Localization.SetIsUserText(path, true); _details.Children.Add(path);
        _details.Children.Add(Ui.Button("打开文件", OpenSelected));
        if (!_results.TryGetValue(entry.Path, out var result)) { _details.Children.Add(Ui.Text(entry.Status, "caption")); return; }
        foreach (var decision in result.Decisions)
        {
            var label = Ui.Text(decision.Name, "settingsHeading"); Localization.SetIsUserText(label, true); _details.Children.Add(label);
            var evidence = Ui.Text(Localization.Text(decision.Evidence), "caption");
            var answer = new ComboBox { ItemsSource = new[] { "待确认", "是", "否" }.Select(Localization.Text).ToArray(), SelectedIndex = (int)decision.Answer, IsEnabled = !_busy };
            answer.SelectionChanged += (_, _) =>
            {
                if (_busy || answer.SelectedIndex < 0) return;
                var updated = _results[entry.Path];
                _results[entry.Path] = updated with { Decisions = updated.Decisions.Select(item => item.RuleId == decision.RuleId
                    ? item with { Answer = (BinaryMediaAnswer)answer.SelectedIndex, Manual = true, Evidence = "人工确认" } : item).ToArray() };
                evidence.Text = Localization.Text("人工确认"); UpdateEntry(entry); InvalidatePlan();
            };
            _details.Children.Add(answer);
            _details.Children.Add(evidence);
            var scores = string.Join(" · ", new[] { decision.PositiveScore is { } yes ? $"是 {yes:0.000}" : "",
                decision.NegativeScore is { } no ? $"否 {no:0.000}" : "", decision.Seconds is { } seconds ? $"{seconds:0.00}s" : "" }.Where(text => text.Length > 0));
            _details.Children.Add(Ui.Text(scores, "caption"));
        }
        _details.Children.Add(Ui.Text("自动标签", "settingsHeading"));
        var tags = Ui.Text(string.Join(" · ", result.Tags), "caption"); Localization.SetIsUserText(tags, true); _details.Children.Add(tags);
    }
}
