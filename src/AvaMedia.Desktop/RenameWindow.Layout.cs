using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaMedia.Desktop;

public sealed partial class RenameWindow
{
    private static Grid FileColumns()
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,Auto,2*,3*,Auto") };
        grid.Bind(Grid.ColumnSpacingProperty, Classification.ClassifyUi.Resource("AcRowGap"));
        foreach (var (column, key) in new[] { (0, "RenameCheckWidth"), (1, "RenameKindWidth"), (4, "RenameStatusWidth") })
            grid.ColumnDefinitions[column].Bind(ColumnDefinition.WidthProperty, Classification.ClassifyUi.Resource(key));
        return grid;
    }

    private static Button RuleIcon(string kind, string label, Action action)
    {
        var button = Ui.Button("", action); button.Classes.Add("icon");
        var icon = new Controls.LucideIcon { Kind = kind };
        icon.Bind(WidthProperty, Classification.ClassifyUi.Resource("UiIconSmall"));
        icon.Bind(HeightProperty, Classification.ClassifyUi.Resource("UiIconSmall"));
        button.Content = icon;
        ToolTip.SetTip(button, label); Avalonia.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    private void ShowRulesMenu()
    {
        var menu = new ContextMenu { Classes = { "ac" } };
        var save = new MenuItem { Header = Localization.Text("保存规则…"), IsEnabled = _rules.Count > 0 };
        save.Click += async (_, _) => await SaveRulesAsync(); menu.Items.Add(save);
        var load = new MenuItem { Header = Localization.Text("载入规则…") };
        load.Click += async (_, _) => await LoadRulesAsync(); menu.Items.Add(load);
        _rulesMenu.ContextMenu = menu; menu.Open(_rulesMenu);
    }

    private void ShowFileMenu(Button target)
    {
        var menu = new ContextMenu { Classes = { "ac" } };
        var invert = new MenuItem { Header = Localization.Text("反选"), IsEnabled = _entries.Count > 0 };
        invert.Click += (_, _) => SelectFiles(true); menu.Items.Add(invert);
        var refresh = new MenuItem { Header = Localization.Text("刷新预览"), IsEnabled = _entries.Any(entry => entry.Include) };
        refresh.Click += async (_, _) => await PreviewRename(); menu.Items.Add(refresh);
        target.ContextMenu = menu; menu.Open(target);
    }

    private void UpdateRuleActions()
    {
        if (_moveUp is null) return;
        var index = _ruleList.SelectedIndex;
        _moveUp.IsEnabled = index > 0;
        _moveDown.IsEnabled = index >= 0 && index < _rules.Count - 1;
        _removeRule.IsEnabled = index >= 0;
    }

    private void RefreshFileSelection()
    {
        var included = _entries.Count(entry => entry.Include);
        _syncingFileSelection = true;
        try { _selectAll.IsChecked = included == 0 ? false : included == _entries.Count ? true : null; }
        finally { _syncingFileSelection = false; }
        _selectAll.IsEnabled = _entries.Count > 0 && !_renaming && !_importing && _operation is null;
    }

    private void RefreshRenameAction()
    {
        if (_rename is null) return;
        if (_renamePlan is { Length: > 0 } plan)
            Localization.SetContent(_rename, $"重命名 {plan.Count(item => !StringComparer.Ordinal.Equals(item.Source, item.Target))} 个文件");
        else _rename.Content = Localization.Text("执行重命名");
    }

    // Inline emphasis inherits the row foreground, including native selection colors.
    private sealed class RenameNamePreview : TextBlock
    {
        public static readonly StyledProperty<string> OriginalNameProperty = AvaloniaProperty.Register<RenameNamePreview, string>(nameof(OriginalName), "");
        public static readonly StyledProperty<string> ProposedNameProperty = AvaloniaProperty.Register<RenameNamePreview, string>(nameof(ProposedName), "");
        public string OriginalName { get => GetValue(OriginalNameProperty); set => SetValue(OriginalNameProperty, value); }
        public string ProposedName { get => GetValue(ProposedNameProperty); set => SetValue(ProposedNameProperty, value); }
        protected override Type StyleKeyOverride => typeof(TextBlock);

        public RenameNamePreview()
        {
            VerticalAlignment = VerticalAlignment.Center; TextWrapping = TextWrapping.NoWrap; TextTrimming = TextTrimming.CharacterEllipsis;
            Localization.SetIsUserText(this, true);
            Bind(FontSizeProperty, Classification.ClassifyUi.Resource("AcBodyFont"));
            Bind(ToolTip.TipProperty, new Binding(nameof(ProposedName)) { Source = this });
            Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(ProposedName)) { Source = this });
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property != OriginalNameProperty && change.Property != ProposedNameProperty) return;
            var original = OriginalName; var proposed = ProposedName;
            var prefix = 0; var suffix = 0;
            while (prefix < Math.Min(original.Length, proposed.Length) && original[prefix] == proposed[prefix]) prefix++;
            var oldBoundaries = StringInfo.ParseCombiningCharacters(original).Append(original.Length).ToHashSet();
            var newBoundaries = StringInfo.ParseCombiningCharacters(proposed).Append(proposed.Length).ToHashSet();
            while (prefix > 0 && (!oldBoundaries.Contains(prefix) || !newBoundaries.Contains(prefix))) prefix--;
            while (suffix < Math.Min(original.Length, proposed.Length) - prefix && original[^(suffix + 1)] == proposed[^(suffix + 1)]) suffix++;
            while (suffix > 0 && (!oldBoundaries.Contains(original.Length - suffix) || !newBoundaries.Contains(proposed.Length - suffix))) suffix--;
            Inlines!.Clear();
            if (!StringComparer.Ordinal.Equals(original, proposed) && proposed.Length == prefix + suffix)
            { Inlines.Add(new Run(proposed) { FontWeight = FontWeight.SemiBold }); return; }
            if (prefix > 0) Inlines.Add(new Run(proposed[..prefix]));
            if (proposed.Length > prefix + suffix) Inlines.Add(new Run(proposed[prefix..(proposed.Length - suffix)]) { FontWeight = FontWeight.SemiBold });
            if (suffix > 0) Inlines.Add(new Run(proposed[^suffix..]));
        }
    }
}
