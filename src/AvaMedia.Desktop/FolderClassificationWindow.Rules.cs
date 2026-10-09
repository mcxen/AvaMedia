using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow
{
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
            Value = (decimal)(existing?.Kind == FolderRuleKind.Tags ? existing.NegativeThreshold : existing?.Margin ?? .04) };
        var body = new StackPanel { Spacing = 8 };
        void Field(string text, Control control) { body.Children.Add(Ui.Text(text, "caption")); body.Children.Add(control); }
        Field("分类名称", name);
        if (existing?.Kind == FolderRuleKind.Tags)
            body.Children.Add(Ui.Text("检测标签：" + string.Join(" · ", existing.Tags.Select(WordLibraryCatalog.TagLabel)), "caption"));
        else
        {
            Field("“是”的画面描述", positive); Field("“否”的画面描述", negative);
            body.Children.Add(Ui.Text("描述具体画面；两侧须能区分。", "caption"));
        }
        Field("命中阈值", threshold); Field(existing?.Kind == FolderRuleKind.Tags ? "未检出阈值" : "最小分差", secondary);
        var errorText = Ui.Text("", "caption"); body.Children.Add(errorText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(Ui.DialogButton("取消", () => dialog.Close()));
        buttons.Children.Add(Ui.DialogButton("保存", () =>
        {
            try
            {
                var rule = (existing ?? new(Guid.NewGuid().ToString("N"), "", FolderRuleKind.Semantic, "", "", [])) with
                { Name = name.Text?.Trim() ?? "", PositiveDescription = positive.Text?.Trim() ?? "", NegativeDescription = negative.Text?.Trim() ?? "",
                    Threshold = (double)(threshold.Value ?? .5m), Margin = existing?.Kind == FolderRuleKind.Tags ? existing.Margin : (double)(secondary.Value ?? .04m),
                    NegativeThreshold = existing?.Kind == FolderRuleKind.Tags ? (double)(secondary.Value ?? .15m) : 0 };
                FolderClassification.ValidateRules(_rules.Where(item => item != existing).Append(rule).ToArray());
                dialog.Close(rule);
            }
            catch (ArgumentException error) { errorText.Text = error.Message; }
        }));
        var root = new Grid { RowDefinitions = new("*,Auto"), Margin = new(18), RowSpacing = 12 };
        root.Children.Add(new ScrollViewer { Content = body }); Grid.SetRow(buttons, 1); root.Children.Add(buttons); dialog.Content = root;
        if (await dialog.ShowDialog<FolderClassificationRule?>(this) is not { } saved) return;
        if (existing is null) _rules.Add(saved); else _rules[_rules.IndexOf(existing)] = saved;
        _ruleList.SelectedItem = saved; InvalidateAnalysis(); SavePreferences();
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
