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
        var currentIsDefault = _rules.SequenceEqual(_defaultRules);
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

    private Task UseSavedRuleAsync()
    {
        if (_busy || _savedSelector.SelectedItem is not FolderClassificationRule rule) return Task.CompletedTask;
        var active = ActiveWith(rule);
        if (!_rules.SequenceEqual(active)) ApplyActiveRules(active, rule.Id); else _ruleList.SelectedItem = _rules.First(item => item.Id == rule.Id);
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

    private async Task AddPresetAsync(FolderClassificationRule rule)
    {
        if (_rules.Any(existing => existing.Id == rule.Id))
        { _ruleList.SelectedItem = _rules.First(existing => existing.Id == rule.Id); return; }
        FolderClassification.ValidateRules(_rules.Append(rule).ToArray());
        _rules.Add(rule); _ruleList.SelectedItem = rule; InvalidateAnalysis(); SavePreferences();
        await Task.CompletedTask;
    }

    private async Task EditRuleAsync(FolderClassificationRule? existing)
    {
        var dialog = new Window { Title = "二分分类规则", Width = 550, Height = 580, MinWidth = 480, MinHeight = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = Ui.Input(existing?.Name ?? "自定义分类");
        var positive = Ui.Input(existing?.PositiveDescription ?? ""); positive.AcceptsReturn = true; positive.Height = 80;
        var negative = Ui.Input(existing?.NegativeDescription ?? ""); negative.AcceptsReturn = true; negative.Height = 80;
        foreach (var input in new[] { name, positive, negative }) Localization.SetIsUserText(input, true);
        var threshold = new NumericUpDown { Minimum = .01m, Maximum = 1, Increment = .05m, FormatString = "0.00", Value = (decimal)(existing?.Threshold ?? .5) };
        var secondary = new NumericUpDown { Minimum = 0, Maximum = 1, Increment = .01m, FormatString = "0.00",
            Value = (decimal)(existing?.Margin ?? .04) };
        var body = new StackPanel { Spacing = 8 };
        void Field(string text, Control control) { body.Children.Add(Ui.Text(text, "caption")); body.Children.Add(control); }
        Field("分类名称", name);
        Field("“是”的画面描述", positive); Field("“否”的画面描述", negative);
        body.Children.Add(Ui.Text("描述具体画面；两侧须能区分。", "caption"));
        Field("命中阈值", threshold); Field("最小分差", secondary);
        var makeDefault = new CheckBox { Content = "同时将当前规则设为默认" }; body.Children.Add(makeDefault);
        var errorText = Ui.Text("", "caption"); body.Children.Add(errorText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(Ui.DialogButton("取消", () => dialog.Close()));
        buttons.Children.Add(Ui.DialogButton("保存", () =>
        {
            try
            {
                var rule = (existing ?? new(Guid.NewGuid().ToString("N"), "", "", "")) with
                { Name = name.Text?.Trim() ?? "", PositiveDescription = positive.Text?.Trim() ?? "", NegativeDescription = negative.Text?.Trim() ?? "",
                    Threshold = (double)(threshold.Value ?? .5m), Margin = (double)(secondary.Value ?? .04m) };
                var activeRules = ActiveWith(rule); var savedRules = SavedWith(rule);
                var defaults = makeDefault.IsChecked == true ? activeRules : DefaultsWith(rule);
                SavePreferences(defaults, savedRules);
                dialog.Close(new RuleEditResult(rule, makeDefault.IsChecked == true));
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { errorText.Text = error.Message; }
        }));
        var root = new Grid { RowDefinitions = new("*,Auto"), Margin = new(18), RowSpacing = 12 };
        root.Children.Add(new ScrollViewer { Content = body }); Grid.SetRow(buttons, 1); root.Children.Add(buttons); dialog.Content = root;
        if (await dialog.ShowDialog<RuleEditResult?>(this) is not { } result) return;
        var active = ActiveWith(result.Rule); var saved = SavedWith(result.Rule);
        var defaults = result.MakeDefault ? active : DefaultsWith(result.Rule);
        _defaultRules = defaults;
        RefreshSavedRules(saved, result.Rule.Id); ApplyActiveRules(active, result.Rule.Id);
        _status.Text = result.MakeDefault ? Localization.Text("分类已保存，当前规则已设为默认")
            : Localization.Format($"已保存分类：{result.Rule.Name}");
    }

    private void Reclassify()
    {
        if (_syncing || _busy) return;
        foreach (var path in _results.Keys.ToArray())
        {
            var previous = _results[path];
            var classified = FolderClassification.Classify(previous.Media, _rules.ToArray(), (double)(_tagThreshold.Value ?? .5m));
            _results[path] = classified with { Decisions = classified.Decisions.Select(decision =>
                previous.Decisions.FirstOrDefault(old => old.RuleId == decision.RuleId && old.Manual) ?? decision).ToArray() };
        }
        foreach (var entry in _entries) UpdateEntry(entry);
        InvalidatePlan(); RenderDetails();
    }

    private void UpdateEntry(MediaFileEntry entry)
    {
        if (!_results.TryGetValue(entry.Path, out var result)) return;
        entry.Status = Localization.Text(result.Decisions.Any(decision => decision.Answer == BinaryMediaAnswer.Review) ? "待确认" : "已分类");
        entry.Details = string.Join(" · ", result.Decisions.Select(decision => decision.Name + ": " + Localization.Text(FolderClassification.AnswerLabel(decision.Answer)))
            .Concat(result.Tags.Take(4)));
    }
}
