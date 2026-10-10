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
        RefreshSavedVisibility();
    }

    private void ApplyActiveRules(FolderClassificationRule[] active, string? selectedId = null)
    {
        _rules.Clear(); foreach (var rule in active.Where(RuleVisible)) _rules.Add(rule);
        _ruleList.SelectedItem = _rules.FirstOrDefault(rule => rule.Id == selectedId) ?? _rules.FirstOrDefault();
        InvalidateAnalysis();
    }

    private void RefreshRuleActions()
    {
        _saveRule.IsEnabled = !_busy && _ruleList.SelectedItem is FolderClassificationRule;
        var currentIsDefault = SameRules(_rules, VisibleDefaults);
        _setDefault.IsEnabled = _restoreDefault.IsEnabled = !_busy && !currentIsDefault;
        _useSaved.IsEnabled = _deleteSaved.IsEnabled = !_busy && _savedSelector.SelectedItem is FolderClassificationRule;
        _defaultSummary.Text = Localization.Format($"默认分类：{(VisibleDefaults.Length == 0 ? Localization.Text("仅按类型分类") : string.Join(" · ", VisibleDefaults.Select(rule => rule.Name)))}");
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
        ApplyActiveRules(VisibleDefaults); _status.Text = Localization.Text("已载入默认分类");
    }

    private static bool SameRules(IEnumerable<FolderClassificationRule> first, IEnumerable<FolderClassificationRule> second)
        => System.Text.Json.JsonSerializer.Serialize(first.ToArray()) == System.Text.Json.JsonSerializer.Serialize(second.ToArray());

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
        if (rule.ByOutfit) _splitTypes.IsChecked = false;
        _boardRuleId = rule.Id; RenderBoard();
        _status.Text = Localization.Format($"已使用分类：{rule.Name}"); return Task.CompletedTask;
    }

    private sealed record CategoryEditor(string Id, TextBox Name, TextBox Description);

    private async Task EditRuleAsync(FolderClassificationRule? existing)
    {
        if (existing is { ByOutfit: true }) { await EditOutfitRuleAsync(existing); return; }
        if (existing is { ByDuration: true }) { await Ui.Message(this, "视频长短", "视频长短按源文件时长分类，无需填写画面描述。"); return; }
        var dialog = new Window { Title = "分类设置", Width = 760, Height = 560, MinWidth = 650, MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = Ui.Input(existing?.Name ?? ""); Localization.SetIsUserText(name, true);
        name.Watermark = Localization.Text("如：室内场景");
        var threshold = new NumericUpDown { Minimum = 0, Maximum = 1, Increment = .05m, FormatString = "0.00", Value = (decimal)(existing?.Threshold ?? FolderClassificationRule.DefaultThreshold) };
        var margin = new NumericUpDown { Minimum = 0, Maximum = 1, Increment = .01m, FormatString = "0.00", Value = (decimal)(existing?.Margin ?? FolderClassificationRule.DefaultMargin) };
        var agreement = new NumericUpDown { Minimum = 51, Maximum = 100, Increment = 5, FormatString = "0'%'", Value = (decimal)((existing?.MinimumAgreement ?? FolderClassificationRule.DefaultMinimumAgreement) * 100) };
        var mode = Ui.Combo(["自动", "手动调整"], existing?.UseAutomaticSettings == false ? "手动调整" : "自动");
        var categories = new List<CategoryEditor>();
        var existingDetection = existing is null ? null : FolderNippleClassification.Resolve(existing);
        foreach (var category in existing?.Categories ?? [new("", "", ""), new("", "", "")])
        {
            var label = Ui.Input(category.Name); var description = Ui.Input(category.Description); description.AcceptsReturn = true; description.MinHeight = 48;
            Localization.SetIsUserText(label, true); Localization.SetIsUserText(description, true);
            categories.Add(new(category.Id.Length == 0 ? Guid.NewGuid().ToString("N") : category.Id, label, description));
        }
        FolderNippleDetection? CurrentDetection() => existingDetection ?? FolderNippleClassification.Resolve(categories
            .Select(category => new FolderClassificationCategory(category.Id, category.Name.Text?.Trim() ?? "", "")).ToArray());
        var rows = new StackPanel { Spacing = 10 };
        Action? updateSettings = null;
        var add = new Button { Content = "添加类别", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) =>
        {
            var label = Ui.Input(""); var description = Ui.Input(""); description.AcceptsReturn = true; description.MinHeight = 48;
            Localization.SetIsUserText(label, true); Localization.SetIsUserText(description, true);
            label.TextChanged += (_, _) => updateSettings?.Invoke();
            categories.Add(new(Guid.NewGuid().ToString("N"), label, description)); RebuildCategories(); updateSettings?.Invoke(); label.Focus();
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
                var remove = Ui.Button("移除", () => { categories.Remove(category); RebuildCategories(); updateSettings?.Invoke(); });
                remove.IsEnabled = categories.Count > 2; Avalonia.Controls.Grid.SetColumn(remove, 2); row.Children.Add(remove); rows.Children.Add(row);
            }
            add.IsEnabled = categories.Count < 12 && CurrentDetection() is null;
        }
        RebuildCategories();
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(Ui.Text("分类名称", "caption")); body.Children.Add(name);
        body.Children.Add(Ui.Text("类别与画面描述", "settingsHeading")); body.Children.Add(rows); body.Children.Add(add);
        body.Children.Add(Ui.Text("类别名称用作文件夹名，不确定的文件放入待确认。", "caption"));
        var detectionHint = Ui.Text("按乳头是否裸露分类；视频采样中检出一次即归入露点。", "caption"); body.Children.Add(detectionHint);
        var modeRow = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        modeRow.Children.Add(Ui.Text("识别设置", "caption")); Grid.SetColumn(mode, 1); modeRow.Children.Add(mode); body.Children.Add(modeRow);
        var limits = new Avalonia.Controls.Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 12 };
        var fields = new[] { ("最低匹配分数", threshold), ("与其他类别的最小差距", margin), ("视频画面一致率", agreement) };
        var fieldLabels = new List<TextBlock>(); var fieldPanels = new List<StackPanel>();
        for (var index = 0; index < fields.Length; index++)
        {
            var field = new StackPanel { Spacing = 6 }; var label = Ui.Text(fields[index].Item1, "caption");
            fieldLabels.Add(label); fieldPanels.Add(field); field.Children.Add(label);
            field.Children.Add(Ui.Parameter(fields[index].Item2,fields[index].Item1)); Grid.SetColumn(field, index); limits.Children.Add(field);
        }

        var advancedBody = new StackPanel { Spacing = 10 };
        var advancedHint = Ui.Text("数值越高，分类越谨慎。", "caption");
        advancedBody.Children.Add(limits); advancedBody.Children.Add(advancedHint);
        var makeDefault = new CheckBox { Content = "设为默认分类" }; advancedBody.Children.Add(makeDefault);
        var privateGroup = new CheckBox { Content = "NSFW 私密分组", IsChecked = existing is not null && MediaPrivacy.IsSensitiveRule(existing), IsVisible = _settings.EnableNsfwContent };
        advancedBody.Children.Add(privateGroup);

        var advanced = new Expander { Header = "高级设置", Content = advancedBody, HorizontalAlignment = HorizontalAlignment.Stretch };
        void UpdateMode()
        {
            var automatic = mode.SelectedIndex == 0;
            var detectsNipples = CurrentDetection() is not null;
            var usesTags = existing?.UsesTagScores == true;
            detectionHint.IsVisible = detectsNipples; fieldPanels[2].IsVisible = !detectsNipples && existing?.UsePeakEvidence != true;
            limits.ColumnDefinitions = new(detectsNipples || existing?.UsePeakEvidence == true ? "*,*" : "*,*,*");
            fieldLabels[0].Text = Localization.Text(detectsNipples ? "露点判定分数" : "最低匹配分数");
            fieldLabels[1].Text = Localization.Text(detectsNipples ? "待确认分数范围" : "与其他类别的最小差距");
            for (var index = 0; index < 2; index++)
                if (fieldPanels[index].Children[1] is Panel parameter)
                    foreach (var control in parameter.Children) Avalonia.Automation.AutomationProperties.SetName(control, fieldLabels[index].Text);
            advancedHint.Text = Localization.Text(detectsNipples ? "判定分数下方的这段范围进入待确认。" : "数值越高，分类越谨慎。");

            if (automatic)
            {
                threshold.Value = (decimal)(detectsNipples ? FolderNippleClassification.DefaultThreshold : usesTags ? .4 : FolderClassificationRule.DefaultThreshold);
                margin.Value = (decimal)(detectsNipples ? FolderNippleClassification.DefaultReviewRange : FolderClassificationRule.DefaultMargin);
                agreement.Value = (decimal)((usesTags ? .6 : FolderClassificationRule.DefaultMinimumAgreement) * 100);
            }
            add.IsEnabled = categories.Count < 12 && !detectsNipples && !usesTags;
            limits.IsEnabled = !automatic; advanced.IsExpanded = !automatic;
        }
        updateSettings = UpdateMode;
        foreach (var category in categories) category.Name.TextChanged += (_, _) => UpdateMode();
        dialog.Closed += (_, _) => _settings.NsfwContentChanged -= ClosePrivateEditor;
        void ClosePrivateEditor(object? sender, EventArgs args) { if (!_settings.EnableNsfwContent) dialog.Close(); }
        _settings.NsfwContentChanged += ClosePrivateEditor;
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
                var detection = CurrentDetection();
                var missing = detection is null ? categories.FirstOrDefault(category => string.IsNullOrWhiteSpace(category.Description.Text)) : null;
                if (missing is not null)
                { missing.Description.Focus(); ShowError(Localization.Format($"请为“{missing.Name.Text}”填写画面描述。")); return; }
                var choices = categories.Select(category => new FolderClassificationCategory(category.Id, category.Name.Text?.Trim() ?? "",
                    string.IsNullOrWhiteSpace(category.Description.Text) && detection is not null
                        ? category.Id == detection.ExposedCategoryId ? "画面中能看到裸露乳头。" : "乳头未露出或被衣物遮住。"
                        : string.Join(" ", (category.Description.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
                    { Tags = existing?.Categories.FirstOrDefault(item => item.Id == category.Id)?.Tags ?? [],
                        SupersededBy = existing?.Categories.FirstOrDefault(item => item.Id == category.Id)?.SupersededBy.Where(id => categories.Any(other => other.Id == id)).ToArray() ?? [] }).ToArray();
                var rule = new FolderClassificationRule(existing?.Id ?? Guid.NewGuid().ToString("N"), name.Text?.Trim() ?? "", choices)
                {
                    NippleDetection = detection,
                    IsNsfw = privateGroup.IsChecked == true || detection is not null,
                    FallbackCategoryId = choices.Any(category => category.Id == existing?.FallbackCategoryId) ? existing?.FallbackCategoryId : null,
                    UsePeakEvidence = existing?.UsePeakEvidence == true,
                    UseAutomaticSettings = mode.SelectedIndex == 0,
                    Threshold = (double)(threshold.Value ?? (decimal)FolderClassificationRule.DefaultThreshold),
                    Margin = (double)(margin.Value ?? (decimal)FolderClassificationRule.DefaultMargin),
                    MinimumAgreement = (double)(agreement.Value ?? (decimal)(FolderClassificationRule.DefaultMinimumAgreement * 100)) / 100
                };
                if (!RuleVisible(rule)) { ShowError(Localization.Text("请先在设置中启用 NSFW 分组与标签。")); return; }
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
            var classified = FolderClassification.Classify(previous.Media, _rules.ToArray(), (double)(_tagThreshold.Value ?? .5m), _settings.EnableNsfwContent);
            _results[path] = FolderClassification.KeepManual(classified, previous);
        }
        RegroupOutfits();
        foreach (var entry in _entries) UpdateEntry(entry);
        InvalidatePlan(); RenderDetails();
    }

    private void UpdateEntry(MediaFileEntry entry)
    {
        if (!_results.TryGetValue(entry.Path, out var result)) return;
        entry.Status = Localization.Text(AnalysisPending(entry) ? "待分析" : result.Decisions.Any(decision => decision.NeedsReview) ? "待确认" : "已分类");
        entry.Details = string.Join(" · ", result.Decisions.Select(decision => decision.Name + ": " +
            (_rules.FirstOrDefault(rule => rule.Id == decision.RuleId) is { } rule && OutfitPending(result, rule)
                ? Localization.Text("待分析") : decision.NeedsReview ? Localization.Text("待确认") : decision.CategoryName))
            .Concat(result.Tags.Take(4)));
        QueueBoardRefresh();
    }
}
