using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow
{
    private sealed record RuleEditResult(FolderClassificationRule Rule, bool MakeDefault);

    private static void ValidateSavedRules(IReadOnlyList<FolderClassificationRule> rules)
    {
        if (rules.Count > 100) throw new ArgumentException("最多保存 100 个分类。");
        foreach (var rule in rules) rule.Validate();
        if (rules.Select(rule => rule.Id).Distinct().Count() != rules.Count
            || rules.Select(rule => rule.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rules.Count)
            throw new ArgumentException("已保存分类的名称或标识重复。");
    }

    private FolderClassificationRule[] SavedWith(FolderClassificationRule rule)
    {
        var saved = _savedRules.Where(item => item.Id != rule.Id).Append(rule).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        ValidateSavedRules(saved); return saved;
    }

    private FolderClassificationRule[] ActiveWith(FolderClassificationRule rule)
    {
        var active = _rules.ToList(); var index = active.FindIndex(item => item.Id == rule.Id);
        if (index < 0) active.Add(rule); else active[index] = rule;
        FolderClassification.ValidateRules(active); return active.ToArray();
    }

    private FolderClassificationRule[] DefaultsWith(FolderClassificationRule rule)
    {
        var defaults = _defaultRules.Select(item => item.Id == rule.Id ? rule : item).ToArray();
        FolderClassification.ValidateRules(defaults); return defaults;
    }

    private void RefreshSavedRules(FolderClassificationRule[] saved, string? selectedId)
    {
        _savedRules.Clear(); foreach (var rule in saved) _savedRules.Add(rule);
        _savedSelector.SelectedItem = _savedRules.FirstOrDefault(rule => rule.Id == selectedId) ?? _savedRules.FirstOrDefault();
        RefreshRuleActions();
    }

    private void ApplyActiveRules(FolderClassificationRule[] active, string? selectedId = null)
    {
        _rules.Clear(); foreach (var rule in active) _rules.Add(rule);
        _ruleList.SelectedItem = _rules.FirstOrDefault(rule => rule.Id == selectedId) ?? _rules.FirstOrDefault();
        InvalidateAnalysis();
    }

    private void RefreshRuleActions()
    {
        _saveRule.IsEnabled = !_busy && _ruleList.SelectedItem is FolderClassificationRule;
        var currentIsDefault = SameRules(_rules, _defaultRules);
        _setDefault.IsEnabled = _restoreDefault.IsEnabled = !_busy && !currentIsDefault;
        _useSaved.IsEnabled = _deleteSaved.IsEnabled = !_busy && _savedSelector.SelectedItem is FolderClassificationRule;
        _defaultSummary.Text = Localization.Format($"默认分类：{(_defaultRules.Length == 0 ? Localization.Text("仅按类型分类") : string.Join(" · ", _defaultRules.Select(rule => rule.Name)))}");
    }

    private Task SaveSelectedRuleAsync()
    {
        if (_busy || _ruleList.SelectedItem is not FolderClassificationRule rule) return Task.CompletedTask;
        var saved = SavedWith(rule); var defaults = DefaultsWith(rule);
        SavePreferences(defaults, saved); _defaultRules = defaults; RefreshSavedRules(saved, rule.Id);
        _status.Text = Localization.Format($"已保存分类：{rule.Name}"); return Task.CompletedTask;
    }

    private Task SetDefaultRulesAsync()
    {
        if (_busy) return Task.CompletedTask;
        var defaults = _rules.ToArray(); FolderClassification.ValidateRules(defaults);
        SavePreferences(defaultRules: defaults); _defaultRules = defaults; RefreshRuleActions();
        _status.Text = Localization.Text("当前分类已设为默认，下次打开自动载入"); return Task.CompletedTask;
    }

    private void RestoreDefaultRules()
    {
        if (_busy) return;
        ApplyActiveRules(_defaultRules); _status.Text = Localization.Text("已载入默认分类");
    }

    private static bool SameRules(IEnumerable<FolderClassificationRule> first, IEnumerable<FolderClassificationRule> second)
        => first.Select(rule => (rule.Id, rule.Name, rule.UseAutomaticSettings, rule.Threshold, rule.Margin, rule.MinimumAgreement,
            Categories: string.Join("\n", rule.Categories.Select(category => category.Id + "\t" + category.Name + "\t" + category.Description))))
        .SequenceEqual(second.Select(rule => (rule.Id, rule.Name, rule.UseAutomaticSettings, rule.Threshold, rule.Margin, rule.MinimumAgreement,
            Categories: string.Join("\n", rule.Categories.Select(category => category.Id + "\t" + category.Name + "\t" + category.Description)))));

    private FolderClassificationRule[] ReplaceSelectedGroup(FolderClassificationRule rule)
    {
        var active = _rules.ToList();
        var index = active.FindIndex(item => item.Id == rule.Id);
        if (index < 0 && _ruleList.SelectedItem is FolderClassificationRule selected) index = active.FindIndex(item => item.Id == selected.Id);
        if (index < 0 && active.Count == 1) index = 0;
        if (index < 0) active.Add(rule); else active[index] = rule;
        FolderClassification.ValidateRules(active); return active.ToArray();
    }

    private Task UseSavedRuleAsync()
    {
        if (_busy || _savedSelector.SelectedItem is not FolderClassificationRule rule) return Task.CompletedTask;
        var active = ReplaceSelectedGroup(rule);
        if (!SameRules(_rules, active)) ApplyActiveRules(active, rule.Id); else _ruleList.SelectedItem = _rules.First(item => item.Id == rule.Id);
        _status.Text = Localization.Format($"已使用分类：{rule.Name}"); return Task.CompletedTask;
    }

    private Task DeleteSavedRuleAsync()
    {
        if (_busy || _savedSelector.SelectedItem is not FolderClassificationRule rule) return Task.CompletedTask;
        var saved = _savedRules.Where(item => item.Id != rule.Id).ToArray();
        var defaults = _defaultRules.Where(item => item.Id != rule.Id).ToArray();
        SavePreferences(defaults, saved); _defaultRules = defaults; RefreshSavedRules(saved, null);
        if (_rules.Any(item => item.Id == rule.Id)) ApplyActiveRules(_rules.Where(item => item.Id != rule.Id).ToArray());
        _status.Text = Localization.Format($"已删除分类：{rule.Name}"); return Task.CompletedTask;
    }

    private Task AddPresetAsync(FolderClassificationRule rule)
    {
        var active = ReplaceSelectedGroup(rule);
        if (!SameRules(_rules, active)) ApplyActiveRules(active, rule.Id);
        else _ruleList.SelectedItem = _rules.First(item => item.Id == rule.Id);
        _boardRuleId = rule.Id; RenderBoard();
        _status.Text = Localization.Format($"已使用分类：{rule.Name}"); return Task.CompletedTask;
    }

    private sealed record CategoryEditor(string Id, TextBox Name, TextBox Description);

    private async Task EditRuleAsync(FolderClassificationRule? existing)
    {
        var dialog = new Window { Title = "分类设置", Width = 760, Height = 560, MinWidth = 650, MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = Ui.Input(existing?.Name ?? ""); Localization.SetIsUserText(name, true);
        name.Watermark = Localization.Text("如：室内场景");
        var threshold = new NumericUpDown { Minimum = 0, Maximum = 1, Increment = .05m, FormatString = "0.00", Value = (decimal)(existing?.Threshold ?? FolderClassificationRule.DefaultThreshold) };
        var margin = new NumericUpDown { Minimum = 0, Maximum = 1, Increment = .01m, FormatString = "0.00", Value = (decimal)(existing?.Margin ?? FolderClassificationRule.DefaultMargin) };
        var agreement = new NumericUpDown { Minimum = 51, Maximum = 100, Increment = 5, FormatString = "0'%'", Value = (decimal)((existing?.MinimumAgreement ?? FolderClassificationRule.DefaultMinimumAgreement) * 100) };
        var mode = Ui.Combo(["自动", "手动调整"], existing?.UseAutomaticSettings == false ? "手动调整" : "自动");
        var categories = new List<CategoryEditor>();
        foreach (var category in existing?.Categories ?? [new("", "", ""), new("", "", "")])
        {
            var label = Ui.Input(category.Name); var description = Ui.Input(category.Description); description.AcceptsReturn = true; description.MinHeight = 48;
            Localization.SetIsUserText(label, true); Localization.SetIsUserText(description, true);
            categories.Add(new(category.Id.Length == 0 ? Guid.NewGuid().ToString("N") : category.Id, label, description));
        }
        var rows = new StackPanel { Spacing = 10 };
        var add = new Button { Content = "添加类别", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) =>
        {
            var label = Ui.Input(""); var description = Ui.Input(""); description.AcceptsReturn = true; description.MinHeight = 48;
            Localization.SetIsUserText(label, true); Localization.SetIsUserText(description, true);
            categories.Add(new(Guid.NewGuid().ToString("N"), label, description)); RebuildCategories(); label.Focus();
        };
        void RebuildCategories()
        {
            foreach (var oldRow in rows.Children.OfType<Grid>()) oldRow.Children.Clear();
            rows.Children.Clear();
            foreach (var category in categories)
            {
                var row = new Avalonia.Controls.Grid { ColumnDefinitions = new("140,*,Auto"), ColumnSpacing = 8 };
                category.Name.Watermark = Localization.Text("类别名称"); category.Description.Watermark = Localization.Text("画面特征，如：床、枕头和被褥");
                row.Children.Add(category.Name); Avalonia.Controls.Grid.SetColumn(category.Description, 1); row.Children.Add(category.Description);
                var remove = Ui.Button("移除", () => { categories.Remove(category); RebuildCategories(); });
                remove.IsEnabled = categories.Count > 2; Avalonia.Controls.Grid.SetColumn(remove, 2); row.Children.Add(remove); rows.Children.Add(row);
            }
            add.IsEnabled = categories.Count < 12;
        }
        RebuildCategories();
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(Ui.Text("分类名称", "caption")); body.Children.Add(name);
        body.Children.Add(Ui.Text("类别与画面描述", "settingsHeading")); body.Children.Add(rows); body.Children.Add(add);
        body.Children.Add(Ui.Text("类别名称用作文件夹名，不确定的文件放入待确认。", "caption"));
        var modeRow = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        modeRow.Children.Add(Ui.Text("识别设置", "caption")); Grid.SetColumn(mode, 1); modeRow.Children.Add(mode); body.Children.Add(modeRow);
        var limits = new Avalonia.Controls.Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 12 };
        var fields = new[] { ("最低匹配分数", threshold), ("与其他类别的最小差距", margin), ("视频画面一致率", agreement) };
        for (var index = 0; index < fields.Length; index++)
        { var field = new StackPanel { Spacing = 6 }; field.Children.Add(Ui.Text(fields[index].Item1, "caption")); field.Children.Add(fields[index].Item2); Avalonia.Controls.Grid.SetColumn(field, index); limits.Children.Add(field); }
        ToolTip.SetTip(threshold, Localization.Text("匹配分数是语义相似度，范围为 0–1。低于此分数的画面进入待确认。"));
        ToolTip.SetTip(margin, Localization.Text("第一名与第二名的分数差距小于此值时，画面进入待确认。"));
        ToolTip.SetTip(agreement, Localization.Text("视频中至少有这一比例的采样画面命中同一类别，才自动归类。"));
        var advancedBody = new StackPanel { Spacing = 10 };
        advancedBody.Children.Add(limits); advancedBody.Children.Add(Ui.Text("数值越高，分类越谨慎。", "caption"));
        var makeDefault = new CheckBox { Content = "设为默认分类" }; advancedBody.Children.Add(makeDefault);
        ToolTip.SetTip(makeDefault, Localization.Text("将当前分类规则设为下次打开时的默认分类"));
        var advanced = new Expander { Header = "高级设置", Content = advancedBody, HorizontalAlignment = HorizontalAlignment.Stretch };
        void UpdateMode()
        {
            var automatic = mode.SelectedIndex == 0;
            if (automatic)
            {
                threshold.Value = (decimal)FolderClassificationRule.DefaultThreshold;
                margin.Value = (decimal)FolderClassificationRule.DefaultMargin;
                agreement.Value = (decimal)(FolderClassificationRule.DefaultMinimumAgreement * 100);
            }
            limits.IsEnabled = !automatic; advanced.IsExpanded = !automatic;
        }
        mode.SelectionChanged += (_, _) => UpdateMode(); UpdateMode(); body.Children.Add(advanced);
        if (existing?.Id == "age-appearance") body.Children.Add(Ui.Text("外观年龄段是粗略判断，无法确认真实年龄。多人或不清晰画面请人工核对。", "caption"));
        var errorText = Ui.Text("", "caption"); errorText.IsVisible = false; body.Children.Add(errorText);
        void ShowError(string message) { errorText.Text = message; errorText.IsVisible = true; }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(Ui.DialogButton("取消", () => dialog.Close()));
        buttons.Children.Add(Ui.DialogButton("保存", () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name.Text))
                { name.Focus(); ShowError(Localization.Text("请填写分类名称。")); return; }
                var missingName = categories.FirstOrDefault(category => string.IsNullOrWhiteSpace(category.Name.Text));
                if (missingName is not null)
                { missingName.Name.Focus(); ShowError(Localization.Text("请填写类别名称。")); return; }
                var missing = categories.FirstOrDefault(category => string.IsNullOrWhiteSpace(category.Description.Text));
                if (missing is not null)
                { missing.Description.Focus(); ShowError(Localization.Format($"请为“{missing.Name.Text}”填写画面描述。")); return; }
                var choices = categories.Select(category => new FolderClassificationCategory(category.Id, category.Name.Text?.Trim() ?? "",
                    string.Join(" ", (category.Description.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))).ToArray();
                var rule = new FolderClassificationRule(existing?.Id ?? Guid.NewGuid().ToString("N"), name.Text?.Trim() ?? "", choices)
                {
                    UseAutomaticSettings = mode.SelectedIndex == 0,
                    Threshold = (double)(threshold.Value ?? (decimal)FolderClassificationRule.DefaultThreshold),
                    Margin = (double)(margin.Value ?? (decimal)FolderClassificationRule.DefaultMargin),
                    MinimumAgreement = (double)(agreement.Value ?? (decimal)(FolderClassificationRule.DefaultMinimumAgreement * 100)) / 100
                };
                var activeRules = ActiveWith(rule); var savedRules = SavedWith(rule);
                var defaults = makeDefault.IsChecked == true ? activeRules : DefaultsWith(rule);
                SavePreferences(defaults, savedRules); dialog.Close(new RuleEditResult(rule, makeDefault.IsChecked == true));
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { ShowError(error.Message); }
        }));
        var root = new Avalonia.Controls.Grid { RowDefinitions = new("*,Auto"), Margin = new(18), RowSpacing = 12 };
        root.Children.Add(new ScrollViewer { Content = body }); Avalonia.Controls.Grid.SetRow(buttons, 1); root.Children.Add(buttons); dialog.Content = root;
        if (await dialog.ShowDialog<RuleEditResult?>(this) is not { } result) return;
        var active = ActiveWith(result.Rule); var saved = SavedWith(result.Rule);
        _defaultRules = result.MakeDefault ? active : DefaultsWith(result.Rule);
        RefreshSavedRules(saved, result.Rule.Id); ApplyActiveRules(active, result.Rule.Id);
        _status.Text = result.MakeDefault ? Localization.Text("分类已保存，当前规则已设为默认") : Localization.Format($"已保存分类：{result.Rule.Name}");
    }

    private void Reclassify()
    {
        if (_syncing || _busy) return;
        foreach (var path in _results.Keys.ToArray())
        {
            var previous = _results[path];
            var classified = FolderClassification.Classify(previous.Media, _rules.ToArray(), (double)(_tagThreshold.Value ?? .5m));
            _results[path] = FolderClassification.KeepManual(classified, previous);
        }
        foreach (var entry in _entries) UpdateEntry(entry);
        InvalidatePlan(); RenderDetails();
    }

    private void UpdateEntry(MediaFileEntry entry)
    {
        if (!_results.TryGetValue(entry.Path, out var result)) return;
        entry.Status = Localization.Text(result.Decisions.Any(decision => decision.NeedsReview) ? "待确认" : "已分类");
        entry.Details = string.Join(" · ", result.Decisions.Select(decision => decision.Name + ": " + (decision.NeedsReview ? Localization.Text("待确认") : decision.CategoryName))
            .Concat(result.Tags.Take(4)));
        QueueBoardRefresh();
    }
}
