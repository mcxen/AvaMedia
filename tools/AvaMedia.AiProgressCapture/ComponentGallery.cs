using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Controls;
using AvaMedia.Desktop.Controls;
using static AvaMedia.Controls.ComponentResources;

/// <summary>用真实组件呈现 demo 的 12 个评审区块。示例数据仅在此截图工具使用。</summary>
internal static class ComponentGallery
{
    private static readonly JsonElement[] Sections = ReadLayout();
    private static JsonElement[] ReadLayout()
    {
        using var stream = typeof(ComponentGallery).Assembly.GetManifestResourceStream("AvaMedia.AiProgressCapture.component-layout.json")!;
        using var document = JsonDocument.Parse(stream); return document.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
    }
    public static Window Create(int index)
    {
        var section = Sections[index]; var canvas = new Canvas();
        foreach (var node in section.GetProperty("nodes").EnumerateArray())
        {
            var view = CreateNode(node); if (view is null) continue;
            view.Width = Number(node, "w"); view.Height = Number(node, "h");
            if (view is ScrollBar scroll) { view.Width += 4; view.Height = 200; Canvas.SetTop(view, Number(node, "y") - (Has(Text(node, "cls"), "w") ? 70 : 20)); scroll.Maximum = 466; scroll.ViewportSize = 200; scroll.Value = Has(Text(node, "cls"), "w") ? 233 : 66; }
            if (view is TextBlock) { view.Width += 32; view.Height += 8; }
            Canvas.SetLeft(view, Number(node, "x") - (view is ScrollBar ? 2 : 0)); if (view is not ScrollBar) Canvas.SetTop(view, Number(node, "y")); canvas.Children.Add(view);
        }
        var border = new Border { [!Border.BackgroundProperty] = Resource("UiSurface"), [!Border.CornerRadiusProperty] = Resource("AcCardRadius"), Child = canvas };
        var window = new Window { Title = "通用组件预览", Width = Number(section, "width"), Height = Number(section, "height"), Content = border, SystemDecorations = SystemDecorations.None };
        ComponentMotion.SetReduced(window, true); return window;
    }
    public static Window CreateOverview()
    {
        var sections = new StackPanel { Spacing = 16, Margin = new(16) };
        for (var i = 0; i < 12; i++)
        {
            var sample = Create(i); var content = (Control)sample.Content!; sample.Content = null;
            content.Width = sample.Width; content.Height = sample.Height; sections.Children.Add(content);
        }
        var choice = new ComboBox { Classes = { "ac" }, ItemsSource = new[] { "浅色", "深色" }, SelectedIndex = 0, Width = 120 };
        choice.SelectionChanged += (_, _) => Application.Current!.RequestedThemeVariant = choice.SelectedIndex == 0 ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(22, 16) };
        header.Children.Add(Label("AvaMedia 通用组件 · 交互预览", 17, weight: FontWeight.SemiBold)); Grid.SetColumn(choice, 1); header.Children.Add(choice);
        var body = new DockPanel(); DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        body.Children.Add(new ScrollViewer { Content = sections, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        return new Window { Title = "通用组件预览", Width = 1280, Height = 820, Content = body, [!Window.BackgroundProperty] = Resource("UiCanvas") };
    }

    private static string Text(JsonElement n, string key) => n.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static double Number(JsonElement n, string key) => n.TryGetProperty(key, out var v) ? v.GetDouble() : 0;
    private static bool Has(string cls, string token) => cls.Split(' ').Contains(token);
    private static void Theme(Control control, string key) { if (control is Avalonia.Controls.Primitives.TemplatedControl t) t.Bind(TemplatedControl.ThemeProperty, Resource(key)); }
    private static T State<T>(T control, string cls) where T : Control
    {
        control.Classes.Add("ac");
        foreach (var (token, state) in new[] { ("h", "ac-hover"), ("p", "ac-pressed"), ("f", "ac-focus"), ("err", "error") }) control.Classes.Set(state, Has(cls, token));
        control.IsEnabled = !Has(cls, "dis"); return control;
    }
    private static TextBlock Label(string text, double size = 13, string ink = "UiText", FontWeight? weight = null)
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
        label.Bind(TextBlock.ForegroundProperty, Resource(ink)); return label;
    }
    private static Button Button(string text, string cls, string icon = "")
    {
        var button = State(new Button(), cls); Theme(button, "AcButton");
        foreach (var (token, role) in new[] { ("primary", "primary"), ("danger", "destructive"), ("dline", "danger"), ("text", "borderless"), ("icon", "icon"), ("ghost", "ghost"), ("sm", "small"), ("lg", "large") }) button.Classes.Set(role, Has(cls, token));
        if (icon.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var glyph = new LucideIcon { Kind = icon.TrimStart('#') switch { "more" => "ellipsis", "dn" => "chevron-down", "trash" => "trash-2", "film" => "film", "gear" => "settings", var k => k }, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
            glyph.Bind(LucideIcon.ForegroundProperty, new Avalonia.Data.Binding(nameof(Avalonia.Controls.Button.Foreground)) { RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) } });
            row.Children.Add(glyph);
            if (text.Length > 0) row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }); button.Content = row;
            ToolTip.SetTip(button, text.Length > 0 ? text : "图标操作"); Avalonia.Automation.AutomationProperties.SetName(button, text.Length > 0 ? text : "图标操作");
        }
        else button.Content = text;
        return button;
    }
    private static Control? CreateNode(JsonElement n)
    {
        var cls = Text(n, "cls"); if (n.TryGetProperty("opacity", out var opacity) && opacity.GetDouble() < .6) cls += " dis"; var text = Text(n, "text"); var html = Text(n, "html");
        if (Text(n, "kind") == "text")
        {
            var label = Label(text, Number(n, "font"), Text(n, "ink"));
            if (int.TryParse(Text(n, "weight"), out var weight) && weight >= 500) label.FontWeight = weight >= 700 ? FontWeight.Bold : weight >= 600 ? FontWeight.SemiBold : FontWeight.Medium;
            return label;
        }
        if (Text(n, "kind") == "shell")
        {
            if (Has(cls, "popv")) return new Popover();
            var border = new Border { Padding = new(0), ClipToBounds = false };
            if (Has(cls, "gr")) { border.Classes.Add("ac-setting-row"); border.Classes.Add("divider"); return border; }
            if (Has(cls, "scrollbox"))
            {
                border.Bind(Border.BackgroundProperty, Resource("UiSurface"));
                border.Bind(Border.CornerRadiusProperty, Resource("AcGroupRadius"));
                border.Bind(Border.BorderBrushProperty, Resource("AcStageLineBrush"));
                border.Bind(Border.BorderThicknessProperty, Resource("AcHairlineThickness"));
                border.UseLayoutRounding = false; return border;
            }
            border.Classes.Add(Has(cls, "card") ? "ac-card" : Has(cls, "group") || Has(cls, "scrollbox") ? "ac-group" : Has(cls, "sheet") ? "ac-sheet" : "ac-popover");
            return border;
        }
        if (Has(cls, "split"))
        {
            var split = new AvaMedia.Controls.SplitButton(); split.ActionButton.Content = text; split.SetPrimary(html.Contains("primary")); return split;
        }
        if (Has(cls, "btn"))
        {
            var button = Button(text, cls, Text(n, "icon"));
            if (html.Contains("spin"))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var spin = new LoadingSpinner(); if (!Has(cls, "primary")) spin.Bind(LoadingSpinner.AccentProperty, Resource("UiTextSecondary")); if (Has(cls, "primary")) { spin.Bind(LoadingSpinner.AccentProperty, Resource("UiAccentText")); spin.Bind(LoadingSpinner.TrackProperty, Resource("AcRunningFill")); }
                row.Children.Add(spin); row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }); button.Content = row;
            }
            return button;
        }
        if (Has(cls, "pop") || Has(cls, "combo"))
        {
            var combo = State(new ComboBox { ItemsSource = new[] { Has(cls, "combo") ? Text(n, "value") : Regex.Replace(text, @"\s+", " ").Trim(), "H.265 / HEVC", "AV1" }, SelectedIndex = 0, IsEditable = Has(cls, "combo") }, cls);
            if (Has(cls, "pd")) combo.Classes.Add("pulldown"); Theme(combo, "AcComboBox"); return combo;
        }
        if (Has(cls, "menu"))
        {
            var body = new StackPanel();
            foreach (var item in n.GetProperty("items").EnumerateArray())
            {
                var itemClass = Text(item, "cls");
                if (Has(itemClass, "mhd")) { body.Children.Add(Label(Text(item, "text"), 11, "UiTextSecondary", FontWeight.SemiBold)); continue; }
                if (Has(itemClass, "msep")) { body.Children.Add(new Border { Height = .5, Margin = new(10, 5), [!Border.BackgroundProperty] = Resource("AcHairline") }); continue; }
                var itemHtml = Text(item, "html");
                var secondary = System.Net.WebUtility.HtmlDecode(Regex.Match(itemHtml, "class=\"sec\">([^<]+)").Groups[1].Value);
                var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 16 };
                header.Children.Add(new TextBlock { Text = (secondary.Length > 0 ? Text(item, "text").Replace(secondary, "") : Text(item, "text")).Trim(), VerticalAlignment = VerticalAlignment.Center });
                if (secondary.Length > 0) { var detail = Label(secondary, 12, Has(itemClass, "on") ? "UiAccentText" : "UiTextSecondary"); Grid.SetColumn(detail, 1); header.Children.Add(detail); }
                var menuItem = new MenuItem { Header = header, IsChecked = Has(itemClass, "on"), ToggleType = MenuItemToggleType.CheckBox, IsEnabled = !Has(itemClass, "dis") };
                menuItem.Classes.Set("ac-hover", Has(itemClass, "on"));
                menuItem.Classes.Set("pulldown", !html.Contains("class=\"ck\""));
                if (menuItem.Classes.Contains("pulldown")) { menuItem.ToggleType = MenuItemToggleType.None; menuItem.IsChecked = false; }
                if (Text(item, "text") == "从列表中移除" || Text(item, "text") == "移到废纸篓") menuItem.Classes.Add("danger");
                Theme(menuItem, "AcMenuItem"); body.Children.Add(menuItem);
            }
            return new Border { Classes = { "ac-popover" }, Padding = new(5), Child = body };
        }
        if (Has(cls, "chk"))
        {
            var inner = Regex.Match(html, "class=\"box([^\"]*)\"").Groups[1].Value;
            ToggleButton choice = Has(inner, "rad") ? new RadioButton { GroupName = Guid.NewGuid().ToString() } : new CheckBox { IsThreeState = true };
            choice.Content = text; choice.IsChecked = html.Contains("#mx") ? null : Has(inner, "on"); State(choice, cls + inner); Theme(choice, choice is RadioButton ? "AcRadioButton" : "AcCheckBox"); return choice;
        }
        if (Has(cls, "sw")) { var sw = State(new ToggleSwitch { IsChecked = Has(cls, "on") }, cls); if (Has(cls, "sm")) sw.Classes.Add("small"); Theme(sw, "AcToggleSwitch"); return sw; }
        if (Has(cls, "num"))
        {
            var values = text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries); decimal.TryParse(values.FirstOrDefault(), out var value);
            var num = State(new NumericUpDown { Value = value }, html.Contains("err") ? "err" : html.Contains("tf f") ? "f" : ""); num.IsEnabled = !Has(cls, "dis"); Theme(num, "AcNumericUpDown");
            if (values.Length > 1) num.InnerRightContent = Label(values[1], 12, "UiTextSecondary"); return num;
        }
        if (Has(cls, "tf"))
        {
            var field = State(new TextBox { Text = Has(cls, "ph") ? "" : text, Watermark = Has(cls, "ph") ? text : null, AcceptsReturn = Text(n, "tag") == "TEXTAREA" }, cls); Theme(field, "AcTextBox"); return field;
        }
        if (Has(cls, "sl"))
        {
            var matches = Regex.Matches(html, "left:([0-9]+)%");
            if (matches.Count > 1 && !Has(cls, "ticks")) { var range = new RangeSlider { LowerValue = 22, UpperValue = 68 }; range.Children.OfType<Canvas>().Single().Children.OfType<Thumb>().Last().Classes.Add("ac-focus"); return range; }
            var value = matches.Count > 0 ? double.Parse(matches[^1].Groups[1].Value) : 40;
            var slider = State(new Slider { Maximum = 100, Value = value, TickFrequency = 25, IsSnapToTickEnabled = Has(cls, "ticks") }, html.Contains("k f") ? cls + " f" : html.Contains("k p") ? cls + " p" : cls);
            if (Has(cls, "ticks")) slider.Classes.Add("ticks"); Theme(slider, "AcSlider"); return slider;
        }
        if (Has(cls, "pb"))
        {
            var progress = new ProgressBar { Maximum = 100, Value = Has(cls, "ind") ? 30 : double.Parse(Regex.Match(html, "width:([0-9]+)%").Groups[1].Value), IsIndeterminate = Has(cls, "ind") };
            progress.Classes.Add("ac"); if (html.Contains("orange")) progress.Classes.Add("paused"); if (html.Contains("red")) progress.Classes.Add("failed"); Theme(progress, "AcProgressBar"); return progress;
        }
        if (Has(cls, "ring")) return new ProgressRing { Value = 73 };
        if (Has(cls, "stages"))
        {
            var grid = new Grid { ColumnDefinitions = new("*,24,*,24,*,24,*") }; var i = 0;
            foreach (var item in n.GetProperty("items").EnumerateArray().Where(x => Text(x, "cls").StartsWith("sc")))
            {
                var stage = new StageIndicator(); stage.Update(i, Text(item, "title"), Text(item, "detail"), Text(item, "count"), Has(Text(item, "cls"), "cur") ? "running" : Has(Text(item, "cls"), "done") ? "done" : "waiting"); Grid.SetColumn(stage, i * 2); grid.Children.Add(stage);
                if (i < 3) { var arrow = Label("→", 12, "AcTextTertiary"); arrow.HorizontalAlignment = HorizontalAlignment.Center; arrow.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(arrow, i * 2 + 1); grid.Children.Add(arrow); } i++;
            }
            return grid;
        }
        if (Has(cls, "subs"))
        {
            var panel = new StackPanel(); var i = 0;
            foreach (var item in n.GetProperty("items").EnumerateArray())
            {
                var step = new SubStepRow(); step.Update(Text(item, "title"), Text(item, "detail"), new[] { "done", "running", "waiting", "failed" }[i++], Text(item, "count"));
                panel.Children.Add(new Border { Padding = new(0, 7), BorderThickness = new(0, 0, 0, .5), [!Border.BorderBrushProperty] = Resource("AcStageLineBrush"), Child = step });
            }
            return panel;
        }
        if (Has(cls, "seg") || Has(cls, "tabs"))
        {
            var items = n.GetProperty("items").EnumerateArray().ToArray();
            var segments = new SegmentedControl { ItemsSource = items.Select(i => Text(i, "text")).ToArray(), SelectedIndex = Array.FindIndex(items, i => Has(Text(i, "cls"), "on")), IsEnabled = !Has(cls, "dis") }; if (Has(cls, "tabs")) segments.Classes.Add("tabs");
            segments.ContainerPrepared += (_, e) => { if (e.Index < items.Length && e.Container is ListBoxItem item) item.Classes.Set("ac-hover", Has(Text(items[e.Index], "cls"), "h")); }; return segments;
        }
        if (Has(cls, "chip")) return new StatusPill { Text = text, State = cls.Contains("c-run") ? "running" : cls.Contains("c-done") ? "done" : cls.Contains("c-fail") ? "failed" : cls.Contains("c-pause") ? "paused" : text == "已取消" ? "cancelled" : "waiting" };
        if (Has(cls, "badge") || Has(cls, "dotb")) { var badge = new CountBadge { Text = text }; if (Has(cls, "b")) badge.Classes.Add("blue"); if (Has(cls, "g")) badge.Classes.Add("neutral"); if (Has(cls, "dotb")) badge.Classes.Add("dot"); return badge; }
        if (Has(cls, "th")) return Thumbnail(cls, text);
        if (Has(cls, "spin")) { var spin = new LoadingSpinner(); if (Has(cls, "lg")) spin.Classes.Add("large"); return spin; }
        if (Has(cls, "sbar"))
        {
            var scroll = State(new ScrollBar { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 100, Value = 20, ViewportSize = 40 }, Has(cls, "w") ? "h" : ""); ScrollVisibility.SetPinned(scroll, true); Theme(scroll, "AcScrollBar"); return scroll;
        }
        if (Has(cls, "ai")) return new Border { CornerRadius = new(11), [!Border.BackgroundProperty] = Resource("UiAccent"), Child = new LucideIcon { Kind = "film", Width = 24, Height = 24, [!LucideIcon.ForegroundProperty] = Resource("UiAccentText") } };
        if (Has(cls, "lr"))
        {
            var parts = text.Split('\n', StringSplitOptions.RemoveEmptyEntries); var row = new ListRow();
            row.Margin = new(0); row.Classes.Set("text", Has(cls, "l36")); row.Classes.Set("ac-hover", Has(cls, "h")); row.Classes.Set("selected", Has(cls, "sel")); row.Classes.Set("active", Has(cls, "selk"));
            var grid = new Grid { ColumnDefinitions = new(Has(cls, "l56") ? "40,*,Auto" : "*,Auto"), ColumnSpacing = 10 };
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; panel.Children.Add(Label(parts.FirstOrDefault() ?? ""));
            if (Has(cls, "l56"))
            {
                var thumbClass = Regex.Match(html, "class=\"(th[^\"]*)\"").Groups[1].Value;
                grid.Children.Add(Thumbnail(thumbClass, parts.FirstOrDefault(p => p.StartsWith('+')) ?? ""));
                panel.Children.Clear(); var title = parts.FirstOrDefault(p => !p.StartsWith('+')) ?? ""; var start = Array.IndexOf(parts, title);
                panel.Children.Add(Label(title)); panel.Children.Add(Label(parts.ElementAtOrDefault(start + 1) ?? "", 11.5, "UiTextSecondary")); Grid.SetColumn(panel, 1);
                var count = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                count.Children.Add(Label(parts.ElementAtOrDefault(start + 2) ?? "", 20, "UiText", FontWeight.Bold)); count.Children.Add(Label("片段", 11, "UiTextSecondary"));
                Grid.SetColumn(count, 2); grid.Children.Add(count);
            }
            else if (parts.Length > 1) { var detail = Label(parts[1], 11.5, "UiTextSecondary"); detail.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(detail, 1); grid.Children.Add(detail); }
            grid.Children.Add(panel);
            if (Has(cls, "selk"))
            {
                foreach (var block in grid.Children.OfType<StackPanel>().SelectMany(p => p.Children).OfType<TextBlock>()) block.Bind(TextBlock.ForegroundProperty, Resource("UiAccentText"));
            }
            row.Child = grid; return row;
        }
        if (Has(cls, "tip")) return new Border { Classes = { "ac-tooltip" }, Child = new TextBlock { Text = text } };
        if (Has(cls, "toast"))
        {
            var parts = text.Split('\n', StringSplitOptions.RemoveEmptyEntries); return new Notice { Title = parts[0], Detail = parts.ElementAtOrDefault(1) ?? "", State = Has(cls, "t-ok") ? "completed" : Has(cls, "t-warn") ? "warning" : Has(cls, "t-err") ? "failed" : "info", ActionContent = Has(cls, "t-warn") ? Button("恢复", "btn sm") : null };
        }
        if (Has(cls, "state"))
        {
            var parts = text.Split('\n', StringSplitOptions.RemoveEmptyEntries); var empty = new EmptyState { Text = parts[0], Detail = parts.ElementAtOrDefault(1) ?? "", IsError = html.Contains("#alert") };
            foreach (var title in parts.Skip(2)) empty.Actions.Children.Add(Button(title, title == "查看日志" ? "btn sm" : "btn primary sm"));
            return new Border { [!Border.BackgroundProperty] = Resource("UiCanvas"), [!Border.CornerRadiusProperty] = Resource("AcCardRadius"), Child = empty };
        }
        return null;
    }
    private static Border Thumbnail(string cls, string badgeText)
    {
        // 仅供预览的媒体示例，颜色取自 demo 的缩略图，不属于控件主题色。
        var brush = new LinearGradientBrush { StartPoint = new(0, 0, RelativeUnit.Relative), EndPoint = new(1, 1, RelativeUnit.Relative) };
        brush.GradientStops.Add(new(Color.Parse(Has(cls, "b") ? "#FFCC00" : Has(cls, "c") ? "#34C759" : "#5AC8FA"), 0));
        if (!Has(cls, "c")) brush.GradientStops.Add(new(Color.Parse(Has(cls, "b") ? "#FF9500" : "#007AFF"), Has(cls, "b") ? .55 : .6));
        brush.GradientStops.Add(new(Color.Parse(Has(cls, "b") ? "#FF2D55" : Has(cls, "c") ? "#30B0C7" : "#5856D6"), 1));
        var thumb = new Border { Height = 40, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new(6), Background = brush };
        if (badgeText.Length > 0)
        {
            var badge = new CountBadge { Text = badgeText, Height = 16, BorderThickness = new(1.5), Classes = { "blue" }, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, -5, -6, 0) };
            badge.Bind(Border.BorderBrushProperty, Resource("UiSurface")); thumb.Child = badge;
        }
        return thumb;
    }
}

internal sealed class GalleryApp : Avalonia.Application
{
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Simple.SimpleTheme());
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://AvaMedia.Controls/")) { Source = new Uri("avares://AvaMedia.Controls/Themes/Components.axaml") });
    }
}
