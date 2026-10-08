using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaMedia.Desktop.Controls;

/// <summary>Selectable, native reading layout for generated notes. No HTML or remote content is executed.</summary>
internal sealed class SummaryDocumentView : StackPanel
{
    private static readonly Regex ListMarker = new(@"^([-*+]\s+|\d+[.)、]\s*)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Inline = new(@"(\*\*([^*]+)\*\*|__([^_]+)__|`([^`]+)`|\[([^\]]+)\]\([^)]*\))", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public SummaryDocumentView(string markdown)
    {
        Spacing = 12;
        var lines = markdown.Replace("\r", "").Split('\n');
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count == 0) return;
            Children.Add(Prose(string.Join("\n", paragraph))); paragraph.Clear();
        }
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                Flush(); var code = new List<string>();
                while (++index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[index]);
                var text = Prose(string.Join("\n", code), formatted: false); text.Classes.Add("summary-code");
                Children.Add(new Border { Classes = { "summary-inset" }, Child = text }); continue;
            }
            if (index + 1 < lines.Length && line.Contains('|') && IsTableDivider(lines[index + 1]))
            {
                Flush(); var rows = new List<string[]> { Cells(line) }; index++;
                while (index + 1 < lines.Length && lines[index + 1].Contains('|') && !string.IsNullOrWhiteSpace(lines[index + 1])) rows.Add(Cells(lines[++index]));
                Children.Add(Table(rows)); continue;
            }
            var headingLength = line.TakeWhile(character => character == '#').Count();
            if (headingLength is > 0 and <= 6 && line.Length > headingLength && line[headingLength] == ' ')
            {
                Flush(); var heading = Prose(line[(headingLength + 1)..]); heading.Classes.Add("summary-heading");
                heading.Margin = new(0, Children.Count > 0 ? 8 : 0, 0, 0); Children.Add(heading); continue;
            }
            if (line is "---" or "***" or "___") { Flush(); Children.Add(new Border { Classes = { "summary-rule" } }); continue; }
            var marker = ListMarker.Match(line);
            if (marker.Success)
            {
                Flush(); var row = new Grid { ColumnDefinitions = new("28,*"), ColumnSpacing = 4 };
                var label = Ui.Text(char.IsDigit(line[0]) ? marker.Value.Trim() : "•", "caption");
                Localization.SetIsUserText(label, true); label.VerticalAlignment = VerticalAlignment.Top; label.Margin = new(0, 4, 0, 0);
                row.Children.Add(label); var text = Prose(line[marker.Length..]); Grid.SetColumn(text, 1); row.Children.Add(text); Children.Add(row); continue;
            }
            if (line.StartsWith('>'))
            {
                Flush(); Children.Add(new Border { Classes = { "summary-quote" }, Child = Prose(line.TrimStart('>', ' ')) }); continue;
            }
            paragraph.Add(line);
        }
        Flush();
    }

    internal static SelectableTextBlock Prose(string text, bool formatted = true)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "summary-prose" } };
        Localization.SetIsUserText(block, true);
        if (!formatted) { block.Text = text; return block; }
        var offset = 0;
        foreach (Match match in Inline.Matches(text))
        {
            if (match.Index > offset) block.Inlines!.Add(new Run(text[offset..match.Index]));
            if (match.Groups[2].Success || match.Groups[3].Success)
                block.Inlines!.Add(new Run(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value) { FontWeight = FontWeight.SemiBold });
            else block.Inlines!.Add(new Run(match.Groups[4].Success ? match.Groups[4].Value : match.Groups[5].Value));
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) block.Inlines!.Add(new Run(text[offset..]));
        return block;
    }

    private static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(value => value.Trim()).ToArray();
    private static bool IsTableDivider(string line) => Cells(line).All(cell => cell.Length > 0 && cell.Trim(':', ' ').Length >= 3 && cell.Trim(':', ' ').All(character => character == '-'));
    private static Control Table(List<string[]> rows)
    {
        var columns = rows.Max(row => row.Length); var grid = new Grid();
        for (var column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var row = 0; row < rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var column = 0; column < columns; column++)
            {
                var text = Prose(column < rows[row].Length ? rows[row][column] : "");
                if (row == 0) text.FontWeight = FontWeight.SemiBold;
                var cell = new Border { Child = text, Classes = { "summary-table-cell" } };
                if (row == 0) cell.Classes.Add("summary-table-header");
                Grid.SetRow(cell, row); Grid.SetColumn(cell, column); grid.Children.Add(cell);
            }
        }
        return grid;
    }
}
