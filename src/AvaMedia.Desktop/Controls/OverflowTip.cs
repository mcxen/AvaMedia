using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AvaMedia.Desktop.Controls;

/// <summary>Show repeated text only when its rendered lines have actually been collapsed.</summary>
public sealed class OverflowTip : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<OverflowTip, TextBlock, bool>("IsEnabled");

    static OverflowTip() => IsEnabledProperty.Changed.AddClassHandler<TextBlock>((text, change) =>
    {
        if (change.NewValue is true) ToolTip.AddToolTipOpeningHandler(text, Opening);
        else ToolTip.RemoveToolTipOpeningHandler(text, Opening);
    });

    public static bool GetIsEnabled(TextBlock text) => text.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(TextBlock text, bool value) => text.SetValue(IsEnabledProperty, value);

    private static void Opening(object? sender, CancelRoutedEventArgs args)
    {
        if (sender is TextBlock text && ReferenceEquals(args.Source, text)
            && ToolTip.GetTip(text) is string tip && string.Equals(tip, text.Text, StringComparison.Ordinal)
            && !text.TextLayout.TextLines.Any(line => line.HasCollapsed))
            args.Cancel = true;
    }
}
