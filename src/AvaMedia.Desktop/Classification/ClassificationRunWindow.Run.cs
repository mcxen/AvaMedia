using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;
using static AvaMedia.Desktop.Classification.ClassifyUi;

namespace AvaMedia.Desktop.Classification;

public sealed partial class ClassificationRunWindow
{
    private readonly TextBlock _percent = T("0", 30, FontWeight.Bold, raw: true);
    private readonly Border _overallBar = new() { Height = 6 };
    private readonly TextBlock _counts = T("", 11.5, brush: Muted, raw: true), _elapsed = T("", 11.5, brush: Muted, raw: true);
    private readonly Grid _stageRow = new();
    private readonly StageCard[] _stages = new StageCard[5];
    private readonly Grid _preview = new() { ClipToBounds = true };
    private readonly ClassifyThumb _previewThumb = new(420) { CornerRadius = new(10) };
    private readonly Canvas _tagLayer = new();
    private readonly TextBlock _lookTitle = T("", 12, brush: Muted, raw: true), _framesTitle = T("", 11.5, brush: Muted, raw: true),
        _consistency = T("", 11.5, brush: Muted, raw: true), _colorsTitle = T("", 11.5, brush: Muted, raw: true);
    private readonly Grid _frames = new() { Height = 52 };
    private readonly Grid _colorBar = new() { Height = 12, ClipToBounds = true };
    private readonly WrapPanel _verdicts = new();
    private readonly TextBlock _reason = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Ink };
    private readonly StackPanel _lookBody = new() { Spacing = 8 };
    private readonly TextBlock _lookEmpty = T("等待第一个文件完成分析", 12, brush: Muted);
    private readonly FileWall _wall = new();
    private readonly TextBlock _groupsTitle = T("", 12, brush: Muted, raw: true);
    private readonly StackPanel _groups = new() { Spacing = 0 };
    private string? _currentKey, _groupsKey;

    private Control BuildRun()
    {
        var grid = new Grid { RowDefinitions = new("Auto,Auto,*"), Margin = new(22, 0, 22, 18), RowSpacing = 12 };
        // Overall progress: big percentage, two-tone bar (analysed + grouped), counts and backend.
        var top = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 14 };
        top.Children.Add(_percent);
        var bars = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        bars.Children.Add(_overallBar);
        var line = new Grid { ColumnDefinitions = new("*,Auto") }; line.Children.Add(_counts); Grid.SetColumn(_elapsed, 1); line.Children.Add(_elapsed);
        bars.Children.Add(line); Grid.SetColumn(bars, 1); top.Children.Add(bars);
        grid.Children.Add(top);

        _stageRow.ColumnDefinitions = new("*,16,*,16,*,16,*,16,*");
        for (var i = 0; i < 5; i++)
        {
            _stages[i] = new StageCard(); Grid.SetColumn(_stages[i], i * 2); _stageRow.Children.Add(_stages[i]);
            if (i < 4)
            {
                var chevron = new LucideIcon { Kind = "chevron-right", Width = 12, Height = 12, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetColumn(chevron, i * 2 + 1); _stageRow.Children.Add(chevron);
            }
        }
        Grid.SetRow(_stageRow, 1); grid.Children.Add(_stageRow);

        var main = new Grid { ColumnDefinitions = new("*,300"), ColumnSpacing = 12 };
        var left = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 };
        left.Children.Add(BuildLook());
        var wallHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        wallHeader.Children.Add(T("文件流", 13, FontWeight.Bold));
        wallHeader.Children.Add(T("按处理顺序 · 绿色完成 · 蓝圈处理中 · 橙色待确认 · 灰色排队", 11.5, brush: Muted));
        var wallPanel = new DockPanel(); DockPanel.SetDock(wallHeader, Dock.Top); wallHeader.Margin = new(0, 0, 0, 8);
        wallPanel.Children.Add(wallHeader); wallPanel.Children.Add(_wall);
        var wallCard = Card(wallPanel); Grid.SetRow(wallCard, 1); left.Children.Add(wallCard);
        main.Children.Add(left);

        var groupsHeader = new StackPanel { Spacing = 2, Margin = new(0, 0, 0, 6) };
        var gt = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        gt.Children.Add(T("分组正在形成", 13, FontWeight.Bold)); gt.Children.Add(_groupsTitle);
        groupsHeader.Children.Add(gt);
        groupsHeader.Children.Add(T("每完成 8 个文件重新归组，角标为本轮新加入", 11.5, brush: Muted));
        var groupsPanel = new DockPanel(); DockPanel.SetDock(groupsHeader, Dock.Top);
        groupsPanel.Children.Add(groupsHeader); groupsPanel.Children.Add(new ScrollViewer { Content = _groups });
        var groupsCard = Card(groupsPanel); Grid.SetColumn(groupsCard, 1); main.Children.Add(groupsCard);
        Grid.SetRow(main, 2); grid.Children.Add(main);
        return grid;
    }

    private Control BuildLook()
    {
        var grid = new Grid { ColumnDefinitions = new("240,*"), ColumnSpacing = 14, Height = 230 };
        _preview.Children.Add(_previewThumb); _preview.Children.Add(_tagLayer);
        _preview.Width = 240; _preview.Height = 230;
        var previewBorder = new Border { CornerRadius = new(10), ClipToBounds = true, Child = _preview };
        grid.Children.Add(previewBorder);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(T("AI 正在看", 13, FontWeight.Bold)); title.Children.Add(_lookTitle);
        _lookBody.Children.Add(title);
        _lookBody.Children.Add(_framesTitle); _lookBody.Children.Add(_frames); _lookBody.Children.Add(_consistency);
        _lookBody.Children.Add(_colorsTitle);
        _lookBody.Children.Add(new Border { CornerRadius = new(4), ClipToBounds = true, Child = _colorBar, BorderBrush = Hairline, BorderThickness = new(1) });
        _lookBody.Children.Add(T("各规则判断", 11.5, brush: Muted));
        _verdicts.Orientation = Orientation.Horizontal; _lookBody.Children.Add(_verdicts);
        _lookBody.Children.Add(new Border { Background = LightBlue, CornerRadius = new(8), Padding = new(10, 7), Child = _reason });
        var right = new Panel(); right.Children.Add(_lookBody); right.Children.Add(_lookEmpty);
        Grid.SetColumn(right, 1); grid.Children.Add(right);
        return Card(grid);
    }

    private void RenderRun()
    {
        var p = _progress;
        _percent.Text = $"{p.Percent:0}%";
        var total = Math.Max(1, p.Files.Count);
        var grouped = p.Files.Count(file => file.GroupId is not null && file.State == FolderFileState.Done);
        var analysed = p.Files.Count(file => file.State is FolderFileState.Done or FolderFileState.Pending or FolderFileState.Failed);
        _overallBar.Child = Bar(grouped / (double)total, Blue, 6, Math.Max(0, analysed - grouped) / (double)total);
        var stage = p.Stages.FirstOrDefault(s => s.Stage == FolderClassificationStage.Tags);
        var outfit = p.Stages.FirstOrDefault(s => s.Stage == FolderClassificationStage.Outfit);
        var parts = new List<string> { $"{L("标签与规则")} {stage?.Done ?? 0}" };
        if (outfit is { Enabled: true }) parts.Add($"{L("服装区域")} {outfit.Done}");
        parts.Add($"{L("已归组")} {grouped}"); parts.Add($"{L("待确认")} {p.Pending}");
        if (p.Failed > 0) parts.Add($"{L("失败")} {p.Failed}");
        _counts.Text = string.Join(" · ", parts);
        _elapsed.Text = $"{L("已用")} {Clock(p.Elapsed)}" + (_job.Activity?.Backend is { Length: > 0 } backend ? " · " + backend : "");
        var stages = p.Stages.Where(s => s.Enabled).ToArray();
        for (var i = 0; i < 5; i++)
        {
            var visible = i < stages.Length;
            _stages[i].IsVisible = visible;
            if (_stageRow.Children.OfType<LucideIcon>().ElementAtOrDefault(i) is { } chevron) chevron.IsVisible = i < stages.Length - 1;
            if (visible) _stages[i].Update(stages[i], p, i == stages.Length - 1 ? p.Regroup : null);
        }
        RenderLook(p.Current ?? p.Files.LastOrDefault(file => file.State is FolderFileState.Done or FolderFileState.Pending));
        _wall.Update(p.Files);
        RenderGroups(p.Groups, p.Regroup);
    }

    private static readonly Point[] TagSpots = [new(.55, .2), new(.08, .3), new(.05, .5), new(.5, .68), new(.1, .1)];

    private void RenderLook(FolderFileVisual? visual)
    {
        _lookBody.IsVisible = visual is not null; _lookEmpty.IsVisible = visual is null;
        _previewThumb.IsVisible = visual is not null;
        if (visual is null) { _tagLayer.Children.Clear(); _currentKey = null; return; }
        var key = visual.Path + "|" + visual.State + "|" + visual.Tags.Length + "|" + visual.Frames.Length + "|" + visual.OutfitColors.Length + "|" + visual.GroupId;
        if (key == _currentKey) return; _currentKey = key;
        _previewThumb.SetSource(visual.Path, visual.IsVideo ? visual.Frames.ElementAtOrDefault(visual.Frames.Length / 2)?.Seconds ?? 1 : null);
        _tagLayer.Children.Clear();
        foreach (var (tag, spot) in visual.Tags.Take(4).Zip(TagSpots))
        {
            var chip = Chip(tag.Tag, tag.Score.ToString("0.00"), Brushes.White);
            chip.BoxShadow = BoxShadows.Parse("0 1 3 0 #22000000");
            Canvas.SetLeft(chip, spot.X * 240); Canvas.SetTop(chip, spot.Y * 230); _tagLayer.Children.Add(chip);
        }
        if (visual.Scene is { } scene)
        {
            var chip = new Border { Background = Dark, CornerRadius = new(11), Padding = new(9, 3), Child = new TextBlock { FontSize = 11.5, Foreground = Brushes.White,
                Text = $"{L("场景")} · {L(scene)}" + (visual.SceneScore is { } s ? $"  {s:0.00}" : "") } };
            Canvas.SetLeft(chip, 8); Canvas.SetBottom(chip, 8); _tagLayer.Children.Add(chip);
        }
        if (visual.IsVideo)
        {
            var chip = new Border { Background = Blue, CornerRadius = new(11), Padding = new(9, 3), Child = new TextBlock { FontSize = 11.5, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold,
                Text = $"{L("视频")} {TimeSpan.FromSeconds(visual.DurationSeconds):mm\\:ss}" } };
            Canvas.SetRight(chip, 8); Canvas.SetTop(chip, 8); _tagLayer.Children.Add(chip);
        }
        var stage = visual.State == FolderFileState.Processing ? L(_job.Activity?.Stage ?? "处理中") : L(visual.State == FolderFileState.Pending ? "待确认" : "已完成");
        _lookTitle.Text = visual.Name + " · " + stage;
        _frames.Children.Clear(); _frames.ColumnDefinitions.Clear();
        var frames = visual.Frames.Length > 0 ? visual.Frames : [new FolderFrameVisual(0, visual.GroupId, true)];
        _framesTitle.Text = visual.IsVideo ? $"{L("采样画面")} · {frames.Length} {L("帧")}" : L("图片");
        for (var i = 0; i < frames.Length; i++)
        {
            // Fixed-size cells: a still image shows one cell, never a stretched strip.
            _frames.ColumnDefinitions.Add(visual.IsVideo && frames.Length >= 6 ? new(1, GridUnitType.Star) : new(new GridLength(68)));
            var cell = new StackPanel { Spacing = 3, Margin = new(i == 0 ? 0 : 3, 0, 0, 0) };
            var thumb = new ClassifyThumb(72) { Height = 40, BorderThickness = new(i + 1 == visual.InconsistentFrame ? 2 : 0), BorderBrush = Orange };
            thumb.SetSource(visual.Path, visual.IsVideo ? frames[i].Seconds : null);
            var time = new TextBlock { Text = visual.IsVideo ? $"{frames[i].Seconds:0.0}s" : "", FontSize = 9, Foreground = Brushes.White, Margin = new(3, 0), VerticalAlignment = VerticalAlignment.Bottom };
            var holder = new Panel(); holder.Children.Add(thumb); holder.Children.Add(time);
            cell.Children.Add(holder);
            cell.Children.Add(new Border { Height = 4, CornerRadius = new(2), Background = frames[i].Consistent ? Blue : Orange });
            Grid.SetColumn(cell, i); _frames.Children.Add(cell);
        }
        _consistency.Text = visual.IsVideo
            ? $"{L("每帧归属一致率")} {visual.FrameConsistency:P0}" + (visual.InconsistentFrame > 0 ? $" · {L("第")} {visual.InconsistentFrame} {L("帧服装不同")}" : "")
            : "";
        _consistency.IsVisible = visual.IsVideo;
        _colorBar.Children.Clear(); _colorBar.ColumnDefinitions.Clear();
        var colors = visual.OutfitColors.Where(c => c.Share > .005).ToArray();
        _colorsTitle.Text = colors.Length > 0 ? $"{L("服装主色")} · {L("人物占画面")} {visual.PersonRatio:P0}" : L("服装主色") + " · " + L("暂无");
        for (var i = 0; i < colors.Length; i++)
        {
            _colorBar.ColumnDefinitions.Add(new(colors[i].Share, GridUnitType.Star));
            var block = new Border { Background = Brush(colors[i].Hex) }; Grid.SetColumn(block, i); _colorBar.Children.Add(block);
        }
        _verdicts.Children.Clear();
        foreach (var verdict in visual.Verdicts)
        {
            var chip = Chip(verdict.RuleName, verdict.Value, rawValue: false); chip.Margin = new(0, 0, 6, 4); _verdicts.Children.Add(chip);
        }
        _reason.Inlines!.Clear();
        _reason.Inlines.Add(new Avalonia.Controls.Documents.Run(L("归组理由")) { FontWeight = FontWeight.Bold });
        _reason.Inlines.Add(new Avalonia.Controls.Documents.Run(" · " + visual.Reason));
    }

    private void RenderGroups(IReadOnlyList<FolderGroupView> groups, int regroup)
    {
        _groupsTitle.Text = regroup > 0 ? $"{L("第")} {regroup} {L("次归组")}" : L("尚未归组");
        var key = regroup + ":" + string.Join(",", groups.Select(g => g.CategoryId + g.Count + "+" + g.Added));
        if (key == _groupsKey) return; _groupsKey = key;
        _groups.Children.Clear();
        if (groups.Count == 0) { _groups.Children.Add(T("完成服装区域识别后开始归组", 12, brush: Muted)); return; }
        var index = 0;
        foreach (var group in groups.OrderByDescending(g => g.Count).Take(40))
        {
            index++;
            var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, Margin = new(0, 6) };
            var mosaic = new Mosaic(52, 62, group.CoverPaths);
            var holder = new Panel(); holder.Children.Add(mosaic);
            if (group.Added > 0)
            {
                var badge = Badge("+" + group.Added, Blue); badge.HorizontalAlignment = HorizontalAlignment.Right; badge.VerticalAlignment = VerticalAlignment.Top;
                badge.Margin = new(0, -4, -6, 0); holder.Children.Add(badge);
            }
            row.Children.Add(holder);
            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(T($"{index:00} {group.Name}", 12.5, FontWeight.SemiBold, raw: true));
            if (group.ShootName is { } shoot) text.Children.Add(T(shoot, 11, brush: Muted, raw: true));
            var count = new TextBlock { FontSize = 11, Foreground = Muted };
            count.Inlines!.Add(new Avalonia.Controls.Documents.Run(group.Count.ToString()) { FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Ink });
            count.Inlines.Add(new Avalonia.Controls.Documents.Run(" " + L("个文件")));
            text.Children.Add(count);
            Grid.SetColumn(text, 1); row.Children.Add(text);
            _groups.Children.Add(row);
            _groups.Children.Add(new Rectangle { Height = 1, Fill = Hairline });
        }
    }

    /// <summary>One pipeline stage card: dot, name, model, stacked sample covers, counts and progress bar.</summary>
    private sealed class StageCard : Border
    {
        private readonly Control _dotHolder = new Panel();
        private readonly TextBlock _name = T("", 12.5, FontWeight.SemiBold), _model = T("", 11, brush: Muted, raw: true), _count = T("", 14, FontWeight.Bold, raw: true);
        private readonly Panel _bar = new() { Height = 4 };
        private readonly Mosaic _stack = new(34, 30, [], stacked: true);
        public StageCard()
        {
            Background = Surface; CornerRadius = new(14); Padding = new(12, 10); BorderThickness = new(1.5);
            BoxShadow = BoxShadows.Parse("0 1 3 0 #14000000");
            var grid = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto"), ColumnDefinitions = new("*,Auto") };
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; title.Children.Add(_dotHolder); title.Children.Add(_name);
            Grid.SetColumnSpan(title, 2); grid.Children.Add(title);
            _model.Margin = new(13, 0, 0, 0); Grid.SetRow(_model, 1); Grid.SetColumnSpan(_model, 2); grid.Children.Add(_model);
            _stack.Margin = new(0, 4, 0, 4); Grid.SetRow(_stack, 2); grid.Children.Add(_stack);
            _count.VerticalAlignment = VerticalAlignment.Bottom; _count.Margin = new(0, 0, 0, 6);
            Grid.SetRow(_count, 2); Grid.SetColumn(_count, 1); grid.Children.Add(_count);
            Grid.SetRow(_bar, 3); Grid.SetColumnSpan(_bar, 2); grid.Children.Add(_bar);
            Child = grid;
        }
        public void Update(FolderStageProgress stage, FolderClassificationProgress progress, int? regroup)
        {
            var done = stage.Total > 0 && stage.Done >= stage.Total;
            var brush = done ? Green : stage.Active || stage.Done > 0 ? Blue : Muted;
            ((Panel)_dotHolder).Children.Clear(); ((Panel)_dotHolder).Children.Add(Dot(brush));
            _name.Text = L(stage.Name);
            _model.Text = stage.Stage switch
            {
                FolderClassificationStage.Scan => stage.Total.ToString(),
                FolderClassificationStage.Group => L("每 8 个结果"),
                _ => L(stage.Model)
            };
            _count.Text = regroup is { } r ? $"{r} {L("次")}" : $"{stage.Done} / {stage.Total}";
            BorderBrush = stage.Active ? Blue : Brushes.Transparent;
            _bar.Children.Clear(); _bar.Children.Add(Bar(regroup is not null ? (progress.Files.Count == 0 ? 0 : progress.Files.Count(f => f.GroupId is not null) / (double)progress.Files.Count) : stage.Fraction, done ? Green : Blue));
            var samples = progress.Files.Where(f => f.State is FolderFileState.Done or FolderFileState.Pending).Take(3).Select(f => f.Path).ToArray();
            _stack.SetPaths(samples);
        }
    }
}
