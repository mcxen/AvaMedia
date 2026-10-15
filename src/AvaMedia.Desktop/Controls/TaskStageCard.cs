using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Controls;
using AvaMedia.Core;
using static AvaMedia.Controls.ComponentResources;

namespace AvaMedia.Desktop.Controls;

/// <summary>Hover card of a multi-stage task row: header with position and overall timing, every stage with its
/// StageGlyph, detail and time, and a footer naming the retry or pause point (UISPEC 7.4.1).</summary>
internal sealed class TaskStageCard : StackPanel
{
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold }, _overall = new() { HorizontalAlignment = HorizontalAlignment.Right },
        _footer = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _rows = new() { Spacing = 1 };
    private readonly Border _footerLine = new() { Height = 0.5, Margin = new(0, 6, 0, 0) };

    public TaskStageCard()
    {
        Width = 340; Spacing = 6;
        Classes.Add("task-stage-card");
        var header = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
        header.Children.Add(_title); Grid.SetColumn(_overall, 1); header.Children.Add(_overall);
        Text(_title, "UiText"); Text(_overall, "UiTextSecondary"); Text(_footer, "UiTextSecondary");
        _overall.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        BindResource(_footerLine, Border.BackgroundProperty, "UiDivider");
        Children.Add(header); Children.Add(_rows); Children.Add(_footerLine); Children.Add(_footer);
    }

    public void Update(JobStageSummary summary, TaskRowStatus status)
    {
        if (summary.Pages.Count > 0) { UpdatePages(summary); return; }
        var stages = summary.Stages;
        var index = summary.State == JobState.Failed && stages.FirstOrDefault(s => s.Id == summary.FailedStageId) is { } failed
            ? IndexOf(stages, failed) : summary.CurrentIndex;
        var done = stages.Count(s => s.State is StageState.Done or StageState.Skipped);
        _title.Text = index >= 0 ? Localization.Format($"处理阶段 {index + 1}/{stages.Count}") : Localization.Format($"处理阶段 · 已完成 {done}/{stages.Count}");
        var elapsed = TaskRowStatus.Elapsed(summary);
        _overall.Text = TaskRowStatus.Join(
            summary.State is JobState.Running or JobState.Paused && summary.OverallFraction is { } o ? Localization.Format($"总进度 {TaskRowStatus.Percent(Math.Clamp(o, 0, 1) * 100, 0)}") : "",
            elapsed > TimeSpan.Zero ? Localization.Format($"{Localization.Key(summary.State == JobState.Running ? "已用" : "用时")} {TaskRowStatus.Clock(elapsed)}") : "",
            TaskRowStatus.Remaining(summary));
        while (_rows.Children.Count > stages.Count) _rows.Children.RemoveAt(_rows.Children.Count - 1);
        for (var i = 0; i < stages.Count; i++)
        {
            if (i >= _rows.Children.Count) _rows.Children.Add(new StageRow());
            ((StageRow)_rows.Children[i]).Update(i + 1, stages[i], i == index, summary);
        }
        var footer = summary.State switch
        {
            JobState.Failed when status.RetryStageTitle.Length > 0 => Localization.Format($"可从「{Localization.Key(status.RetryStageTitle)}」重试，之前阶段的结果会保留"),
            JobState.Failed => Localization.Text("只能重新执行全部阶段"),
            JobState.Paused when index >= 0 => Localization.Format($"已暂停于「{Localization.Key(stages[index].Title)}」，继续后从此阶段接着处理"),
            JobState.Cancelled when index >= 0 => Localization.Format($"已在「{Localization.Key(stages[index].Title)}」取消"),
            _ => ""
        };
        _footer.Text = footer;
        _footer.IsVisible = _footerLine.IsVisible = footer.Length > 0;
    }

    private void UpdatePages(JobStageSummary summary)
    {
        var index = summary.Pages.ToList().FindIndex(page => page.Page.Id == summary.CurrentPageId);
        _title.Text = Localization.Format($"任务页 {index + 1}/{summary.Pages.Count}");
        _overall.Text = Localization.Format($"用时 {TaskRowStatus.Clock(TimeSpan.FromTicks(summary.Pages.Sum(page => page.Elapsed.Ticks)))}");
        while (_rows.Children.Count > summary.Pages.Count) _rows.Children.RemoveAt(_rows.Children.Count - 1);
        for (var i = 0; i < summary.Pages.Count; i++)
        {
            var page = summary.Pages[i];
            if (i >= _rows.Children.Count) _rows.Children.Add(new StageRow());
            var state = page.State switch { PageState.Completed => StageState.Done, PageState.Running => StageState.Active,
                PageState.Failed => StageState.Failed, _ => StageState.Pending };
            ((StageRow)_rows.Children[i]).Update(i + 1, new(page.Page.Id, page.Page.Title, state)
                { Detail = page.State == PageState.AwaitingUser ? "等待你操作" : page.Detail, Elapsed = page.Elapsed }, i == index, summary,
                page.State == PageState.AwaitingUser ? "awaiting" : null);
        }
        _footer.Text = Localization.Text(summary.State == JobState.AwaitingUser ? "等待期间不计时，点击“去处理”继续" : "点击任务页可回看已完成步骤");
        _footer.IsVisible = _footerLine.IsVisible = true;
    }

    private static int IndexOf(IReadOnlyList<JobStageItem> stages, JobStageItem stage)
    { for (var i = 0; i < stages.Count; i++) if (ReferenceEquals(stages[i], stage)) return i; return -1; }
    private static void Text(TextBlock text, string brush)
    { BindResource(text, TextBlock.FontSizeProperty, "UiFontCaption"); BindResource(text, TextBlock.ForegroundProperty, brush); }

    private sealed class StageRow : Border
    {
        private readonly StageGlyph _glyph = new() { Width = 14, Height = 14, Margin = new(0, 1, 0, 0) };
        private readonly TextBlock _title = new() { FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis },
            _time = new() { HorizontalAlignment = HorizontalAlignment.Right },
            _detail = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };

        public StageRow()
        {
            Padding = new(6, 4); CornerRadius = new(6);
            var grid = new Grid { ColumnDefinitions = new("16,*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 8 };
            grid.Children.Add(_glyph);
            Grid.SetColumn(_title, 1); grid.Children.Add(_title);
            Grid.SetColumn(_time, 2); grid.Children.Add(_time);
            Grid.SetColumn(_detail, 1); Grid.SetColumnSpan(_detail, 2); Grid.SetRow(_detail, 1); grid.Children.Add(_detail);
            Text(_title, "UiText"); Text(_time, "UiTextSecondary"); Text(_detail, "UiTextSecondary");
            _time.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
            Child = grid;
        }

        public void Update(int number, JobStageItem stage, bool current, JobStageSummary summary, string? glyph = null)
        {
            _glyph.Number = number;
            _glyph.State = glyph ?? TaskRowStatus.Segment(stage.State);
            _title.Text = Localization.Text(stage.Title);
            Text(_title, stage.State is StageState.Pending ? "UiTextSecondary" : "UiText");
            var detail = stage.Detail.Length > 0 ? Localization.Text(stage.Detail)
                : stage.State == StageState.Failed && summary.Error.Length > 0 ? summary.Error
                : stage.State == StageState.Pending ? Localization.Text("等待") : "";
            if (stage.State is StageState.Active or StageState.Paused && stage.Fraction is { } f && double.IsFinite(f))
                detail = TaskRowStatus.Join(detail, TaskRowStatus.Percent(Math.Clamp(f, 0, 1) * 100, 0));
            _detail.Text = detail; _detail.IsVisible = detail.Length > 0;
            ToolTip.SetTip(_detail, detail.Length > 0 ? detail : null);
            Text(_detail, stage.State == StageState.Failed ? "UiDanger" : "UiTextSecondary");
            var elapsed = stage.Elapsed is { } e ? e + (stage.State == StageState.Active && summary.IsClockRunning ? DateTime.UtcNow - summary.SnapshotUtc : TimeSpan.Zero) : (TimeSpan?)null;
            _time.Text = elapsed is { } time && time > TimeSpan.Zero ? time.TotalSeconds < 10
                ? Localization.Format($"{time.TotalSeconds:0.0} 秒") : TaskRowStatus.Clock(time) : "";
            if (stage.State == StageState.Failed) BindResource(this, BackgroundProperty, "AcDangerFill");
            else if (current) BindResource(this, BackgroundProperty, "AcRunningFill");
            else Background = null;
        }
    }
}
