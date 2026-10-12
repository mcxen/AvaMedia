using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class VideoSummaryResultWindow
{
    private readonly Job? _summaryJob;
    private LongSummaryReport? _longReport;
    private SegmentOutput[]? _longSegments;
    private readonly DispatcherTimer _longTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _loadingLong;
    private async Task<bool> LoadLongAsync()
    {
        var saved = _summaryJob?.LongSummaryReport is { } live
            ? new LongSummaryTaskSnapshot(live, _summaryJob.SummarySegments, _summaryJob.SummarySegments.Length)
            : await LongVideoSummaryOrchestrator.LoadAsync(_folder, _lifetime.Token);
        if (saved is null) return false;
        RenderLong(saved.Report, saved.Segments);
        if (_summaryJob is not null)
        {
            _longTimer.Tick += async (_, _) =>
            {
                if (_loadingLong || _closed || _summaryJob.LongSummaryReport is not { } report) return;
                var segments = _summaryJob.SummarySegments;
                if (ReferenceEquals(report, _longReport) && segments.SequenceEqual(_longSegments ?? [])) return;
                _loadingLong = true;
                try { RenderLong(report, segments); } finally { _loadingLong = false; }
                await Task.CompletedTask;
            };
            _longTimer.Start();
        }
        return true;
    }
    private void RenderLong(LongSummaryReport report, SegmentOutput[] segments)
    {
        if (_closed) return; _longReport = report; _longSegments = segments;
        var selection = _navigation.SelectedItem as string;
        _pages.Clear(); _fileName.Text = Path.GetFileName(_source ?? _summaryJob?.Inputs.FirstOrDefault() ?? _folder);
        Localization.SetText(_metadata, $"{(report.IsPartial ? Localization.Text("部分结果") + " · " : "")}{MediaTime.Format(report.Duration)} · {segments.Length} 段");
        _play.IsEnabled = _engine is not null && File.Exists(_source);
        var segmentText = LongVideoSummaryOrchestrator.Markdown(report with { Summary = "", Chapters = [] }, segments);
        _pages.Add(new("分段", segmentText, "md", () => LongSegments(segments)));
        if (report.Summary.Length > 0) _pages.Add(new("概览", LongVideoSummaryOrchestrator.Markdown(report, []), "md", () =>
        {
            var stack = new StackPanel { Spacing = 8 }; stack.Children.Add(Card("摘要", SummaryDocumentView.Prose(report.Summary)));
            foreach (var point in report.KeyPoints) stack.Children.Add(LongPoint(point));
            foreach (var limitation in report.Limitations) stack.Children.Add(Ui.Text(limitation, "caption"));
            return Reader(stack);
        }));
        if (report.Chapters.Length > 0) _pages.Add(new("章节", LongVideoSummaryOrchestrator.Markdown(report, []), "md", () =>
        {
            var stack = new StackPanel { Spacing = 8 };
            foreach (var chapter in report.Chapters)
            {
                var body = new StackPanel { Spacing = 6 }; body.Children.Add(TimeButton(chapter.Start));
                body.Children.Add(SummaryDocumentView.Prose(chapter.Summary));
                foreach (var point in chapter.KeyPoints) body.Children.Add(LongPoint(point));
                stack.Children.Add(Card(chapter.Title, body));
            }
            return Reader(stack);
        }));
        _navigation.ItemsSource = _pages.Select(p => p.Title).ToArray();
        _navigation.SelectedIndex = Math.Max(0, _pages.FindIndex(p => p.Title == selection));
        SelectPage(); _copy.IsEnabled = _export.IsEnabled = true; _notice.Text = "";
    }
    private Control LongSegments(SegmentOutput[] segments)
    {
        if (segments.Length == 0) return Empty("等待分段结果", "每段完成后会立即显示。");
        var list = new ListBox { ItemsSource = segments, BorderThickness = new(0),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<SegmentOutput>((output, _) =>
            {
                if (output is null) return null;
                var body = new StackPanel { Spacing = 6 }; body.Children.Add(TimeButton(output.Range.Start));
                body.Children.Add(SummaryDocumentView.Prose(output.Result.Summary));
                foreach (var claim in output.Result.Claims) body.Children.Add(LongPoint(new(claim.Text, claim.Evidence)));
                foreach (var ev in output.Result.Events)
                { var row = new StackPanel { Spacing = 3 }; row.Children.Add(TimeButton(ev.Start)); row.Children.Add(SummaryDocumentView.Prose(ev.Text)); body.Children.Add(row); }
                return Card(output.Result.TopicLabel, body);
            }) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled); return list;
    }
    private Control LongPoint(SummaryKeyPoint point)
    {
        var body = new StackPanel { Spacing = 3 }; body.Children.Add(SummaryDocumentView.Prose(point.Text));
        var links = new WrapPanel();
        foreach (var id in point.Citations)
        {
            var link = Ui.Button(id, async () =>
            {
                var evidence = await new EvidenceStore(_folder).LoadAsync(id, _lifetime.Token);
                if (evidence is not null) await PlayAsync(evidence.Start);
            });
            link.Classes.Add("link"); Localization.SetIsUserText(link, true);
            var tooltip = Ui.Text("读取引用…", "caption"); ToolTip.SetTip(link, tooltip);
            link.PointerEntered += async (_, _) =>
            {
                try { var evidence = await new EvidenceStore(_folder).LoadAsync(id, _lifetime.Token); if (!_closed) { Localization.SetIsUserText(tooltip, true); tooltip.Text = evidence?.Text ?? Localization.Text("引用不可用"); } }
                catch (OperationCanceledException) { }
                catch (Exception error) { if (!_closed) tooltip.Text = error.Message; }
            };
            links.Children.Add(link);
        }
        body.Children.Add(links); return body;
    }
}
