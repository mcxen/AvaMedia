using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;
using static AvaMedia.Desktop.Classification.ClassifyUi;

namespace AvaMedia.Desktop.Classification;

public sealed partial class ClassificationRunWindow
{
    private readonly TextBlock _percent = T("", 26, FontWeight.Bold, raw: true);
    private readonly StageCard[] _stages = new StageCard[4];
    private readonly Grid _preview = new() { ClipToBounds = true };
    private readonly ClassifyThumb _previewThumb = new(420) { CornerRadius = new(10) };
    private readonly Image _livePreview = new() { Stretch = Stretch.UniformToFill };
    private readonly Canvas _tagLayer = new() { IsHitTestVisible = false };
    private readonly TextBlock _fileTitle = T("", 12, brush: Muted, raw: true), _activityTitle = T("", 11.5, brush: Muted, raw: true);
    private readonly Grid _fileSteps = new() { ColumnSpacing = 8 };
    private readonly TextBlock _lookTitle = T("", 12, brush: Muted, raw: true), _framesTitle = T("", 11.5, brush: Muted, raw: true),
        _consistency = T("", 11.5, brush: Muted, raw: true), _colorsTitle = T("", 11.5, brush: Muted, raw: true);
    private readonly StackPanel _frames = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly Grid _colorBar = new() { Height = 12, ClipToBounds = true };
    private readonly Border _colors = new() { CornerRadius = new(4), ClipToBounds = true, BorderBrush = Hairline, BorderThickness = new(1) };
    private readonly WrapPanel _verdicts = new();
    private readonly TextBlock _reason = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Ink };
    private readonly Border _reasonCard = new() { Background = LightBlue, CornerRadius = new(8), Padding = new(10, 8) };
    private readonly StackPanel _lookBody = new() { Spacing = 8 };
    private readonly TextBlock _lookEmpty = T("等待文件分析", 12, brush: Muted);
    private readonly FileFlow _wall = new();
    private readonly TextBlock _groupsTitle = T("", 11.5, brush: Muted, raw: true), _groupsEmpty = T("尚未归组", 12, brush: Muted);
    private readonly ListBox _groups = new() { Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0) };
    private readonly Dictionary<int, FileStep> _stepControls = [];
    private readonly List<Border> _tagChips = [];
    private FolderFileVisual? _renderedVisual;
    private string? _framesKey;
    private FolderGroupView[]? _renderedGroups;
    private Control? _runView, _resultView;

    private Control BuildRun()
    {
        var grid = new Grid { RowDefinitions = new("Auto,*"), Margin = new(18, 0, 18, 16), RowSpacing = 14 };
        var stageRow = new Grid { ColumnDefinitions = new("*,16,*,16,*,16,*") };
        for (var i = 0; i < 4; i++)
        {
            _stages[i] = new StageCard(); Grid.SetColumn(_stages[i], i * 2); stageRow.Children.Add(_stages[i]);
            if (i == 3) continue;
            var chevron = new LucideIcon { Kind = "chevron-right", Width = 12, Height = 12, Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chevron, i * 2 + 1); stageRow.Children.Add(chevron);
        }
        grid.Children.Add(stageRow);
        var main = new Grid { ColumnDefinitions = new("*,250"), ColumnSpacing = 14 };
        var left = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 };
        var current = new StackPanel { Spacing = 10 };
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        heading.Children.Add(T("第 2 步 · 当前文件", 13, FontWeight.Bold));
        Grid.SetColumn(_fileTitle, 1); heading.Children.Add(_fileTitle); current.Children.Add(heading);
        current.Children.Add(_fileSteps); var currentCard = Card(current); currentCard.Padding = new(14, 12); left.Children.Add(currentCard);
        var look = BuildLook(); Grid.SetRow(look, 1); left.Children.Add(look); main.Children.Add(left);
        var groupsPanel = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8 };
        groupsPanel.Children.Add(T("第 3 步 · 分组正在形成", 13, FontWeight.Bold));
        _groups.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _groups.ItemTemplate = new FuncDataTemplate<FolderGroupView>((_, _) => new GroupRow(), supportsRecycling: true);
        _groups.ContainerPrepared += (_, args) =>
        {
            if (args.Container is ListBoxItem item) { item.Padding = new(0); item.MinHeight = 0; item.HorizontalContentAlignment = HorizontalAlignment.Stretch; }
        };
        var groupBody = new Panel(); groupBody.Children.Add(_groups); groupBody.Children.Add(_groupsEmpty);
        _groupsEmpty.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(groupBody, 1); groupsPanel.Children.Add(groupBody);
        var groupsCard = Card(groupsPanel); groupsCard.Padding = new(14, 12); Grid.SetColumn(groupsCard, 1); main.Children.Add(groupsCard);
        Grid.SetRow(main, 1); grid.Children.Add(main);
        return grid;
    }

    private Control BuildLook()
    {
        var grid = new Grid { ColumnDefinitions = new("2*,3.5*"), ColumnSpacing = 14 };
        _preview.Children.Add(_previewThumb); _preview.Children.Add(_livePreview); _preview.Children.Add(_tagLayer);
        _preview.SizeChanged += (_, _) => PositionTags();
        grid.Children.Add(new Border { CornerRadius = new(10), ClipToBounds = true, Child = _preview });
        var title = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        title.Children.Add(T("AI 正在看", 13, FontWeight.Bold)); Grid.SetColumn(_lookTitle, 1); title.Children.Add(_lookTitle);
        _lookBody.Children.Add(title);
        _lookBody.Children.Add(new ScrollViewer { Content = _frames, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        _lookBody.Children.Add(_consistency); _lookBody.Children.Add(_colorsTitle);
        _colors.Child = _colorBar; _lookBody.Children.Add(_colors);
        _lookBody.Children.Add(_verdicts); _reasonCard.Child = _reason; _lookBody.Children.Add(_reasonCard);
        var detail = new Grid { RowDefinitions = new("Auto,Auto,Auto,*"), RowSpacing = 12 };
        var summary = new Panel(); summary.Children.Add(_lookBody); summary.Children.Add(_lookEmpty); detail.Children.Add(summary);
        var flowTitle = T("文件流 · 按处理顺序", 12, brush: Muted); Grid.SetRow(flowTitle, 1); detail.Children.Add(flowTitle);
        Grid.SetRow(_wall, 2); detail.Children.Add(_wall);
        Grid.SetColumn(detail, 1); grid.Children.Add(detail);
        var card = Card(grid); card.Padding = new(14, 12); return card;
    }

    private void RenderRun()
    {
        if (_runProjection is not { } run) return;
        _percent.Text = $"{_progress.Percent:0}%";
        for (var i = 0; i < _stages.Length; i++)
        {
            var node = run.Stages[i];
            var count = i switch
            {
                0 => node.ElapsedSeconds is { } seconds ? Clock(TimeSpan.FromSeconds(seconds)) : "",
                1 => $"{run.Done} / {run.Total}",
                2 => run.Outfit && _progress.Regroup > 0 ? string.Format(L("第 {0} 次"), _progress.Regroup) : "",
                _ => ""
            };
            _stages[i].Update(node, count, _job.State, i == 2 && _progress.Regroup > 0);
        }
        _fileTitle.Text = run.Current is { } file ? file.Name + " · " + string.Format(L("第 {0} / {1} 个"), run.Ordinal, run.Total) : L("等待文件分析");
        ToolTip.SetTip(_fileTitle, run.Current?.Path);
        _activityTitle.Text = L(_job.State == JobState.Paused ? "已暂停" : _job.State == JobState.Stopping ? "正在停止" : run.Activity?.Stage ?? "等待开始");
        if (run.Activity?.Backend is { Length: > 0 } backend) _activityTitle.Text += " · " + backend;
        ToolTip.SetTip(_fileTitle, run.Current?.Path + "\n" + _activityTitle.Text);
        foreach (var item in _stepControls.Values) item.IsVisible = false;
        foreach (var step in run.Steps)
        {
            if (!_stepControls.TryGetValue(step.Index, out var control))
            {
                control = new FileStep(); _stepControls.Add(step.Index, control);
                Grid.SetColumn(control, step.Index); _fileSteps.Children.Add(control);
            }
            control.IsVisible = true; control.Update(step, _job.State);
        }
        _fileSteps.ColumnDefinitions.Clear();
        foreach (var step in run.Steps) _fileSteps.ColumnDefinitions.Add(new(1, GridUnitType.Star));
        RenderLook(run);
        _wall.Update(run.Files);
        _groupsTitle.Text = run.Outfit ? (_progress.Regroup > 0 ? string.Format(L("第 {0} 次"), _progress.Regroup) : L("尚未归组")) : L("规则分类结果");
        _groupsEmpty.IsVisible = run.Groups.Length == 0;
        _groupsEmpty.Text = L(run.Outfit ? "等待服装区域识别" : "等待分类结果");
        if (!ReferenceEquals(_renderedGroups, run.Groups)) { _renderedGroups = run.Groups; _groups.ItemsSource = run.Groups; }
    }

    private void RenderLook(RunProjection run)
    {
        var visual = run.Current;
        _lookBody.IsVisible = visual is not null; _lookEmpty.IsVisible = visual is null;
        _previewThumb.IsVisible = visual is not null && run.Preview is null;
        _livePreview.Source = run.Preview; _livePreview.IsVisible = run.Preview is not null;
        if (visual is null)
        { _tagLayer.Children.Clear(); _tagChips.Clear(); _renderedVisual = null; _framesKey = null; return; }
        var activeStage = _job.State == JobState.Paused ? "已暂停" : run.Activity?.Stage ?? "";
        _lookTitle.Text = L(activeStage);
        if (run.Frame is > 0 and var frame && run.FrameTotal is { } total)
            _lookTitle.Text += " · " + string.Format(L("第 {0} / {1} 帧"), frame, total);
        var selected = run.SelectedFrame;
        _previewThumb.SetSource(visual.Path, visual.IsVideo ? visual.Frames.ElementAtOrDefault(Math.Max(0, selected))?.Seconds ?? 0 : null);
        // Array references change when real evidence changes, including scores with unchanged tag counts.
        if (_renderedVisual is null || visual.Path != _renderedVisual.Path || !ReferenceEquals(visual.Tags, _renderedVisual.Tags) ||
            visual.Scene != _renderedVisual.Scene || visual.SceneScore != _renderedVisual.SceneScore)
        {
            _tagLayer.Children.Clear(); _tagChips.Clear();
            foreach (var tag in visual.Tags.OrderBy(tag => tag.Tag == "shirt" ? 1 : tag.Tag == "jeans" ? 2 : 0).Take(4))
            {
                var chip = Chip(WordLibraryCatalog.TagLabel(tag.Tag), tag.Score.ToString("0.00"), Surface);
                chip.BoxShadow = BoxShadows.Parse("0 1 3 0 #14000000"); _tagLayer.Children.Add(chip); _tagChips.Add(chip);
            }
            if (visual.Scene is { } scene)
            {
                var chip = Chip(L("场景") + " · " + L(scene), visual.SceneScore?.ToString("0.00"), Surface);
                chip.Background = Dark; chip.Padding = new(9, 3);
                if (chip.Child is TextBlock label)
                {
                    label.FontSize = 11.5;
                    foreach (var inline in label.Inlines!.OfType<Avalonia.Controls.Documents.Run>())
                        inline.Foreground = inline.FontWeight == FontWeight.SemiBold ? BlueSoft : Brushes.White;
                }
                Canvas.SetLeft(chip, 8); Canvas.SetBottom(chip, 8); _tagLayer.Children.Add(chip);
            }
            PositionTags();
        }
        var framesKey = visual.Path + "|" + selected + "|" + run.FrameOwnership + "|" + string.Join(",", visual.Frames.Select(f => $"{f.Seconds:R}:{f.Consistent}"));
        if (_framesKey != framesKey)
        {
            _framesKey = framesKey; _frames.Children.Clear();
            // Only real sampled timestamps; an image itself is the single actual frame.
            var frames = visual.IsVideo ? visual.Frames : [new FolderFrameVisual(0, visual.GroupId, true)];
            _framesTitle.Text = visual.IsVideo ? frames.Length > 0 ? string.Format(L("采样画面 · {0} 帧"), frames.Length) : L("等待采样画面") : L("图片");
            for (var i = 0; i < frames.Length; i++)
            {
                var thumb = new ClassifyThumb(72) { Width = 54, Height = 38, BorderThickness = new(i == selected ? 2 : 0), BorderBrush = Blue };
                thumb.SetSource(visual.Path, visual.IsVideo ? frames[i].Seconds : null);
                ToolTip.SetTip(thumb, _framesTitle.Text + " · " + (visual.IsVideo ? $"{frames[i].Seconds:0.00}s" : visual.Name));
                _frames.Children.Add(thumb);
            }
        }
        _consistency.IsVisible = visual.IsVideo && run.FrameOwnership && activeStage != "识别服装区域";
        _consistency.Text = $"{L("每帧归属一致率")} {visual.FrameConsistency:P0}";
        if (_renderedVisual is null || !ReferenceEquals(visual.OutfitColors, _renderedVisual.OutfitColors))
        {
            _colorBar.Children.Clear(); _colorBar.ColumnDefinitions.Clear();
            foreach (var color in visual.OutfitColors.Where(color => color.Share > .005).OrderByDescending(color => color.Share))
            {
                var block = new Border { Background = Brush(color.Hex) };
                ToolTip.SetTip(block, $"{color.Hex} · {color.Share:P0}");
                Grid.SetColumn(block, _colorBar.ColumnDefinitions.Count);
                _colorBar.ColumnDefinitions.Add(new(color.Share, GridUnitType.Star)); _colorBar.Children.Add(block);
            }
        }
        _colorsTitle.IsVisible = _colors.IsVisible = run.Outfit && visual.OutfitColors.Length > 0;
        _colorsTitle.Text = $"{L("服装主色")} · {L("人物占画面")} {visual.PersonRatio:P0}";
        _verdicts.IsVisible = !run.Outfit;
        if (_renderedVisual is null || !ReferenceEquals(visual.Verdicts, _renderedVisual.Verdicts))
        {
            _verdicts.Children.Clear();
            foreach (var verdict in visual.Verdicts)
            { var chip = Chip(verdict.RuleName, verdict.Value, rawValue: false); chip.Margin = new(0, 0, 6, 4); _verdicts.Children.Add(chip); }
        }
        _reasonCard.IsVisible = visual.Verdicts.Length > 0 || visual.Error is not null;
        _reason.Inlines!.Clear();
        _reason.Inlines.Add(new Avalonia.Controls.Documents.Run(L("归组理由")) { FontWeight = FontWeight.Bold });
        _reason.Inlines.Add(new Avalonia.Controls.Documents.Run(" · " + L(visual.Reason)));
        _renderedVisual = visual;
    }

    private void PositionTags()
    {
        Point[] positions = [new(.46, .10), new(.08, .20), new(.52, .64), new(.08, .45)];
        for (var i = 0; i < _tagChips.Count; i++)
        {
            Canvas.SetLeft(_tagChips[i], Math.Clamp(positions[i].X * _preview.Bounds.Width, 8,
                Math.Max(8, _preview.Bounds.Width - _tagChips[i].DesiredSize.Width - 8)));
            Canvas.SetTop(_tagChips[i], positions[i].Y * Math.Max(0, _preview.Bounds.Height - 30));
        }
    }

    private sealed class StageCard : Border
    {
        private readonly TextBlock _number = T("", 12, FontWeight.Bold, Brushes.White, raw: true);
        private readonly Border _badge = new() { Width = 18, Height = 18, CornerRadius = new(9) };
        private readonly TextBlock _name = T("", 12.5, FontWeight.Bold), _detail = T("", 11.5, brush: Muted, raw: true), _count = T("", 10.5, brush: Muted, raw: true);
        public StageCard()
        {
            Background = Surface; CornerRadius = new(12); Padding = new(12, 10); BorderThickness = new(1); MinHeight = 75;
            _number.HorizontalAlignment = HorizontalAlignment.Center; _badge.Child = _number;
            var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 7, RowSpacing = 6 };
            grid.Children.Add(_badge); Grid.SetColumn(_name, 1); grid.Children.Add(_name); Grid.SetColumn(_count, 2); grid.Children.Add(_count);
            _detail.TextWrapping = TextWrapping.Wrap; _detail.TextTrimming = TextTrimming.None;
            Grid.SetRow(_detail, 1); Grid.SetColumnSpan(_detail, 3); grid.Children.Add(_detail); Child = grid;
        }
        public void Update(AiStageNodeDto node, string count, JobState state, bool started)
        {
            _number.Text = (node.Index + 1).ToString(); _name.Text = L(node.Title); _count.Text = count;
            _detail.Text = node.SubSteps.Length > 0 ? string.Join(" · ", node.SubSteps.Select(L)) : L(node.Detail);
            _badge.Background = node.State == "done" ? Green : node.State == "running" ? state == JobState.Paused ? Orange : Blue : started ? Blue : Muted;
            BorderBrush = node.State == "running" ? Blue : Hairline;
            ToolTip.SetTip(this, _detail.Text);
        }
    }

    private sealed class FileStep : Grid
    {
        private readonly Border _pending = new() { Width = 15, Height = 15, CornerRadius = new(8), BorderBrush = Hairline, BorderThickness = new(1.5), VerticalAlignment = VerticalAlignment.Center };
        private readonly LucideIcon _icon = new() { Width = 17, Height = 17, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _name = T("", 12.5), _detail = T("", 11, brush: Muted, raw: true);
        public FileStep()
        {
            ColumnDefinitions = new("Auto,*"); ColumnSpacing = 6;
            _name.LineHeight = 18; _detail.LineHeight = 14;
            Children.Add(_icon); Children.Add(_pending); var text = new StackPanel(); text.Children.Add(_name); text.Children.Add(_detail);
            Grid.SetColumn(text, 1); Children.Add(text);
        }
        public void Update(AiStageNodeDto step, JobState state)
        {
            _name.Text = L(step.Title);
            _detail.Text = step.Title == "规则判定" ? string.Format(L("{0} 组规则"), step.Detail) : step.Title == "读取画面" && step.Detail.Length > 0
                ? string.Format(L("采样 {0} 帧"), step.Detail) : L(step.Detail);
            _detail.IsVisible = _detail.Text.Length > 0;
            var paused = state == JobState.Paused && step.State == "running";
            var stopping = state == JobState.Stopping && step.State == "running";
            _pending.IsVisible = step.State == "pending"; _icon.IsVisible = !_pending.IsVisible;
            _icon.RenderTransform = step.State == "running" && !paused && !stopping ? new RotateTransform((Environment.TickCount64 / 250 % 8) * 45) : null;
            _icon.Kind = paused ? "pause" : stopping ? "square" : step.State switch { "done" => "circle-check", "running" => "loader-circle", "failed" => "circle-alert", _ => "clock" };
            _icon.Foreground = paused ? Orange : stopping ? Muted : step.State switch { "done" => Green, "running" => Blue, "failed" => Red, _ => Muted };
            ToolTip.SetTip(this, _detail.Text);
        }
    }

    private sealed class GroupRow : Border
    {
        private readonly Mosaic _mosaic = new(52, 52, []);
        private readonly Border _badge = Badge("", Blue);
        private readonly TextBlock _name = T("", 13, FontWeight.SemiBold, raw: true), _shoot = T("", 11.5, brush: Muted, raw: true), _count = T("", 17, FontWeight.Bold, raw: true);
        public GroupRow()
        {
            Padding = new(0, 6); BorderBrush = Hairline; BorderThickness = new(0, 0, 0, 1);
            var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
            var cover = new Panel(); cover.Children.Add(_mosaic); cover.Children.Add(_badge);
            _badge.HorizontalAlignment = HorizontalAlignment.Right; _badge.VerticalAlignment = VerticalAlignment.Top; _badge.Margin = new(0, -5, -6, 0); row.Children.Add(cover);
            _badge.BorderBrush = Surface; _badge.BorderThickness = new(2);
            var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center }; text.Children.Add(_name); text.Children.Add(_shoot);
            var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 }; counts.Children.Add(_count); counts.Children.Add(T("个文件", 11.5, brush: Muted)); text.Children.Add(counts);
            Grid.SetColumn(text, 1); row.Children.Add(text); Child = row;
        }
        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is not FolderGroupView group) return;
            _mosaic.SetPaths(group.CoverPaths); _name.Text = group.Name;
            _shoot.Text = string.Join(" · ", new[] { group.Similarity is { } similarity ? $"{L("相似度")} {similarity:0.00}" : group.ShootName, L(group.Scene ?? "") }
                .Where(value => !string.IsNullOrEmpty(value)));
            _shoot.IsVisible = _shoot.Text.Length > 0; ToolTip.SetTip(_shoot, group.ShootName + " · " + _shoot.Text);
            _count.Text = group.Count.ToString(); _badge.IsVisible = group.Added > 0;
            if (_badge.Child is TextBlock text) text.Text = "+" + group.Added;
            ToolTip.SetTip(_name, group.Name);
        }
    }
}
