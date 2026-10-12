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
        var label = Text(text, role);
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
        if (control is TextBox or ComboBox or CheckBox or RadioButton or ToggleSwitch or Slider) control.Classes.Add("ac");
        return control;
    }

    public static Card ComponentCard(Control content) => new() { Child = content };
}
