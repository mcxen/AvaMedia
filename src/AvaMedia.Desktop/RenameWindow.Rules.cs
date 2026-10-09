using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class RenameWindow
{
    private const int MaximumRules = 100;
    private static readonly JsonSerializerOptions RuleJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly HashSet<Control> _invalidRuleInputs = [];
    private void InitializeCatalog()
    {
        Category("名称与文本", [
            ("新名称模板", new(RenameAction.Template) { Text = "{name}" }),
            ("添加文本", new(RenameAction.Add) { Separator = "" }),
            ("替换文本", new(RenameAction.Replace)),
            ("正则替换", new(RenameAction.Replace) { Regex = true }),
            ("删除字符", new(RenameAction.Remove)),
            ("移动字符", new(RenameAction.Move))]);
        Category("规范与清理", [("大小写", new(RenameAction.Case)), ("清理空白", new(RenameAction.Cleanup))]);
        Category("编号与日期", [("自动编号", new(RenameAction.Number)), ("文件日期", new(RenameAction.Date))]);
        Category("扩展名", [("设置扩展名", new(RenameAction.Extension)), ("扩展名大小写", new(RenameAction.ExtensionCase))]);
        Category("媒体信息", [("按媒体信息命名", new(RenameAction.Template) { Text = "{name}_{width}x{height}" })]);
        if (_settings.EnableBetaFeatures)
        {
            var panel = new StackPanel { Spacing = 5 }; panel.Children.Add(Ui.Text("智能命名 · Beta", "settingsHeading"));
            panel.Children.Add(Ui.Button("关键词匹配", () => { _semantic.IsChecked = true; _semanticPanel.BringIntoView(); })); _catalog.Children.Add(panel);
        }
        void Category(string name, (string Label, RenameOperation Rule)[] tools)
        {
            var items = new StackPanel { Spacing = 3 };
            foreach (var (label, rule) in tools)
            { var button = Ui.Button(label, () => AddRule(label, rule)); button.HorizontalAlignment = HorizontalAlignment.Stretch; items.Children.Add(button); }
            _catalog.Children.Add(new Expander { Header = name, IsExpanded = true, Content = items, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
    }
    private void AddRule(string label, RenameOperation operation)
    {
        if (_rules.Count >= MaximumRules) { _progressText.Text = Localization.Text("最多可添加 100 条命名规则。"); return; }
        var step = new RenameStep(label, operation);
        step.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(RenameStep.Operation)) return;
            if (step.Operation.Action == RenameAction.Template && _rules.FirstOrDefault(rule => rule.Operation.Action == RenameAction.Template) == step)
            { _syncingTemplate = true; try { _pattern.Text = step.Operation.Text; } finally { _syncingTemplate = false; } }
            InvalidatePlan();
        };
        _rules.Add(step); RenumberRules(); _ruleList.SelectedItem = step; InvalidatePlan();
    }
    private void RenumberRules() { for (var index = 0; index < _rules.Count; index++) _rules[index].Number = index + 1; }
    private void MoveRule(int offset)
    {
        if (_ruleList.SelectedItem is not RenameStep step) return;
        var index = _rules.IndexOf(step); var target = index + offset;
        if (target < 0 || target >= _rules.Count) return;
        _rules.Move(index, target); _ruleList.SelectedItem = step; RenumberRules(); InvalidatePlan();
    }
    private void RemoveRule()
    {
        if (_ruleList.SelectedItem is not RenameStep step) return;
        var index = _rules.IndexOf(step); _rules.Remove(step); RenumberRules();
        _ruleList.SelectedIndex = Math.Min(index, _rules.Count - 1); BuildRuleEditor(); InvalidatePlan();
    }
    private void SyncSemanticTemplate()
    {
        if (_syncingTemplate || _closed) return;
        var step = _rules.FirstOrDefault(rule => rule.Operation.Action == RenameAction.Template);
        if (step is null) AddRule("新名称模板", new(RenameAction.Template) { Text = _pattern.Text ?? "" });
        else { step.Operation = step.Operation with { Text = _pattern.Text ?? "" }; if (_ruleList.SelectedItem == step) BuildRuleEditor(); }
    }
    private void BuildRuleEditor()
    {
        _ruleEditor.Children.Clear(); _invalidRuleInputs.Clear();
        if (_ruleList.SelectedItem is not RenameStep step) { _ruleEditor.Children.Add(Ui.Text("从左侧添加命名规则。", "caption")); return; }
        var rule = step.Operation; _ruleEditor.Children.Add(Ui.Text(step.Label, "settingsHeading"));
        switch (rule.Action)
        {
            case RenameAction.Template:
                var template = Text("命名模板", rule.Text, text => step.Operation = step.Operation with { Text = text });
                var tokens = new WrapPanel();
                foreach (var (label, token) in new[] { ("原名称", "{name}"), ("序号", "{index}"), ("文件夹", "{parent}"), ("宽度", "{width}"), ("高度", "{height}"), ("时长", "{duration}"), ("修改日期", "{modified:yyyyMMdd}") })
                {
                    var button = Ui.Button(label, () => { var position = template.CaretIndex; template.Text = (template.Text ?? "").Insert(position, token); template.CaretIndex = position + token.Length; template.Focus(); });
                    button.Margin = new(0, 0, 4, 4); button.Classes.Add("field-action"); tokens.Children.Add(button);
                }
                _ruleEditor.Children.Add(tokens); _ruleEditor.Children.Add(Ui.Text("扩展名自动保留", "caption")); Numbering(); break;
            case RenameAction.Add:
                Text("添加文本", rule.Text, text => step.Operation = step.Operation with { Text = text }); Placement(); break;
            case RenameAction.Replace:
                Text("查找文本", rule.Find, text => step.Operation = step.Operation with { Find = text });
                Text("替换为", rule.Replacement, text => step.Operation = step.Operation with { Replacement = text });
                Toggle("使用正则表达式", rule.Regex, value => step.Operation = step.Operation with { Regex = value });
                Toggle("忽略大小写", rule.IgnoreCase, value => step.Operation = step.Operation with { IgnoreCase = value }); break;
            case RenameAction.Remove:
            case RenameAction.Move:
                Number("起始位置", 0, 1000000, rule.Position, value => step.Operation = step.Operation with { Position = value });
                Number("字符数量", 1, 1000000, rule.Count, value => step.Operation = step.Operation with { Count = value });
                Toggle("从末尾计算", rule.FromEnd, value => step.Operation = step.Operation with { FromEnd = value });
                if (rule.Action == RenameAction.Move) Number("移至位置", 0, 1000000, rule.Destination, value => step.Operation = step.Operation with { Destination = value });
                _ruleEditor.Children.Add(Ui.Text("位置从 0 开始", "caption")); break;
            case RenameAction.Case:
            case RenameAction.ExtensionCase:
                Choice("大小写", ["小写", "大写", "单词首字母大写", "首字母大写"], (int)rule.Case, value => step.Operation = step.Operation with { Case = (RenameCase)value }); break;
            case RenameAction.Cleanup:
                Choice("清理方式", ["去除首尾空白", "合并连续空白", "空白转下划线", "删除所有空白"], (int)rule.Cleanup, value => step.Operation = step.Operation with { Cleanup = (RenameCleanup)value }); break;
            case RenameAction.Number:
                Numbering(); Placement(); break;
            case RenameAction.Date:
                Choice("日期来源", ["修改时间", "创建时间"], (int)rule.Date, value => step.Operation = step.Operation with { Date = (RenameDate)value });
                var dateFormats = new[] { "yyyyMMdd", "yyyy-MM-dd", "yyyy-MM-dd_HH-mm", "yyyy年MM月dd日", "yyyyMMdd_HHmmss" };
                var dateChoice = Ui.Combo(dateFormats.Append(rule.DateFormat).Distinct(), rule.DateFormat);
                dateChoice.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((pattern, _) => { try { return Ui.Text(DateTime.Today.ToString(pattern)); } catch(FormatException) { return Ui.Text(pattern??""); } });
                AddRow(_ruleEditor,"日期样式",dateChoice);dateChoice.SelectionChanged+=(_,_)=>{if(dateChoice.SelectedItem is string pattern)step.Operation=step.Operation with {DateFormat=pattern};}; Placement(); break;
            case RenameAction.Extension:
                Text("扩展名", rule.Text, text => step.Operation = step.Operation with { Text = text });
                _ruleEditor.Children.Add(Ui.Text("只修改扩展名，不转换文件格式。", "caption")); break;
        }
        void Numbering()
        {
            Number("起始序号", 0, 1000000, rule.Start, value => step.Operation = step.Operation with { Start = value });
            Number("递增步长", 1, 1000000, rule.Step, value => step.Operation = step.Operation with { Step = value });
            Number("序号位数", 1, 12, rule.Digits, value => step.Operation = step.Operation with { Digits = value });
        }
        void Placement()
        {
            Choice("添加位置", ["前缀", "后缀", "指定位置"], (int)rule.Placement, value => { step.Operation = step.Operation with { Placement = (RenamePlacement)value }; BuildRuleEditor(); });
            if (rule.Placement == RenamePlacement.Position)
            {
                Number("字符位置", 0, 1000000, rule.Position, value => step.Operation = step.Operation with { Position = value });
                Toggle("从末尾计算", rule.FromEnd, value => step.Operation = step.Operation with { FromEnd = value });
                _ruleEditor.Children.Add(Ui.Text("位置从 0 开始", "caption"));
            }
            else
            {
                var separator=Text("分隔符",rule.Separator,text=>step.Operation=step.Operation with {Separator=text});
                var choices=new WrapPanel();foreach(var value in new[]{""," ","_","-","."}){var button=Ui.Button(value.Length==0?"无":value==" "?"空格":value,()=>separator.Text=value);button.Margin=new(0,0,4,4);choices.Children.Add(button);}_ruleEditor.Children.Add(choices);
            }
        }
        TextBox Text(string label, string value, Action<string> change)
        { var box = Ui.Input(value); Localization.SetIsUserText(box, true); AddRow(_ruleEditor, label, box); box.TextChanged += (_, _) => change(box.Text ?? ""); return box; }
        void Number(string label, int min, int max, int value, Action<int> change)
        {
            var input = new NumericUpDown { Minimum = min, Maximum = max, Value = value, Increment = 1 }; AddRow(_ruleEditor, label, input);
            input.PropertyChanged += (_, args) =>
            {
                if (args.Property != NumericUpDown.ValueProperty && args.Property != NumericUpDown.TextProperty) return;
                if (!int.TryParse(input.Text, NumberStyles.Integer, input.NumberFormat, out var parsed) || parsed < min || parsed > max)
                { _invalidRuleInputs.Add(input); InvalidatePlan(); }
                else { _invalidRuleInputs.Remove(input); change(parsed); }
            };
        }
        void Toggle(string label, bool value, Action<bool> change)
        { var check = new CheckBox { Content = label, IsChecked = value }; check.IsCheckedChanged += (_, _) => change(check.IsChecked == true); _ruleEditor.Children.Add(check); }
        void Choice(string label, string[] choices, int value, Action<int> change)
        { var combo = new ComboBox { ItemsSource = choices.Select(Localization.Text).ToArray(), SelectedIndex = value, HorizontalAlignment = HorizontalAlignment.Stretch }; AddRow(_ruleEditor, label, combo); combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) change(combo.SelectedIndex); }; }
    }
    private async Task SaveRulesAsync()
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new() { Title = Localization.Text("保存命名规则"), SuggestedFileName = "rename-rules.json", DefaultExtension = "json" });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
            await JsonSerializer.SerializeAsync(stream, _rules.Select(rule => rule.Operation).ToArray(), RuleJson);
        }
        catch (Exception error) { await ShowErrorAsync("保存规则失败", error); }
    }
    private async Task LoadRulesAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = Localization.Text("载入命名规则"), FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }] });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            var rules = await JsonSerializer.DeserializeAsync<RenameOperation[]>(stream, RuleJson) ?? throw new InvalidDataException("命名规则无效。");
            if (rules.Length > MaximumRules || rules.Any(rule => rule is null || rule.Text is null || rule.Find is null || rule.Replacement is null || rule.Separator is null || rule.DateFormat is null
                || !Enum.IsDefined(rule.Action) || !Enum.IsDefined(rule.Placement) || !Enum.IsDefined(rule.Case) || !Enum.IsDefined(rule.Cleanup) || !Enum.IsDefined(rule.Date)
                || rule.Start is < 0 or > 1000000 || rule.Step is < 1 or > 1000000 || rule.Digits is < 1 or > 12
                || rule.Position is < 0 or > 1000000 || rule.Count is < 1 or > 1000000 || rule.Destination is < 0 or > 1000000))
                throw new InvalidDataException("命名规则无效。");
            if (_closed) return;
            _rules.Clear(); foreach (var rule in rules) AddRule(ActionLabel(rule), rule); BuildRuleEditor(); InvalidatePlan();
        }
        catch (Exception error) { await ShowErrorAsync("载入规则失败", error); }
    }
    private static string ActionLabel(RenameOperation rule) => rule.Action switch
    {
        RenameAction.Template => BatchRename.NeedsMediaInfo([rule]) ? "按媒体信息命名" : "新名称模板", RenameAction.Add => "添加文本", RenameAction.Replace => rule.Regex ? "正则替换" : "替换文本",
        RenameAction.Remove => "删除字符", RenameAction.Move => "移动字符", RenameAction.Case => "大小写", RenameAction.Cleanup => "清理空白", RenameAction.Number => "自动编号",
        RenameAction.Date => "文件日期", RenameAction.Extension => "设置扩展名", _ => "扩展名大小写"
    };
}
