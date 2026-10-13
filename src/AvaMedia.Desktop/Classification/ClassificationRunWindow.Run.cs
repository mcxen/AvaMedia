using AvaMedia.Controls;
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
    private readonly StageIndicator[] _stages = new StageIndicator[4];
    private readonly Grid _preview = new() { ClipToBounds = true };
    private readonly ClassifyThumb _previewThumb = new(420) { [!Border.CornerRadiusProperty] = Resource("AcPreviewRadius") };
    private readonly Image _livePreview = new() { Stretch = Stretch.UniformToFill };
    private readonly Canvas _tagLayer = new() { IsHitTestVisible = false };
    private readonly TextBlock _fileTitle = T("", 12, brush: Muted, raw: true), _activityTitle = T("", 12, brush: Muted, raw: true);
    private readonly Grid _fileSteps = new() { ColumnSpacing = 8 };
    private readonly TextBlock _lookTitle = T("", 12, brush: Muted, raw: true), _framesTitle = T("", 12, brush: Muted, raw: true),
        _consistency = T("", 12, brush: Muted, raw: true), _colorsTitle = T("", 12, brush: Muted, raw: true);
    private readonly Grid _frames = new() { ColumnSpacing = 4 };
    private readonly Grid _colorBar = new() { [!HeightProperty] = Resource("AcColorBarHeight"), ClipToBounds = true };
    private readonly Border _colors = new() { [!Border.CornerRadiusProperty] = Resource("AcColorBarRadius"), ClipToBounds = true };
    private readonly WrapPanel _verdicts = new();
    private readonly TextBlock _reason = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, [!TextBlock.ForegroundProperty] = Resource(Ink) };
    private readonly Border _reasonCard = new() { [!Border.BackgroundProperty] = Resource("UiAccentSoft"), [!Border.CornerRadiusProperty] = Resource("AcFieldRadius"), Padding = new(10, 8) };
    private readonly StackPanel _lookBody = new() { Spacing = 8 };
    private readonly TextBlock _lookEmpty = T("等待文件分析", 12, brush: Muted);
    private readonly FileFlow _wall = new();
    private readonly TextBlock _groupsTitle = T("", 12, brush: Muted, raw: true);
    private readonly EmptyState _groupsEmpty = new() { Text = L("等待分类结果") };
    private readonly ListBox _groups = new() { Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0) };
    private readonly Dictionary<int, SubStepRow> _stepControls = [];
    private readonly List<Border> _tagChips = [];
    private FolderFileVisual? _renderedVisual;
    private string? _framesKey;
    private FolderGroupView[]? _renderedGroups;
    private Control? _runView, _resultView;

    private Control BuildRun()
    {
        var grid = new Grid { RowDefinitions = new("Auto,*"), Margin = new(18, 0, 18, 16), RowSpacing = 12 };
        var stageRow = new Grid { ColumnDefinitions = new("*,24,*,24,*,24,*") };
        for (var i = 0; i < 4; i++)
        {
            _stages[i] = new StageIndicator { ShowIdleBorder = false }; Grid.SetColumn(_stages[i], i * 2); stageRow.Children.Add(_stages[i]);
            if (i == 3) continue;
            var chevron = new LucideIcon { Kind = "chevron-right", Width = 12, Height = 12, [!LucideIcon.ForegroundProperty] = Resource(Muted),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chevron, i * 2 + 1); stageRow.Children.Add(chevron);
        }
        grid.Children.Add(stageRow);
        var main = new Grid { ColumnDefinitions = new("*,250"), ColumnSpacing = 12 };
        var left = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 };
        var current = new StackPanel { Spacing = 10 };
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        heading.Children.Add(T("第 2 步 · 当前文件", 13, FontWeight.Bold));
        Grid.SetColumn(_fileTitle, 1); heading.Children.Add(_fileTitle); current.Children.Add(heading);
        current.Children.Add(_fileSteps); var currentCard = Card(current);  left.Children.Add(currentCard);
        var look = BuildLook(); Grid.SetRow(look, 1); left.Children.Add(look); main.Children.Add(left);
        var groupsPanel = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8 };
        groupsPanel.Children.Add(T("第 3 步 · 分组正在形成", 13, FontWeight.Bold));
        ScrollViewer.SetHorizontalScrollBarVisibility(_groups, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _groups.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        _groups.ItemTemplate = new FuncDataTemplate<FolderGroupView>((_, _) => new GroupRow(), supportsRecycling: true);
        _groups.ContainerPrepared += (_, args) =>
        {
            if (args.Container is ListBoxItem item) { item.Padding = new(0); item.MinHeight = 0; item.HorizontalContentAlignment = HorizontalAlignment.Stretch; }
        };
        var groupBody = new Panel(); groupBody.Children.Add(_groups); groupBody.Children.Add(_groupsEmpty);
        _groupsEmpty.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(groupBody, 1); groupsPanel.Children.Add(groupBody);
        var groupsCard = Card(groupsPanel);  Grid.SetColumn(groupsCard, 1); main.Children.Add(groupsCard);
        Grid.SetRow(main, 1); grid.Children.Add(main);
        return grid;
    }

    private Control BuildLook()
    {
        var grid = new Grid { ColumnDefinitions = new("2*,3.5*"), ColumnSpacing = 12 };
        _preview.Children.Add(_previewThumb); _preview.Children.Add(_livePreview); _preview.Children.Add(_tagLayer);
        _preview.SizeChanged += (_, _) => PositionTags();
        _preview.Bind(BackgroundProperty, Resource("AcPreviewFill"));
        _previewThumb.Bind(Border.BackgroundProperty, Resource("AcPreviewFill"));
        _previewThumb.Bind(Border.CornerRadiusProperty, Resource("AcPreviewRadius"));
        grid.Children.Add(new MediaPreviewSurface { UseAmbientLighting = false, FitContent = false, PreviewContent = _preview });
        var title = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        title.Children.Add(T("AI 正在看", 13, FontWeight.Bold)); Grid.SetColumn(_lookTitle, 1); title.Children.Add(_lookTitle);
        _lookBody.Children.Add(title);
        _lookBody.Children.Add(_frames);
        _lookBody.Children.Add(_consistency);
        _colors.Child = _colorBar; _lookBody.Children.Add(_colors); _lookBody.Children.Add(_colorsTitle);
        _lookBody.Children.Add(_verdicts); _reasonCard.Child = _reason; _lookBody.Children.Add(_reasonCard);
        var detail = new Grid { RowDefinitions = new("Auto,Auto,Auto,*"), RowSpacing = 12 };
        var summary = new Panel(); summary.Children.Add(_lookBody); summary.Children.Add(_lookEmpty); detail.Children.Add(summary);
        var flowTitle = T("文件流 · 按处理顺序", 12, brush: Muted); Grid.SetRow(flowTitle, 1); detail.Children.Add(flowTitle);
        Grid.SetRow(_wall, 2); detail.Children.Add(_wall);
        Grid.SetColumn(detail, 1); grid.Children.Add(detail);
        var card = Card(grid);  return card;
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
            _stages[i].Update(i, L(node.Title), node.SubSteps.Length > 0 ? string.Join(" · ", node.SubSteps.Select(L)) : L(node.Detail), count, node.State == "running" && _job.State == JobState.Paused ? "paused" : node.State);
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
                control = new SubStepRow(); _stepControls.Add(step.Index, control);
                Grid.SetColumn(control, step.Index); _fileSteps.Children.Add(control);
            }
            control.IsVisible = true; var detail = step.Title == "规则判定" ? string.Format(L("{0} 组规则"), step.Detail) : step.Title == "读取画面" && step.Detail.Length > 0 && step.State != "failed" ? string.Format(L("采样 {0} 帧"), step.Detail) : L(step.Detail);
            control.Update(L(step.Title), detail, step.State == "running" && _job.State == JobState.Paused ? "paused" : step.State);
        }
        _fileSteps.ColumnDefinitions.Clear();
        foreach (var step in run.Steps) _fileSteps.ColumnDefinitions.Add(new(step.Title == "服装区域" ? 1.6 : step.Title == "保存结果" ? .8 : 1, GridUnitType.Star));
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
                var chip = Chip(L(tag.Tag == "white" ? "白色上衣" : WordLibraryCatalog.TagLabel(tag.Tag)), tag.Score.ToString("0.00"), Surface);
                chip.Bind(Border.BoxShadowProperty, Resource("AcChipShadow")); _tagLayer.Children.Add(chip); _tagChips.Add(chip);
            }
            if (visual.Scene is { } scene)
            {
                var chip = Chip(L("场景") + " · " + L(scene), visual.SceneScore?.ToString("0.00"), Surface);
                chip.Bind(Border.BackgroundProperty, Resource(Dark)); chip.Bind(Border.PaddingProperty, Resource("AcChipPadding"));
                if (chip.Child is TextBlock label)
                {
                    label.Bind(TextBlock.FontSizeProperty, Resource("UiFontCaption"));
                    foreach (var inline in label.Inlines!.OfType<Avalonia.Controls.Documents.Run>())
                        inline.Bind(Avalonia.Controls.Documents.Run.ForegroundProperty, Resource(White));
                }
                Canvas.SetLeft(chip, 8); Canvas.SetBottom(chip, 8); _tagLayer.Children.Add(chip);
            }
            PositionTags();
        }
        var framesKey = visual.Path + "|" + selected + "|" + run.FrameOwnership + "|" + string.Join(",", visual.Frames.Select(f => $"{f.Seconds:R}:{f.Consistent}"));
        if (_framesKey != framesKey)
        {
            _framesKey = framesKey; _frames.Children.Clear(); _frames.ColumnDefinitions.Clear();
            // Only real sampled timestamps; an image itself is the single actual frame.
            var frames = visual.IsVideo ? visual.Frames : [new FolderFrameVisual(0, visual.GroupId, true)];
            _framesTitle.Text = visual.IsVideo ? frames.Length > 0 ? string.Format(L("采样画面 · {0} 帧"), frames.Length) : L("等待采样画面") : L("图片");
            for (var i = 0; i < frames.Length; i++)
            {
                var thumb = new ClassifyThumb(72) { [!HeightProperty] = Resource("AcFrameHeight"), [!Border.CornerRadiusProperty] = Resource("AcThumbRadius"), BorderThickness = new(i == selected ? 2 : 0), [!Border.BorderBrushProperty] = Resource(Blue) };
                thumb.SetSource(visual.Path, visual.IsVideo ? frames[i].Seconds : null);
                ToolTip.SetTip(thumb, _framesTitle.Text + " · " + (visual.IsVideo ? $"{frames[i].Seconds:0.00}s" : visual.Name));
                Grid.SetColumn(thumb, i); _frames.ColumnDefinitions.Add(new(1, GridUnitType.Star)); _frames.Children.Add(thumb);
            }
        }
        _consistency.IsVisible = visual.IsVideo && run.FrameOwnership && activeStage != "识别服装区域";
        _consistency.Text = $"{L("每帧归属一致率")} {visual.FrameConsistency:P0}";
        if (_renderedVisual is null || !ReferenceEquals(visual.OutfitColors, _renderedVisual.OutfitColors))
        {
            _colorBar.Children.Clear(); _colorBar.ColumnDefinitions.Clear();
            foreach (var color in visual.OutfitColors.Where(color => color.Share > .005).OrderByDescending(color => color.Share))
            {
                var block = new Border { Background = Avalonia.Media.Brush.Parse(color.Hex) };
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
        _reason.Inlines.Add(new Avalonia.Controls.Documents.Run(L("归组理由")) { FontWeight = FontWeight.SemiBold, [!Avalonia.Controls.Documents.Run.ForegroundProperty] = Resource(Blue) });
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

    private sealed class GroupRow : ListRow
    {
        private readonly Mosaic _mosaic = new(40, 40, [], stacked: true);
        private readonly Border _badge = Badge("", Blue);
        private readonly TextBlock _name = T("", 13, FontWeight.SemiBold, raw: true), _shoot = T("", 12, brush: Muted, raw: true), _count = T("", 20, FontWeight.Bold, raw: true);
        public GroupRow()
        {
            var row = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8 };
            _mosaic.Bind(WidthProperty, Resource("AcThumbnailSize")); _mosaic.Bind(HeightProperty, Resource("AcThumbnailSize"));
            _name.FontSize = 13; _name.TextWrapping = TextWrapping.Wrap; _name.MaxLines = 2;
            _count.Bind(TextBlock.FontSizeProperty, Resource("AcCountFont")); _count.Bind(TextBlock.FontFamilyProperty, Resource("UiNumericFont"));
            var cover = new Panel(); cover.Children.Add(_mosaic); cover.Children.Add(_badge);
            _badge.HorizontalAlignment = HorizontalAlignment.Right; _badge.VerticalAlignment = VerticalAlignment.Top; _badge.Margin = new(0, -5, -6, 0); row.Children.Add(cover);
            _badge.Bind(Border.BorderBrushProperty, Resource(Surface)); _badge.BorderThickness = new(2);
            var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center }; text.Children.Add(_name); text.Children.Add(_shoot);
            var counts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            _count.HorizontalAlignment = HorizontalAlignment.Right; counts.Children.Add(_count); counts.Children.Add(T("个文件", 11, brush: Muted));
            Grid.SetColumn(counts, 2); row.Children.Add(counts);
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
