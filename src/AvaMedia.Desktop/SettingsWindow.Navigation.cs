using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private static readonly string[] SettingsThemeKeys = ["Light", "Dark", "MacOS9", "WindowsXP"];
    private static readonly string[] SettingsLanguageKeys = ["system", "zh-CN", "en-US"];
    private void InitializeSettingsNavigation()
    {
        SettingsTabs.SelectionChanged += (_, _) => UpdateSettingsPageTitle();
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.F && (args.KeyModifiers.HasFlag(KeyModifiers.Control) || args.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            { SettingsTabs.Focus(); args.Handled = true; }
        };
        foreach (var choice in new[] { ThemeInput, LanguageInput })
        { _values.Add(() => choice.SelectedIndex); choice.SelectionChanged += (_, _) => MarkDirty(); }
        _appliedValues = _values.Select(value => value()).ToArray();
        UpdateSettingsPageTitle();
    }
    private void UpdateSettingsPageTitle()
    {
        if (SettingsTabs.SelectedItem is TabItem page) SettingsPageTitle.Text = page.Header?.ToString() ?? "";
    }
    private static void SelectContainingPages(Control input)
    {
        foreach (var page in input.GetLogicalAncestors().OfType<TabItem>().Reverse())
            if (page.GetLogicalAncestors().OfType<TabControl>().FirstOrDefault() is { } tabs && tabs.SelectedItem != page)
                tabs.SelectedItem = page;
        foreach (var expander in input.GetLogicalAncestors().OfType<Expander>()) expander.IsExpanded = true;
        if (input is TabItem item && item.GetLogicalAncestors().OfType<TabControl>().FirstOrDefault() is { } owner) owner.SelectedItem = item;
    }
    private void RevealSetting(Control input)
    {
        SelectContainingPages(input);
        Dispatcher.UIThread.Post(() => { if (!_modelsClosed) { input.BringIntoView(); input.Focus(); } }, DispatcherPriority.Loaded);
    }
    private void RestoreSettingsView((TabControl Tabs, object? Selection)[] pages, (ScrollViewer View, Vector Offset)[] scrolls)
    {
        void RestorePages()
        {
            foreach (var (tabs, selection) in pages)
                if (selection is TabItem { IsVisible: true }) tabs.SelectedItem = selection;
        }
        RestorePages();
        Dispatcher.UIThread.Post(() =>
        {
            if (_modelsClosed) return;
            RestorePages();
            foreach (var (view, offset) in scrolls) view.Offset = offset;
        }, DispatcherPriority.Loaded);
    }
    // NumericUpDown initializes its formatted Text after the draft is populated.
    // Compare parsed values so that showing a page cannot enable Apply by itself.
    private static object? NumericDraftValue(NumericUpDown input) =>
        decimal.TryParse(input.Text, System.Globalization.NumberStyles.Number, input.NumberFormat, out var value) ? value
            : string.IsNullOrEmpty(input.Text) && !input.IsFocused ? input.Value : input.Text;
}
