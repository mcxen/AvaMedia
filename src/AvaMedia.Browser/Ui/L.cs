using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace AvaMedia.Browser;

/// <summary>
/// Browser localization. Chinese source strings are translated by exact match (desktop dictionary plus
/// remote additions). Text marked with the "raw" class (file names, paths, user text) is never translated.
/// </summary>
internal static class L
{
    private static Dictionary<string, string> _english = new();
    public static bool English { get; private set; }

    public static void Initialize(string language)
    {
        English = !language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        if (!English) return;
        foreach (var name in new[] { "AvaMedia.Browser.Strings.Desktop.json", "AvaMedia.Browser.Strings.Remote.json" })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream is null) continue;
            using var document = JsonDocument.Parse(stream);
            foreach (var property in document.RootElement.EnumerateObject()) _english[property.Name] = property.Value.GetString() ?? property.Name;
        }
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, change) =>
        {
            if (change.NewValue is string text && !block.Classes.Contains("raw") && _english.TryGetValue(text, out var translated) && translated != text)
                block.Text = translated;
        });
        ContentControl.ContentProperty.Changed.AddClassHandler<ContentControl>((control, change) =>
        {
            if (change.NewValue is string text && !control.Classes.Contains("raw") && _english.TryGetValue(text, out var translated) && translated != text)
                control.Content = translated;
        });
        HeaderedContentControl.HeaderProperty.Changed.AddClassHandler<HeaderedContentControl>((control, change) =>
        {
            if (change.NewValue is string text && _english.TryGetValue(text, out var translated) && translated != text) control.Header = translated;
        });
    }

    public static string T(string text) => English && _english.TryGetValue(text, out var translated) ? translated : text;

    /// <summary>Formats a template whose Chinese key uses {0}, {1}… placeholders.</summary>
    public static string F(string template, params object?[] args) => string.Format(T(template), args);
}
