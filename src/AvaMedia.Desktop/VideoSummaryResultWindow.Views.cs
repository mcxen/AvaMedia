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
        if (analysis is not null) _pages.Add(new("内容分析", analysis.Text + VideoSummaryService.EvidenceMarkdown(report, analysis.Claims.SelectMany(claim => claim.EvidenceIds)), "md", () => Analysis(report, analysis)));
        if (report.Transcript.Count > 0)
        {
            _transcriptPage = _pages.Count;
            _pages.Add(new("字幕", SubtitleTranscript.Timeline(report.Transcript), "txt", () => Transcript(report)));
        }
        if (report.Frames.Count > 0) _pages.Add(new("画面", string.Join("\n\n", report.Frames.Select(frame =>
            $"{frame.Id} · {MediaTime.Format(frame.Seconds)}\n{frame.Description}").Concat(report.Sequences.Select(sequence =>
            $"{sequence.Id} · {MediaTime.Format(sequence.Start)}\n{sequence.Description} 〔{string.Join("、", sequence.FrameIds)}〕"))), "txt", () => Frames(report)));
        if (report.Evidence.Count > 0) _pages.Add(new("证据", string.Join("\n\n", report.Evidence.Select(item =>
            $"{item.Id} · {MediaTime.Format(item.Start)}\n{item.Text}")), "txt", () => Evidence(report)));
        if (report.SegmentNotes.Count > 0) _pages.Add(new("笔记", string.Join("\n\n", report.SegmentNotes), "md", () => Notes(report)));
        _pages.Add(new("信息", $"{report.Source}\n{report.TranscriptSource}\n{report.Language}\n" + string.Join("\n", report.Models)
            + "\n\n" + string.Join("\n", report.Limitations), "txt", () => Information(report)));
    }

    private Control Overview(VideoSummaryReport report, string summary)
    {
        var primary = new StackPanel { Spacing = 8 };
        if (summary.Length > 0) primary.Children.Add(Card("摘要", ClaimDocument(report, report.Sections.First(section => section.Title == "摘要"))));
        if (report.Highlights.Length > 0)
        {
            var points = new StackPanel { Spacing = 6 };
            for (var index = 0; index < report.Highlights.Length; index++)
            {
                var row = new Grid { ColumnDefinitions = new("22,*"), ColumnSpacing = 6 };
                var number = Ui.Text((index + 1).ToString("00"), "caption"); Localization.SetIsUserText(number, true);
                number.VerticalAlignment = VerticalAlignment.Top; number.Margin = new(0, 2, 0, 0); row.Children.Add(number);
                var point = new StackPanel { Spacing = 2 };
                point.Children.Add(SummaryDocumentView.Prose(report.Highlights[index]));
                if (index < report.HighlightClaims.Length) point.Children.Add(EvidenceLinks(report, report.HighlightClaims[index].EvidenceIds));
                Grid.SetColumn(point, 1); row.Children.Add(point); points.Children.Add(row);
            }
            primary.Children.Add(Card("要点", points));
        }
        if (report.Limitations.Length > 0)
        {
            var scope = new StackPanel { Spacing = 2 };
            foreach (var limitation in report.Limitations) scope.Children.Add(Ui.Text(limitation, "caption"));
            primary.Children.Add(Card("范围", scope));
        }

        var secondary = new StackPanel { Spacing = 8 };
        if (report.Keywords.Length > 0)
        {
            var tags = new WrapPanel();
            foreach (var word in report.Keywords)
            {
                var text = new SelectableTextBlock { Text = word, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
                Localization.SetIsUserText(text, true);
                tags.Children.Add(new Border { Child = text, Classes = { "summary-chip" }, MaxWidth = 180 });
            }
            secondary.Children.Add(Card("关键词", tags));
        }
        if (report.Chapters.Length > 0)
        {
            var list = new StackPanel { Spacing = 4 };
            foreach (var chapter in report.Chapters)
            {
                var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
                if (chapter.Seconds is { } time) row.Children.Add(TimeButton(time));
                var title = Ui.Text(chapter.Title); Localization.SetIsUserText(title, true);
                title.TextTrimming = TextTrimming.CharacterEllipsis; title.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(title, 1); row.Children.Add(title); list.Children.Add(row);
            }
            secondary.Children.Add(Card("章节", list));
        }
        if (report.Frames.Count > 0)
        {
            var film = new WrapPanel();
            foreach (var frame in report.Frames)
            {
                var image = new Image { Width = 148, Height = 84, Stretch = Stretch.UniformToFill };
                var tile = new StackPanel { Spacing = 2, Margin = new(0, 0, 6, 6), Width = 148 };
                tile.Children.Add(new Border { Child = image, Classes = { "media-preview" }, ClipToBounds = true, Height = 84 });
                tile.Children.Add(TimeButton(frame.Seconds)); film.Children.Add(tile);
                image.AttachedToVisualTree += async (_, _) => { if (image.Source is null) await LoadImageAsync(image, frame.Image); };
            }
            secondary.Children.Add(Card("画面", film));
        }
        if (secondary.Children.Count == 0) return Reader(primary);
        if (primary.Children.Count == 0) return Reader(secondary);
        return Split(primary, secondary);
    }

    private Control Chapters(VideoSummaryReport report, string markdown)
    {
        if (report.Chapters.Length == 0) return Reader(Card("", new SummaryDocumentView(markdown)));
        var stack = new StackPanel { Spacing = 6 };
        for (var index = 0; index < report.Chapters.Length; index++)
        {
            var chapter = report.Chapters[index]; var content = new StackPanel { Spacing = 4 };
            var top = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
            var heading = Ui.Text(chapter.Title, "summary-section"); Localization.SetIsUserText(heading, true); top.Children.Add(heading);
            if (chapter.Seconds is { } time) { var stamp = TimeButton(time); Grid.SetColumn(stamp, 1); top.Children.Add(stamp); }
            content.Children.Add(top); content.Children.Add(new SummaryDocumentView(chapter.Text));
            content.Children.Add(EvidenceLinks(report, chapter.EvidenceIds));
            stack.Children.Add(Card("", content));
        }
        return Reader(stack);
    }

    private Control Analysis(VideoSummaryReport report, VideoSummarySection section)
    {
        var document = ClaimDocument(report, section);
        if (report.Evidence.Count == 0) return Reader(Card("", document));
        var cited = section.Claims.SelectMany(claim => claim.EvidenceIds).ToHashSet(StringComparer.Ordinal);
        var related = report.Evidence.Where(item => cited.Contains(item.Id)).OrderBy(item => item.Start).ToArray();
        if (related.Length == 0) return Reader(Card("", document));
        var side = new StackPanel { Spacing = 4 };
        foreach (var item in related.Take(24))
        {
            var row = new StackPanel { Spacing = 1, Margin = new(0, 0, 0, 4) };
            var stamp = TimeButton(item.Start); stamp.Content = item.Id + " · " + EditorTime.Format(item.Start); row.Children.Add(stamp);
            row.Children.Add(SummaryDocumentView.Prose(item.Text, formatted: false)); side.Children.Add(row);
        }
        return Split(Card("", document), Card("依据", side));
    }

    private Control Transcript(VideoSummaryReport report)
    {
        var root = new Grid { RowDefinitions = new("Auto,*"), Margin = new(12, 0, 12, 8), RowSpacing = 6 };
        var tools = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        _search.MinHeight = 28; tools.Children.Add(_search); var count = Ui.Text("", "caption"); Grid.SetColumn(count, 1); tools.Children.Add(count); root.Children.Add(tools);
        var list = new ListBox { HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "summary-transcript" } };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        list.ItemTemplate = new FuncDataTemplate<SubtitleCue>((cue, _) =>
        {
            if (cue is null) return new Border();
            var row = new Grid { ColumnDefinitions = new("92,*"), ColumnSpacing = 8, Margin = new(4, 2) };
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
        var stack = new StackPanel { Spacing = 8 };
        var film = new WrapPanel();
        foreach (var frame in report.Frames)
        {
            var image = new Image { Width = 220, Height = 124, Stretch = Stretch.UniformToFill };
            var tile = new StackPanel { Spacing = 2, Width = 220, Margin = new(0, 0, 8, 8) };
            tile.Children.Add(new Border { Child = image, Classes = { "media-preview" }, ClipToBounds = true, Height = 124 });
            var stamp = TimeButton(frame.Seconds); stamp.Content = frame.Id + " · " + EditorTime.Format(frame.Seconds); tile.Children.Add(stamp);
            tile.Children.Add(SummaryDocumentView.Prose(frame.Description)); film.Children.Add(tile);
            image.AttachedToVisualTree += async (_, _) => { if (image.Source is null) await LoadImageAsync(image, frame.Image); };
        }
        stack.Children.Add(film);
        foreach (var sequence in report.Sequences)
        {
            var details = new StackPanel { Spacing = 4 };
            details.Children.Add(SummaryDocumentView.Prose(sequence.Description));
            details.Children.Add(EvidenceLinks(report, sequence.FrameIds));
            stack.Children.Add(Card(sequence.Id, details));
        }
        return Reader(stack);
    }

    private Control ClaimDocument(VideoSummaryReport report, VideoSummarySection section)
    {
        if (section.Claims.Count == 0) return new SummaryDocumentView(section.Text);
        var stack = new StackPanel { Spacing = 6 };
        foreach (var claim in section.Claims)
        {
            var row = new StackPanel { Spacing = 2 };
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
            button.Margin = new(0, 0, 4, 2); ToolTip.SetTip(button, evidence.Text); row.Children.Add(button);
        }
        return row;
    }

    private Control Evidence(VideoSummaryReport report)
    {
        var list = new ListBox { ItemsSource = report.Evidence.OrderBy(item => item.Start).ThenBy(item => item.Id).ToArray(),
            Classes = { "summary-transcript" }, Margin = new(12, 0, 12, 8) };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        list.ItemTemplate = new FuncDataTemplate<VideoSummaryEvidence>((item, _) =>
        {
            if (item is null) return new Border();
            var row = new Grid { ColumnDefinitions = new("148,*"), ColumnSpacing = 8, Margin = new(4, 2) };
            var stamp = TimeButton(item.Start); stamp.Content = item.Id + " · " + EditorTime.Format(item.Start);
            stamp.VerticalAlignment = VerticalAlignment.Top; row.Children.Add(stamp);
            var details = new StackPanel { Spacing = 2 }; details.Children.Add(SummaryDocumentView.Prose(item.Text, formatted: false));
            if (item.FrameIds.Length > 1) details.Children.Add(EvidenceLinks(report, item.FrameIds));
            Grid.SetColumn(details, 1); row.Children.Add(details); return row;
        });
        return list;
    }

    private static Control Notes(VideoSummaryReport report)
    {
        var columns = new Grid { ColumnDefinitions = new("*,8,*"), ColumnSpacing = 0 };
        var left = new StackPanel { Spacing = 6 }; var right = new StackPanel { Spacing = 6 };
        for (var index = 0; index < report.SegmentNotes.Count; index++)
            (index % 2 == 0 ? left : right).Children.Add(Card(Localization.Format($"片段 {index + 1}"), new SummaryDocumentView(report.SegmentNotes[index])));
        columns.Children.Add(left); Grid.SetColumn(right, 2); columns.Children.Add(right);
        return Reader(columns);
    }

    private static Control Information(VideoSummaryReport report)
    {
        var source = new StackPanel { Spacing = 4 };
        source.Children.Add(Ui.FormattedText($"字幕来源：{Localization.Key(report.TranscriptSource)}", "caption"));
        source.Children.Add(Ui.FormattedText($"输出语言：{Localization.Key(report.Language)}", "caption"));
        if (report.Models.Length > 0)
        {
            var models = Ui.Text(string.Join("\n", report.Models), "caption"); Localization.SetIsUserText(models, true); source.Children.Add(models);
        }
        var scope = new StackPanel { Spacing = 4 };
        foreach (var limitation in report.Limitations) scope.Children.Add(Ui.Text(limitation, "caption"));
        return Split(Card("生成信息", source), Card("结果范围", scope));
    }
}
