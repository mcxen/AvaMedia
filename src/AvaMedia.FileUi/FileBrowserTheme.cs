using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace AvaMedia.FileUi;

internal static class FileBrowserTheme
{
    public static void Install(Control view)
    {
        void Palette(ThemeVariant variant, string surface, string sidebar, string panel, string text, string muted,
            string line, string fill, string hover, string selected, string selectionText, string accent, double radius)
        {
            var resources = new ResourceDictionary();
            foreach (var (key, color) in new[] { ("Surface", surface), ("Sidebar", sidebar), ("Panel", panel), ("Text", text),
                ("Muted", muted), ("Line", line), ("Fill", fill), ("Hover", hover), ("Selected", selected),
                ("SelectionText", selectionText), ("Accent", accent) })
                resources["Fb" + key] = new SolidColorBrush(Color.Parse(color));
            resources["FbRadius"] = new CornerRadius(radius);
            resources["FbToolbarBorder"] = new Thickness(radius <= 3 ? 1 : 0);
            resources["FbToolbarFill"] = new SolidColorBrush(Color.Parse(radius <= 3 ? fill : surface));
            resources["FbSelectedGlyph"] = new SolidColorBrush(Color.Parse(radius <= 3 ? selectionText : accent));
            resources["FbActionRadius"] = new CornerRadius(radius <= 3 ? radius : 15);
            resources["FbCheckRadius"] = new CornerRadius(radius <= 3 ? radius : 5);
            resources["FbCheckText"] = new SolidColorBrush(Color.Parse("#FFFFFF"));
            resources["FbCardRadius"] = new CornerRadius(radius == 0 ? 0 : radius == 3 ? 3 : 14);
            resources["FbRowHeight"] = 54d;
            resources["FbSidebarWidth"] = 220d; resources["FbInspectorWidth"] = 270d;
            resources["FbCoverHeight"] = 96d; resources["FbGridRowHeight"] = 166d;
            view.Resources.ThemeDictionaries[variant] = resources;
        }
        Palette(ThemeVariant.Light, "#FFFFFF", "#F1F4FA", "#FAFAFC", "#1D1D1F", "#86868B", "#E5E5EA", "#ECECF0", "#F0F0F3", "#EAF3FF", "#1D1D1F", "#007AFF", 9);
        Palette(ThemeVariant.Dark, "#222226", "#2A2C31", "#29292D", "#F5F5F7", "#A1A1A6", "#45454A", "#3A3A3F", "#38383D", "#163B66", "#F5F5F7", "#0A84FF", 9);
        Palette(new ThemeVariant("MacOS9", ThemeVariant.Light), "#FFFFFF", "#DDDDDD", "#EEEEEE", "#000000", "#555555", "#888888", "#CCCCCC", "#E5E5E5", "#3D4E80", "#FFFFFF", "#3D4E80", 0);
        Palette(new ThemeVariant("WindowsXP", ThemeVariant.Light), "#FFFFFF", "#ECE9D8", "#F5F4EA", "#000000", "#666666", "#ACA899", "#ECE9D8", "#E0E8F5", "#316AC5", "#FFFFFF", "#215DC6", 3);
    }
    public static void Bind(AvaloniaObject target, AvaloniaProperty property, string key)
        => target.Bind(property, new DynamicResourceExtension("Fb" + key));
}
