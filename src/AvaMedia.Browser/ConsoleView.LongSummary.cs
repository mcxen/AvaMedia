using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AvaMedia.Browser;

public sealed partial class ConsoleView
{
    private static Control LongSummaryResult(JsonNode result)
    {
        var stack = new StackPanel { Spacing = 8 }; var report = result["report"];
        if (report.Text("summary").Length > 0) stack.Children.Add(Ui.Text(report.Text("summary"), raw: true));
        foreach (var chapter in report?["chapters"].Items() ?? [])
        {
            var text = chapter.Text("start") + " – " + chapter.Text("end") + " · " + chapter.Text("title");
            var content = new StackPanel { Spacing = 4 }; content.Children.Add(Ui.Text(chapter.Text("summary"), raw: true));
            foreach (var point in chapter["keyPoints"].Items()) content.Children.Add(Ui.Text(point.Text("text") + " 〔" + string.Join("、", point["citations"].Items().Select(c => c.ToString())) + "〕", raw: true));
            stack.Children.Add(new Expander { Header = text, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        stack.Children.Add(Ui.Text("分段结果", "section"));
        foreach (var segment in result["segments"].Items())
        {
            var range = segment["range"]; var mapped = segment["result"];
            var content = new StackPanel { Spacing = 4 }; content.Children.Add(Ui.Text(mapped.Text("summary"), raw: true));
            foreach (var claim in mapped?["claims"].Items() ?? []) content.Children.Add(Ui.Text(claim.Text("text") + " 〔" + string.Join("、", claim["evidence"].Items().Select(c => c.ToString())) + "〕", raw: true));
            foreach (var ev in mapped?["events"].Items() ?? []) content.Children.Add(Ui.Text(ev.Text("start") + " – " + ev.Text("end") + " · " + ev.Text("text"), raw: true));
            stack.Children.Add(new Expander { Header = range.Text("start") + " – " + range.Text("end") + " · " + mapped.Text("topicLabel"), IsExpanded = true, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        return stack;
    }
}
