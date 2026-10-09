using Avalonia.Controls;

namespace AvaMedia.Desktop;

internal static class ToolInputs
{
    // Leaving optional settings keeps the last usable value for an unfinished input.
    public static void CommitNumber(NumericUpDown input, bool integer = false)
    {
        if (!decimal.TryParse(input.Text, System.Globalization.NumberStyles.Number, input.NumberFormat, out var value))
            value = input.Value ?? input.Minimum;
        value = Math.Clamp(value, input.Minimum, input.Maximum);
        if (integer) value = Math.Round(value, MidpointRounding.AwayFromZero);
        input.Value = value;
        input.Text = value.ToString(input.NumberFormat);
    }
}
