using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow
{
    private readonly List<FolderClassificationRule> _disabledNsfwRules = [];
    private readonly Dictionary<string, FolderClassifiedFile> _hiddenPrivateResults = new(BatchRename.PathComparer);
    private bool RuleVisible(FolderClassificationRule rule) => _settings.EnableNsfwContent || !MediaPrivacy.IsSensitiveRule(rule);
    private IEnumerable<string> PrivateSemanticLabels => _defaultRules.Concat(_savedRules).Concat(_disabledNsfwRules)
        .Where(MediaPrivacy.IsSensitiveRule).SelectMany(rule => rule.Categories.Select(category => rule.Label(category.Id)));
    private FolderClassificationRule[] VisibleDefaults => _defaultRules.Where(RuleVisible).ToArray();

    private void PrivacyChanged(object? sender, EventArgs args)
    {
        if (_closed) return;
        _operation?.Cancel(); _activity.Update(null); _status.Text = Localization.Text("隐私设置已更新");
        if (!_settings.EnableNsfwContent)
        {
            foreach (var (path, result) in _results) _hiddenPrivateResults[path] = result;
            _disabledNsfwRules.AddRange(_rules.Where(MediaPrivacy.IsSensitiveRule));
            foreach (var rule in _rules.Where(MediaPrivacy.IsSensitiveRule).ToArray()) _rules.Remove(rule);
        }
        else
        {
            foreach (var rule in _disabledNsfwRules.Where(rule => !_rules.Any(active => active.Id == rule.Id)).Take(8 - _rules.Count)) _rules.Add(rule);
            _disabledNsfwRules.Clear();
        }
        RefreshSavedVisibility();
        foreach (var path in _results.Keys.ToArray())
        {
            var previous = _results[path];
            if (_settings.EnableNsfwContent && _hiddenPrivateResults.TryGetValue(path, out var hidden)
                && hidden.Media.Length == previous.Media.Length && hidden.Media.LastWriteUtc == previous.Media.LastWriteUtc)
                previous = previous with { Decisions = previous.Decisions.Concat(hidden.Decisions.Where(decision =>
                    _rules.Any(rule => rule.Id == decision.RuleId && MediaPrivacy.IsSensitiveRule(rule)))).ToArray() };
            _results[path] = FolderClassification.KeepManual(FolderClassification.Classify(previous.Media, _rules.ToArray(), (double)(_tagThreshold.Value ?? .5m), _settings.EnableNsfwContent), previous);
        }
        RegroupOutfits();
        if (_settings.EnableNsfwContent) _hiddenPrivateResults.Clear();
        foreach (var entry in _entries) UpdateEntry(entry);
        InvalidatePlan(); RenderBoard(); RenderDetails();
    }

    private void RefreshSavedVisibility()
    {
        var selectedId = (_savedSelector.SelectedItem as FolderClassificationRule)?.Id;
        var visible = _savedRules.Where(RuleVisible).ToArray();
        _savedSelector.ItemsSource = visible;
        _savedSelector.SelectedItem = visible.FirstOrDefault(rule => rule.Id == selectedId) ?? visible.FirstOrDefault();
        RefreshRuleActions();
    }

    private async Task SelectPresetsAsync()
    {
        if (_busy) return;
        var dialog = new Window { Title = "选择分组", Width = 650, Height = 620, MinWidth = 500, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(Ui.Text("选择要用的分组，类别已经准备好。", "caption"));
        var choices = new List<(FolderClassificationRule Rule, CheckBox Check)>();
        foreach (var nsfw in new[] { false, true })
        {
            var presets = FolderClassificationRule.Presets.Where(rule => MediaPrivacy.IsSensitiveRule(rule) == nsfw && RuleVisible(rule)).ToArray();
            if (presets.Length == 0) continue;
            body.Children.Add(Ui.Text(nsfw ? "NSFW 分组" : "日常分组", "settingsHeading"));
            foreach (var rule in presets)
            {
                var check = new CheckBox { Content = Localization.Text(rule.Name), IsChecked = _rules.Any(active => active.Id == rule.Id) };
                var names = UserText(rule.ByOutfit ? Localization.Text("按服装颜色、款式和配饰自动分组") : string.Join(" · ", rule.Categories.Select(category => category.Name)));
                names.Margin = new(28, 0, 0, 0); names.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
                body.Children.Add(new StackPanel { Spacing = 2, Children = { check, names } }); choices.Add((rule, check));
            }
        }
        var error = Ui.Text("", "caption"); error.IsVisible = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(Ui.DialogButton("取消", () => dialog.Close(false)));
        buttons.Children.Add(Ui.DialogButton("应用选择", () =>
        {
            var presetIds = choices.Select(choice => choice.Rule.Id).ToHashSet();
            var active = _rules.Where(rule => !presetIds.Contains(rule.Id)).Concat(choices.Where(choice => choice.Check.IsChecked == true && RuleVisible(choice.Rule))
                .Select(choice => _rules.FirstOrDefault(rule => rule.Id == choice.Rule.Id) ?? choice.Rule)).ToArray();
            try { FolderClassification.ValidateRules(active); ApplyActiveRules(active); if (active.Any(rule => rule.ByOutfit)) _splitTypes.IsChecked = false; dialog.Close(true); }
            catch (ArgumentException exception) { error.Text = Localization.Text(exception.Message); error.IsVisible = true; }
        }));
        var root = new Grid { RowDefinitions = new("*,Auto,Auto"), Margin = new(18), RowSpacing = 10 };
        root.Children.Add(new ScrollViewer { Content = body }); Grid.SetRow(error, 1); root.Children.Add(error); Grid.SetRow(buttons, 2); root.Children.Add(buttons); dialog.Content = root;
        EventHandler privacy = (_, _) => dialog.Close(false);
        _settings.NsfwContentChanged += privacy;
        try { await dialog.ShowDialog<bool>(this); }
        finally { _settings.NsfwContentChanged -= privacy; }
    }
}
