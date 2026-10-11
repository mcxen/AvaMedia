using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;

namespace AvaMedia.Desktop.Controls;

// Only reviewed, complete action labels receive icons. Content stays a string for localization,
// automation, existing click handlers and dynamic states; unknown labels keep the normal template.
public sealed class ActionButtonTemplateConverter : IMultiValueConverter
{
    internal const double IconWidth = 16;
    internal const double IconSpacing = 6;
    private static readonly IReadOnlyDictionary<string, LabelTemplate> Templates = Load();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.Count >= 2 && values[1] is false && values[0] is string label
            && Templates.TryGetValue(label, out var template) ? template : null;

    internal static double ExtraWidth(IDataTemplate? template, string label)
        => template is LabelTemplate && Templates.ContainsKey(label) ? IconWidth + IconSpacing : 0;

    private static IReadOnlyDictionary<string, LabelTemplate> Load()
    {
        using var actions = typeof(ActionButtonTemplateConverter).Assembly.GetManifestResourceStream("AvaMedia.Desktop.ActionButtonIcons.json")!;
        using var translations = typeof(ActionButtonTemplateConverter).Assembly.GetManifestResourceStream("AvaMedia.Desktop.Strings.en-US.json")!;
        var semantics = JsonSerializer.Deserialize<Dictionary<string, string>>(actions)!;
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(translations)!;
        var result = new Dictionary<string, LabelTemplate>(StringComparer.Ordinal);
        var byIcon = new Dictionary<string, LabelTemplate>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        void Add(string label, LabelTemplate template)
        {
            if (ambiguous.Contains(label)) return;
            if (result.TryGetValue(label, out var previous) && previous.Kind != template.Kind)
            { result.Remove(label); ambiguous.Add(label); return; }
            result[label] = template;
        }
        foreach (var (label, kind) in semantics)
        {
            if (!byIcon.TryGetValue(kind, out var template)) byIcon.Add(kind, template = new(kind));
            Add(label, template);
            if (english.TryGetValue(label, out var translated)) Add(translated, template);
        }
        return result;
    }

    private sealed class LabelTemplate(string kind) : IDataTemplate
    {
        public string Kind { get; } = kind;
        public bool Match(object? data) => data is string;
        public Control? Build(object? data)
        {
            var icon = new LucideIcon { Kind = Kind, Width = IconWidth, Height = IconWidth, VerticalAlignment = VerticalAlignment.Center };
            icon.Bind(LucideIcon.ForegroundProperty, new Binding(nameof(Button.Foreground))
            { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) } });
            return new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = IconSpacing, VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, new AccessText { Text = data as string, VerticalAlignment = VerticalAlignment.Center } }
            };
        }
    }
}
