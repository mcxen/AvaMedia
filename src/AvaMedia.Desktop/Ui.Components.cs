using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Controls;

namespace AvaMedia.Desktop;

internal static partial class Ui
{
    // Shared factories for the adopted windows. Keep the roles in the component
    // contract so the host's classic themes can continue to select native faces.
    public static TextBlock ComponentText(string text, string? role = null)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (role is not null) label.Classes.Add(role);
        var caption = role is "caption" or "settingsDescription";
        if (role is "heading" or "settingsHeading" or "settingsTitle")
        {
            label.Classes.Add("ac-section");
            label.FontWeight = FontWeight.SemiBold;
        }
        label.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension(
            caption ? "AcCaptionFont" : label.Classes.Contains("ac-section") ? "AcSectionFont" : "AcBodyFont"));
        return label;
    }

    public static TextBlock ComponentFormattedText(FormattableString text, string? role = null)
    {
        var label = ComponentText("", role);
        Localization.SetText(label, text);
        return label;
    }

    public static TextBlock ComponentStatus(string text = "", string? role = "caption")
    {
        var label = ComponentText(text, role);
        StableLayout.SetStatusLines(label, 2);
        return label;
    }

    public static TextBox ComponentInput(string value = "", int width = 0)
        => new() { Text = value, MinWidth = width, HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "ac" } };

    public static NumericUpDown ComponentNumber(double value, double minimum, double maximum, double step = 1)
        => new() { Value = (decimal)value, Minimum = (decimal)minimum, Maximum = (decimal)maximum,
            Increment = (decimal)step, FormatString = step >= 1 ? "0" : "0.##", Classes = { "ac" } };

    public static ComboBox ComponentCombo(IEnumerable<string> items, string selected)
    {
        var list = items.ToArray();
        return new() { ItemsSource = list, SelectedItem = list.Contains(selected) ? selected : list.FirstOrDefault(),
            HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "ac" } };
    }

    public static Control ComponentAdjust(NumericUpDown input, double? maximum = null)
    {
        input.Classes.Add("ac");
        var row = Adjust(input, maximum);
        if (row is Panel panel)
            foreach (var slider in panel.Children.OfType<Slider>()) slider.Classes.Add("ac");
        return row;
    }

    public static Control ComponentParameter(Control control, string label)
    {
        Avalonia.Automation.AutomationProperties.SetName(control, label);
        if (control is NumericUpDown number) return ComponentAdjust(number);
        if (control is TextBox or ComboBox or CheckBox or RadioButton or ToggleSwitch or Slider or Avalonia.Controls.Primitives.ToggleButton or Expander or TreeView or TabControl or ListBox) control.Classes.Add("ac");
        return control;
    }

    public static Card ComponentCard(Control content) => new() { Child = content };

    public static async Task<bool> ComponentConfirm(Window owner, string title, string message, string action)
    {
        var dialog = new Window { Title = title, Width = 480, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel();
        body.Bind(StackPanel.SpacingProperty, new DynamicResourceExtension("AcGap"));
        body.Children.Add(ComponentText(message));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Bind(StackPanel.SpacingProperty, new DynamicResourceExtension("AcRowGap"));
        buttons.Children.Add(DialogButton("取消", () => dialog.Close(false)));
        buttons.Children.Add(DialogButton(action, () => dialog.Close(true), isDefault: true));
        body.Children.Add(buttons);
        var sheet = new Border { Classes = { "ac-sheet" }, Child = body };
        dialog.Content = Controls.WindowChrome.WrapDialog(dialog, sheet);
        return await dialog.ShowDialog<bool>(owner);
    }

    public static Task ComponentMessageDialog(Window owner, string title, string message, IReadOnlyList<string> details)
    {
        var dialog = new Window { Title = title, Width = 480, Height = 300,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new Grid { RowDefinitions = new("Auto,*,Auto") };
        body.Bind(Grid.RowSpacingProperty, new DynamicResourceExtension("AcGap"));
        body.Children.Add(new Notice { Title = Localization.Text(title), Detail = Localization.Text(message), State = "warning" });
        var names = ComponentText(string.Join("\n", details));
        Localization.SetIsUserText(names, true);
        body.Children.Add(new ScrollViewer { Content = names, [Grid.RowProperty] = 1 });
        var okay = DialogButton("好", () => dialog.Close(), isDefault: true);
        okay.HorizontalAlignment = HorizontalAlignment.Right;
        body.Children.Add(okay); Grid.SetRow(okay, 2);
        dialog.Content = Controls.WindowChrome.WrapDialog(dialog, ComponentCard(body));
        return dialog.ShowDialog(owner);
    }
}
