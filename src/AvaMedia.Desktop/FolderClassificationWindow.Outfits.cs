using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow
{
    private Task UseOutfitGroupingAsync()
    {
        var rule = _rules.FirstOrDefault(rule => rule.ByOutfit) ?? FolderClassificationRule.Presets.Single(rule => rule.ByOutfit);
        if (!SameRules(_rules, [rule])) ApplyActiveRules([rule], rule.Id);
        _splitTypes.IsChecked = false; _boardRuleId = rule.Id; RenderBoard();
        _status.Text = Localization.Text("已使用相似服装分组");
        return Task.CompletedTask;
    }

    private async Task EditOutfitRuleAsync(FolderClassificationRule rule)
    {
        var dialog = new Window { Title = "相似服装", Width = 420, Height = 240, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var similarity = new NumericUpDown { Minimum = .5m, Maximum = .95m, Increment = .02m, FormatString = "0.00", Value = (decimal)rule.OutfitSimilarity };
        var body = new StackPanel { Margin = new(18), Spacing = 12 };
        body.Children.Add(Ui.Text("服装相似度", "settingsHeading")); body.Children.Add(Ui.Adjust(similarity));
        body.Children.Add(Ui.Text("调高可拆分更多款式，调低可合并相近服装。", "caption"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(Ui.DialogButton("取消", () => dialog.Close(false)));
        buttons.Children.Add(Ui.DialogButton("应用", () => dialog.Close(true))); body.Children.Add(buttons); dialog.Content = body;
        if (!await dialog.ShowDialog<bool>(this)) return;
        var updated = rule with { OutfitSimilarity = (double)(similarity.Value ?? .64m) };
        updated.Validate();
        var index = _rules.IndexOf(rule); if (index < 0) return;
        _rules[index] = updated; _ruleList.SelectedItem = updated;
        var saved = SavedWith(updated); _defaultRules = DefaultsWith(updated);
        SavePreferences(_defaultRules, saved); RefreshSavedRules(saved, updated.Id);
        Reclassify();
    }
}
