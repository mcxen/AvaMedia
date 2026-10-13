using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace AvaMedia.FileUi;

internal static class FileBrowserTheme
{
    // Fb owns the B-layout surfaces, 54px rows and classic selection colors.
    // Ac supplies action/navigation metrics and composition (segments, cards,
    // section headers and empty states); Ui supplies host radii. Do not replace
    // the neutral virtualized layout rows with the generic selected ListBox face.
    public static void Install(Control view)
    {
        void Palette(ThemeVariant variant, string surface, string sidebar, string panel, string text, string muted,
            string line, string fill, string hover, string selected, string selectionText, string accent, double radius)
        {
            var resources = new ResourceDictionary();
            double HostNumber(string key, double fallback) => radius > 3
                && Application.Current?.TryFindResource(key, variant, out var value) == true && value is double number ? number : fallback;
            CornerRadius HostRadius(string key, double fallback) => radius > 3
                && Application.Current?.TryFindResource(key, variant, out var value) == true && value is CornerRadius corners ? corners : new(fallback);
            resources["FbActionHeight"] = HostNumber("AcShellButtonHeight", 30);
            resources["FbNavigationHeight"] = HostNumber("AcButtonHeight", 28);
            resources["FbTextRowHeight"] = HostNumber("AcTextRowHeight", 36);
            resources["FbHeaderHeight"] = 28d;
            resources["FbBodyFont"] = 13d;
            resources["FbCaptionFont"] = 12d;
            resources["FbCrumbRadius"] = HostRadius("AcListRadius", 8);
            foreach (var (key, color) in new[] { ("Surface", surface), ("Sidebar", sidebar), ("Panel", panel), ("Text", text),
                ("Muted", muted), ("Line", line), ("Fill", fill), ("Hover", hover), ("Selected", selected),
                ("SelectionText", selectionText), ("Accent", accent) })
                resources["Fb" + key] = new SolidColorBrush(Color.Parse(color));
            resources["FbSelectedMuted"] = new SolidColorBrush(Color.Parse(radius <= 3 ? selectionText : muted));
            resources["FbRadius"] = new CornerRadius(radius);
            resources["FbNavigationRadius"] = HostRadius("UiInputRadius", radius <= 3 ? radius : 7);
            resources["FbDurationFill"] = new SolidColorBrush(Color.Parse("#99000000"));
            resources["FbDurationText"] = new SolidColorBrush(Color.Parse("#FFFFFF"));
            resources["FbDurationRadius"] = new CornerRadius(radius <= 3 ? radius : 4);
            resources["FbCardPadding"] = new Thickness(8);
            resources["FbRowGap"] = new Thickness(0, 0, 0, 12);
            resources["FbToolbarBorder"] = new Thickness(radius <= 3 ? 1 : 0);
            resources["FbToolbarFill"] = new SolidColorBrush(Color.Parse(radius <= 3 ? fill : surface));
            resources["FbSelectedGlyph"] = new SolidColorBrush(Color.Parse(radius <= 3 ? selectionText : accent));
            resources["FbActionRadius"] = HostRadius("AcShellButtonRadius", radius <= 3 ? radius : 15);
            resources["FbCheckRadius"] = new CornerRadius(radius <= 3 ? radius : 5);
            resources["FbCheckFace"] = new SolidColorBrush(Color.Parse(radius <= 3 ? surface : variant == ThemeVariant.Dark ? "#D1D1D6" : "#FFFFFF"));
            resources["FbCheckLine"] = new SolidColorBrush(Color.Parse(radius <= 3 ? line : variant == ThemeVariant.Dark ? "#8E8E93" : "#C7C7CC"));
            resources["FbCheckText"] = new SolidColorBrush(Color.Parse("#FFFFFF"));
            resources["FbCardRadius"] = HostRadius("UiCardRadius", radius == 0 ? 0 : radius == 3 ? 3 : 14);
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
