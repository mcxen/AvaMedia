using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    // Keep every legacy integer available, with the demo's common values first.
    private static readonly int[] ThreadChoices = [0, 1, 2, 4, 8, 12, 16, 20, 24, 28, 32, 3, 5, 6, 7, 9, 10, 11, 13, 14, 15];
    private static readonly string[] AccentChoices = ["Blue", "Purple", "Pink", "Red", "Orange", "Yellow", "Green", "Gray"];
    private static readonly string[] AccentNames = ["蓝色", "紫色", "粉色", "红色", "橙色", "黄色", "绿色", "灰色"];
    private string _accentColor = "Blue";
    private bool _resettingDefaults, _updateModeEdited;
    private readonly TextBlock _settingsPageTitle = new() { [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("StPageTitleFont"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, [!MarginProperty] = new DynamicResourceExtension("StTitleMargin") };
    private void InitializeV2Navigation()
    {
        var pages = new[] { GeneralTab, AppearanceTab, PlayerTab, ConversionTab, ModelsTab, ProvidersTab, RemoteTab, ToolsTab, AboutTab };
        string[] colors = ["Gray", "Blue", "Pink", "Orange", "Green", "Purple", "Cyan", "Gray", "Gray"];
        string[] icons = ["settings", "monitor", "play", "layers", "cpu", "sparkles", "wifi", "sliders-horizontal", "info"];
        for (var index = 0; index < pages.Length; index++)
        {
            var color = colors[index]; var icon = icons[index];
            pages[index].HeaderTemplate = new FuncDataTemplate<string>((label, _) =>
            {
                var glyph = new LucideIcon { Kind = icon, [!WidthProperty] = new DynamicResourceExtension("StNavigationIconSize"), [!HeightProperty] = new DynamicResourceExtension("StNavigationIconSize"), [!LucideIcon.ForegroundProperty] = new DynamicResourceExtension("StIconText"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var tile = new Border { [!WidthProperty] = new DynamicResourceExtension("StNavigationTileSize"), [!HeightProperty] = new DynamicResourceExtension("StNavigationTileSize"), [!Border.CornerRadiusProperty] = new DynamicResourceExtension("UiSettingsIconRadius"), Child = glyph };
                tile.Bind(Border.BackgroundProperty, new DynamicResourceExtension("St" + color));
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, [!StackPanel.SpacingProperty] = new DynamicResourceExtension("StNavigationIconGap") };
                row.Children.Add(tile);
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); return row;
            });
        }
        _values.Add(() => _accentColor);
        _values.Add(() => _resettingDefaults);
        _values.Add(() => _updateModeEdited);
        UpdateModeInput.SelectionChanged += (_, _) =>
        { if (!_initializing) { _updateModeEdited = true; MarkDirty(); } };
        for (var index = 0; index < AccentChoices.Length; index++)
        {
            var color = AccentChoices[index];
            var button = new Button { Classes = { "ac", "accentSwatch" }, Tag = color };
            button.Bind(BackgroundProperty, new DynamicResourceExtension("St" + color));
            ToolTip.SetTip(button, AccentNames[index]); Avalonia.Automation.AutomationProperties.SetName(button, AccentNames[index]);
            button.Click += (_, _) => { _accentColor = color; RefreshAccentPalette(); MarkDirty(); };
            AccentPalette.Children.Add(button);
        }
        RefreshAccentPalette();
    }
    private void RefreshAccentPalette()
    {
        foreach (var button in AccentPalette.Children.OfType<Button>()) button.Classes.Set("selected", button.Tag as string == _accentColor);
    }
    private void RefreshLastUpdateCheck()
    {
        LastUpdateCheckText.Text = (ApplicationUpdater.Shared.CheckState.CheckedAt ?? _settings.LastUpdateCheck) is { } time
            ? Localization.Text("上次检查：") + time.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : Localization.Text("尚未检查更新");
        // Do not repeat the status title (e.g. "尚未检查更新") on a second line.
        LastUpdateCheckText.Opacity = LastUpdateCheckText.Text != UpdateStatusTitle.Text ? 1 : 0;
    }
    private void ModelLibraryClick(object? sender, RoutedEventArgs args)
    {
        ModelLibraryPanel.IsVisible = !ModelLibraryPanel.IsVisible;
        ModelLibraryButton.Content = Localization.Text(ModelLibraryPanel.IsVisible ? "收起模型库" : "查看模型库…");
        if (ModelLibraryPanel.IsVisible) ModelLibraryPanel.BringIntoView();
    }
}
