using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class VideoSummaryResultWindow
{
    private void BuildPages(VideoSummaryReport report)
    {
        var overview = report.Sections.FirstOrDefault(section => section.Title == "摘要")?.Text ?? "";
        var overviewText = overview + (report.Keywords.Length > 0 ? "\n\n" + string.Join(" · ", report.Keywords) : "")
            + (report.Highlights.Length > 0 ? "\n\n" + string.Join("\n", report.Highlights.Select(point => "- " + point)) : "");
        if (overviewText.Length > 0) _pages.Add(new("概览", overviewText.Trim() + VideoSummaryService.EvidenceMarkdown(report,
            report.Sections.Where(section => section.Title == "摘要").SelectMany(section => section.Claims).Concat(report.KeywordClaims).Concat(report.HighlightClaims)
                .SelectMany(claim => claim.EvidenceIds)), "md", () => Overview(report, overview)));
        var chapters = report.Sections.FirstOrDefault(section => section.Title == "视频内容总结");
        if (chapters is not null) _pages.Add(new("章节", chapters.Text + VideoSummaryService.EvidenceMarkdown(report, report.Chapters.SelectMany(chapter => chapter.EvidenceIds)), "md", () => Chapters(report, chapters.Text)));
        var analysis = report.Sections.FirstOrDefault(section => section.Title == "内容分析");
        if (analysis is not null) _pages.Add(new("内容分析", analysis.Text + VideoSummaryService.EvidenceMarkdown(report, analysis.Claims.SelectMany(claim => claim.EvidenceIds)), "md", () => Reader(Card("", ClaimDocument(report, analysis)))));
        if (report.Transcript.Count > 0)
        {
            _transcriptPage = _pages.Count;
            _pages.Add(new("字幕与逐字稿", SubtitleTranscript.Timeline(report.Transcript), "txt", () => Transcript(report)));
        }
        if (report.Frames.Count > 0) _pages.Add(new("关键画面", string.Join("\n\n", report.Frames.Select(frame =>
            $"{frame.Id} · {MediaTime.Format(frame.Seconds)}\n{frame.Description}").Concat(report.Sequences.Select(sequence =>
            $"{sequence.Id} · {MediaTime.Format(sequence.Start)}\n{sequence.Description} 〔{string.Join("、", sequence.FrameIds)}〕"))), "txt", () => Frames(report)));
        if (report.Evidence.Count > 0) _pages.Add(new("证据", string.Join("\n\n", report.Evidence.Select(item =>
            $"{item.Id} · {MediaTime.Format(item.Start)}\n{item.Text}")), "txt", () => Evidence(report)));
        if (report.SegmentNotes.Count > 0) _pages.Add(new("分段笔记", string.Join("\n\n", report.SegmentNotes), "md", () => Notes(report)));
        _pages.Add(new("结果信息", $"{report.Source}\n{report.TranscriptSource}\n{report.Language}\n" + string.Join("\n", report.Models)
            + "\n\n" + string.Join("\n", report.Limitations), "txt", () => Information(report)));
    }

    private Control Overview(VideoSummaryReport report, string summary)
    {
        var stack = new StackPanel { Spacing = 20 };
        if (summary.Length > 0) stack.Children.Add(Card("摘要", ClaimDocument(report, report.Sections.First(section => section.Title == "摘要"))));
        if (report.Keywords.Length > 0)
        {
            var tags = new WrapPanel();
            foreach (var word in report.Keywords)
            {
                var text = new SelectableTextBlock { Text = word, TextWrapping = TextWrapping.Wrap };
                Localization.SetIsUserText(text, true);
                tags.Children.Add(new Border { Child = text, Classes = { "summary-chip" }, MaxWidth = 240 });
            }
            stack.Children.Add(Card("关键词", tags));
        }
        if (report.Highlights.Length > 0)
        {
            var points = new StackPanel { Spacing = 16 };
            for (var index = 0; index < report.Highlights.Length; index++)
            {
                var row = new Grid { ColumnDefinitions = new("28,*"), ColumnSpacing = 12 };
                var number = Ui.Text((index + 1).ToString("00"), "caption"); Localization.SetIsUserText(number, true);
                number.VerticalAlignment = VerticalAlignment.Top; number.Margin = new(0, 5, 0, 0); row.Children.Add(number);
                var point = new StackPanel { Spacing = 6 };
                point.Children.Add(SummaryDocumentView.Prose(report.Highlights[index]));
                if (index < report.HighlightClaims.Length) point.Children.Add(EvidenceLinks(report, report.HighlightClaims[index].EvidenceIds));
                Grid.SetColumn(point, 1); row.Children.Add(point); points.Children.Add(row);
            }
            stack.Children.Add(Card("关键要点", points));
        }
        var scope = new StackPanel { Spacing = 8 };
        foreach (var limitation in report.Limitations) scope.Children.Add(Ui.Text(limitation, "caption"));
        stack.Children.Add(new Expander { Header = "结果范围", Content = scope, HorizontalAlignment = HorizontalAlignment.Stretch });
        return Reader(stack);
    }

    private Control Chapters(VideoSummaryReport report, string markdown)
    {
        if (report.Chapters.Length == 0) return Reader(Card("", new SummaryDocumentView(markdown)));
        var stack = new StackPanel { Spacing = 16 };
        for (var index = 0; index < report.Chapters.Length; index++)
        {
            var chapter = report.Chapters[index]; var content = new StackPanel { Spacing = 12 };
            var top = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
            var heading = Ui.Text(chapter.Title, "title"); Localization.SetIsUserText(heading, true); top.Children.Add(heading);
            if (chapter.Seconds is { } time) { var stamp = TimeButton(time); Grid.SetColumn(stamp, 1); top.Children.Add(stamp); }
            content.Children.Add(top); content.Children.Add(new SummaryDocumentView(chapter.Text));
            content.Children.Add(EvidenceLinks(report, chapter.EvidenceIds));
            stack.Children.Add(Card("", content));
        }
        return Reader(stack);
    }

    private Control Transcript(VideoSummaryReport report)
    {
        var root = new Grid { RowDefinitions = new("Auto,*"), Margin = new(24), RowSpacing = 12 };
        var tools = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        tools.Children.Add(_search); var count = Ui.Text("", "caption"); Grid.SetColumn(count, 1); tools.Children.Add(count); root.Children.Add(tools);
        var list = new ListBox { HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "summary-transcript" } };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        list.ItemTemplate = new FuncDataTemplate<SubtitleCue>((cue, _) =>
        {
            if (cue is null) return new Border();
            var row = new Grid { ColumnDefinitions = new("124,*"), ColumnSpacing = 16, Margin = new(8, 10) };
            var stamp = TimeButton(cue.Start.TotalSeconds); stamp.VerticalAlignment = VerticalAlignment.Top; row.Children.Add(stamp);
            var text = SummaryDocumentView.Prose(cue.Text, formatted: false); Grid.SetColumn(text, 1); row.Children.Add(text); return row;
        });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        var generation = 0;
        async Task FilterAsync()
        {
            var revision = ++generation;
            var query = _search.Text?.Trim() ?? "";
            IReadOnlyList<SubtitleCue> matches;
            try
            {
                matches = query.Length == 0 ? report.Transcript : await Task.Run(() => report.Transcript
                    .Where(cue => cue.Text.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray(), _lifetime.Token);
            }
            catch (OperationCanceledException) when (_closed) { return; }
            if (_closed || revision != generation) return;
            list.ItemsSource = matches; Localization.SetText(count, $"{matches.Count} / {report.Transcript.Count} 条");
        }
        timer.Tick += async (_, _) => { timer.Stop(); await FilterAsync(); };
        _search.TextChanged += (_, _) => { generation++; timer.Stop(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
        list.ItemsSource = report.Transcript; Localization.SetText(count, $"{report.Transcript.Count} / {report.Transcript.Count} 条");
        Grid.SetRow(list, 1); root.Children.Add(list); return root;
    }

    private Control Frames(VideoSummaryReport report)
    {
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(Ui.FormattedText($"{report.Frames.Count} 个采样画面", "caption"));
        // A single readable column lets observations wrap beside the image at every supported window size.
        foreach (var frame in report.Frames)
        {
            var row = new Grid { ColumnDefinitions = new("216,*"), ColumnSpacing = 20 };
            var image = new Image { Height = 122, Stretch = Stretch.UniformToFill };
            row.Children.Add(new Border { Child = image, Classes = { "media-preview" }, ClipToBounds = true });
            var details = new StackPanel { Spacing = 8 }; var stamp = TimeButton(frame.Seconds);
            stamp.Content = frame.Id + " · " + EditorTime.Format(frame.Seconds); details.Children.Add(stamp);
            details.Children.Add(SummaryDocumentView.Prose(frame.Description)); Grid.SetColumn(details, 1); row.Children.Add(details);
            stack.Children.Add(Card("", row));
            image.AttachedToVisualTree += async (_, _) => { if (image.Source is null) await LoadImageAsync(image, frame.Image); };
        }
        foreach (var sequence in report.Sequences)
        {
            var details = new StackPanel { Spacing = 8 };
            details.Children.Add(SummaryDocumentView.Prose(sequence.Description));
            details.Children.Add(EvidenceLinks(report, sequence.FrameIds));
            stack.Children.Add(Card(sequence.Id, details));
        }
        return Reader(stack);
    }

    private Control ClaimDocument(VideoSummaryReport report, VideoSummarySection section)
    {
        if (section.Claims.Count == 0) return new SummaryDocumentView(section.Text);
        var stack = new StackPanel { Spacing = 16 };
        foreach (var claim in section.Claims)
        {
            var row = new StackPanel { Spacing = 6 };
            row.Children.Add(SummaryDocumentView.Prose(claim.Text)); row.Children.Add(EvidenceLinks(report, claim.EvidenceIds));
            stack.Children.Add(row);
        }
        return stack;
    }

    private Control EvidenceLinks(VideoSummaryReport report, IEnumerable<string> ids)
    {
        var row = new WrapPanel();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            var evidence = report.Evidence.FirstOrDefault(item => item.Id == id);
            if (evidence is null) continue;
            var button = TimeButton(evidence.Start); button.Content = id + " · " + EditorTime.Format(evidence.Start);
            button.Margin = new(0, 0, 8, 4); ToolTip.SetTip(button, evidence.Text); row.Children.Add(button);
        }
        return row;
    }

    private Control Evidence(VideoSummaryReport report)
    {
        var list = new ListBox { ItemsSource = report.Evidence.OrderBy(item => item.Start).ThenBy(item => item.Id).ToArray(),
            Classes = { "summary-transcript" }, Margin = new(24) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        list.ItemTemplate = new FuncDataTemplate<VideoSummaryEvidence>((item, _) =>
        {
            if (item is null) return new Border();
            var row = new Grid { ColumnDefinitions = new("168,*"), ColumnSpacing = 16, Margin = new(8, 10) };
            var stamp = TimeButton(item.Start); stamp.Content = item.Id + " · " + EditorTime.Format(item.Start);
            stamp.VerticalAlignment = VerticalAlignment.Top; row.Children.Add(stamp);
            var details = new StackPanel { Spacing = 6 }; details.Children.Add(SummaryDocumentView.Prose(item.Text, formatted: false));
            if (item.FrameIds.Length > 1) details.Children.Add(EvidenceLinks(report, item.FrameIds));
            Grid.SetColumn(details, 1); row.Children.Add(details); return row;
        });
        return list;
    }

    private static Control Notes(VideoSummaryReport report)
    {
        var stack = new StackPanel { Spacing = 16 };
        for (var index = 0; index < report.SegmentNotes.Count; index++)
            stack.Children.Add(Card(Localization.Format($"片段 {index + 1}"), new SummaryDocumentView(report.SegmentNotes[index])));
        return Reader(stack);
    }

    private static Control Information(VideoSummaryReport report)
    {
        var stack = new StackPanel { Spacing = 20 };
        var source = new StackPanel { Spacing = 8 };
        source.Children.Add(Ui.FormattedText($"字幕来源：{Localization.Key(report.TranscriptSource)}", "caption"));
        source.Children.Add(Ui.FormattedText($"输出语言：{Localization.Key(report.Language)}", "caption"));
        if (report.Models.Length > 0)
        {
            var models = Ui.Text(string.Join("\n", report.Models), "caption"); Localization.SetIsUserText(models, true); source.Children.Add(models);
        }
        stack.Children.Add(Card("生成信息", source));
        var scope = new StackPanel { Spacing = 12 };
        foreach (var limitation in report.Limitations) scope.Children.Add(Ui.Text(limitation, "caption"));
        stack.Children.Add(Card("结果范围", scope)); return Reader(stack);
    }
}
