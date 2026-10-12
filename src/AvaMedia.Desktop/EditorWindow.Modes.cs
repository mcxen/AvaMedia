using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace AvaMedia.Desktop;

/// <summary>
/// Segmented mode switch (剪辑 / 画面裁剪 / 自动转正 / 选项). The TabItems remain the source of truth for
/// headers, visibility and enabled state, so existing logic that toggles tabs keeps working.
/// </summary>
public partial class EditorWindow
{
    private void BuildModeBar()
    {
        ModeButtons.Children.Clear();
        // Visual order follows the design: clip, crop, direction, options.
        var tabs = EditTabs.Items.OfType<TabItem>().ToList();
        var order = tabs.OrderBy(tab => ReferenceEquals(tab, DirectionTab) ? 2 : tabs.IndexOf(tab) >= 2 ? 3 : tabs.IndexOf(tab)).ToList();
        foreach (var tab in order)
        {
            var button = new Button { Classes = { "mode" } };
            button.Bind(ContentControl.ContentProperty, tab.GetObservable(HeaderedContentControl.HeaderProperty));
            button.Bind(IsVisibleProperty, tab.GetObservable(IsVisibleProperty));
            button.Bind(IsEnabledProperty, tab.GetObservable(IsEnabledProperty));
            button.Click += (_, _) => EditTabs.SelectedItem = tab;
            button.Tag = tab;
            ModeButtons.Children.Add(button);
        }
        EditTabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, EditTabs)) UpdateModeBar(); };
        UpdateModeBar();
    }
    private void UpdateModeBar()
    {
        foreach (var button in ModeButtons.Children.OfType<Button>())
            button.Classes.Set("active", ReferenceEquals(button.Tag, EditTabs.SelectedItem));
    }
}
