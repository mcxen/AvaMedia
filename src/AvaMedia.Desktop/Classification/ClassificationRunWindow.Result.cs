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
    private string? _resultRule, _resultKey;
    private readonly StackPanel _before = new() { Spacing = 10 }, _after = new() { Spacing = 14 }, _pendingList = new();
    private readonly TextBlock _afterTitle = T("", 11.5, brush: Muted, raw: true), _pendingTitle = T("", 11.5, brush: Muted, raw: true),
        _destination = T("", 11.5, brush: Muted, raw: true);
    private readonly Grid _histogram = new() { Height = 70 };
    private readonly Grid _histAxis = new() { ColumnDefinitions = new("*,*,*") };
    private readonly TextBlock _histCounts = new() { FontSize = 11.5 };
    private bool _moveMode;
    private Button? _copyMove;

    private Control BuildResult()
    {
        _resultRule ??= (_job.Options.FolderClassification?.Rules.FirstOrDefault(r => r.ByOutfit) ?? _job.Options.FolderClassification?.Rules.FirstOrDefault())?.Id;
        _moveMode = _job.Options.FolderClassification?.Move == true;
        var grid = new Grid { RowDefinitions = new("*,Auto"), ColumnDefinitions = new("200,*,290"), ColumnSpacing = 12, RowSpacing = 12, Margin = new(22, 0, 22, 14) };
        var beforeHead = new StackPanel { Spacing = 2, Margin = new(0, 0, 0, 10) };
        grid.Children.Add(Card(new ScrollViewer { Content = _before }));
        var afterHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 0, 0, 10) };
        afterHead.Children.Add(new LucideIcon { Kind = "arrow-right", Width = 14, Height = 14, [!LucideIcon.ForegroundProperty] = Resource(Blue) });
        afterHead.Children.Add(T("整理后", 13, FontWeight.Bold)); afterHead.Children.Add(_afterTitle);
        var afterPanel = new DockPanel(); DockPanel.SetDock(afterHead, Dock.Top); afterPanel.Children.Add(afterHead);
        afterPanel.Children.Add(new ScrollViewer { Content = _after });
        var afterCard = Card(afterPanel); Grid.SetColumn(afterCard, 1); grid.Children.Add(afterCard);

        var right = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 };
        var hist = new StackPanel { Spacing = 6 };
        var histHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        histHead.Children.Add(T("归组把握", 13, FontWeight.Bold)); histHead.Children.Add(T("相似度分布", 11.5, brush: Muted));
        hist.Children.Add(histHead); hist.Children.Add(_histogram); hist.Children.Add(_histAxis); hist.Children.Add(_histCounts);
        right.Children.Add(Card(hist));
        var pendingHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 0, 0, 6) };
        pendingHead.Children.Add(T("待你确认", 13, FontWeight.Bold)); pendingHead.Children.Add(_pendingTitle);
        var pendingPanel = new DockPanel(); DockPanel.SetDock(pendingHead, Dock.Top); pendingPanel.Children.Add(pendingHead);
        pendingPanel.Children.Add(new ScrollViewer { Content = _pendingList });
        var pendingCard = Card(pendingPanel); Grid.SetRow(pendingCard, 1); right.Children.Add(pendingCard);
        Grid.SetColumn(right, 2); grid.Children.Add(right);

        // Bottom action bar: destination note left, organize actions right with one primary.
        var bar = new Grid { ColumnDefinitions = new("*,Auto") };
        var note = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        note.Children.Add(new LucideIcon { Kind = "folder-closed", Width = 13, Height = 13, [!LucideIcon.ForegroundProperty] = Resource(Muted) }); note.Children.Add(_destination);
        bar.Children.Add(note);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Pill("撤销上次整理", "undo-2", click: () => _openAction(_job, "undo")));
        actions.Children.Add(Pill("导出分类结果…", click: () => _openAction(_job, "export")));
        _copyMove = Pill(_moveMode ? "移动到分类目录" : "复制到分类目录", "chevron-down", iconAfter: true);
        _copyMove.Flyout = new MenuFlyout { Items = {
            new MenuItem { Header = L("复制到分类目录"), Command = new RelayCommand(() => SetMove(false)) },
            new MenuItem { Header = L("移动到分类目录"), Command = new RelayCommand(() => SetMove(true)) } } };
        actions.Children.Add(_copyMove);
        actions.Children.Add(Pill("预览分类目录", click: () => _openAction(_job, _moveMode ? "preview-move" : "preview")));
        actions.Children.Add(Pill("执行整理", kind: "primary", click: () => _openAction(_job, _moveMode ? "organize-move" : "organize")));
        Grid.SetColumn(actions, 1); bar.Children.Add(actions);
        Grid.SetRow(bar, 1); Grid.SetColumnSpan(bar, 3); grid.Children.Add(bar);
        return grid;
    }

    private void SetMove(bool move)
    {
        _moveMode = move; if (_copyMove is not null) SetPillText(_copyMove, move ? "移动到分类目录" : "复制到分类目录");
        _resultKey = null; RenderResult();
    }

    private void RenderResult()
    {
        var snapshot = _job.ClassificationSnapshot; var spec = _job.Options.FolderClassification;
        if (snapshot is null || spec is null) return;
        var key = $"{_resultRule}|{_moveMode}|{snapshot.GetHashCode()}";
        if (key == _resultKey) return; _resultKey = key;
        var output = spec.OutputFolder;
        _destination.Text = $"{L(_moveMode ? "将移动到" : "将复制到")} {output} · {L(_moveMode ? "源文件移走" : "源文件保留")}";
        var files = snapshot.Files;
        var summary = FolderClassificationInsights.BeforeAfter(files, SourceFolders());
        RenderBefore(summary);
        var rule = spec.Rules.FirstOrDefault(r => r.Id == _resultRule) ?? spec.Rules.FirstOrDefault();
        RenderAfter(files, rule);
        var histogram = _progress.Histogram;
        _histogram.Children.Clear(); _histogram.ColumnDefinitions.Clear();
        if (histogram is not null)
        {
            var max = Math.Max(1, histogram.Bins.DefaultIfEmpty(0).Max());
            for (var i = 0; i < histogram.Bins.Length; i++)
            {
                _histogram.ColumnDefinitions.Add(new(1, GridUnitType.Star));
                var low = histogram.Min + (histogram.Max - histogram.Min) * i / histogram.Bins.Length;
                var bar = new Border { [!Border.BackgroundProperty] = Resource(low < histogram.Threshold ? Orange : Blue), CornerRadius = new(2, 2, 0, 0), Margin = new(1.5, 0),
                    VerticalAlignment = VerticalAlignment.Bottom, Height = Math.Max(2, 68 * histogram.Bins[i] / (double)max) };
                ToolTip.SetTip(bar, $"{low:0.00} · {histogram.Bins[i]}"); Grid.SetColumn(bar, i); _histogram.Children.Add(bar);
            }
            _histAxis.Children.Clear();
            var labels = new[] { ($"{histogram.Min:0.00}", HorizontalAlignment.Left), ($"{histogram.Threshold:0.00} {L("阈值")}", HorizontalAlignment.Center), ($"{histogram.Max:0.00}", HorizontalAlignment.Right) };
            for (var i = 0; i < 3; i++) { var text = T(labels[i].Item1, 10.5, brush: Muted, raw: true); text.HorizontalAlignment = labels[i].Item2; Grid.SetColumn(text, i); _histAxis.Children.Add(text); }
            _histCounts.Inlines!.Clear();
            void Add(int value, string brush, string label)
            {
                _histCounts.Inlines!.Add(new Avalonia.Controls.Documents.Run(value + " ") { [!Avalonia.Controls.Documents.Run.ForegroundProperty] = Resource(brush), FontWeight = FontWeight.Bold });
                _histCounts.Inlines.Add(new Avalonia.Controls.Documents.Run(L(label) + "    ") { [!Avalonia.Controls.Documents.Run.ForegroundProperty] = Resource(Ink) });
            }
            Add(histogram.Grouped, GreenText, "已归组"); Add(histogram.Pending, Orange, "待确认"); Add(histogram.Failed, Ink, "失败");
        }
        RenderPending(files, spec);
    }

    private string[] SourceFolders()
    {
        var folders = _job.Inputs.Select(System.IO.Path.GetDirectoryName).OfType<string>().Distinct(BatchRename.PathComparer).OrderBy(p => p.Length).ToList();
        var roots = new List<string>();
        foreach (var folder in folders) if (!roots.Any(root => FolderClassification.IsWithin(folder, root))) roots.Add(folder);
        return roots.ToArray();
    }

    private void RenderBefore(FolderBeforeAfter summary)
    {
        _before.Children.Clear();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(T("整理前", 13, FontWeight.Bold)); head.Children.Add(T($"{summary.Sources.Length} {L("个源文件夹")}", 11.5, brush: Muted, raw: true));
        _before.Children.Add(head);
        _before.Children.Add(T($"{summary.Images + summary.Videos} {L("个文件混在一起")}", 11.5, brush: Muted, raw: true));
        var wrap = new WrapPanel { Margin = new(0, 6, 0, 6) };
        var samples = summary.Sources.SelectMany(s => s.SamplePaths).Take(50).ToArray();
        var random = new Random(7);
        foreach (var path in samples)
        {
            var thumb = new ClassifyThumb(40) { Width = 28, Height = 28, Margin = new(2), [!ClassifyThumb.BorderBrushProperty] = Resource(White), BorderThickness = new(1.5), CornerRadius = new(4),
                RenderTransform = new RotateTransform(random.Next(-8, 9)), [!ClassifyThumb.BoxShadowProperty] = Resource("UiThumbnailShadow") };
            thumb.SetSource(path, VideoFormats.IsVideo(path) ? 1 : null); wrap.Children.Add(thumb);
        }
        _before.Children.Add(wrap);
        foreach (var (label, value) in new[] { ("图片", summary.Images.ToString()), ("视频", summary.Videos.ToString()), ("总大小", Size(summary.Bytes)) })
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto") };
            row.Children.Add(T(label, 11.5)); var v = T(value, 11.5, raw: true); Grid.SetColumn(v, 1); row.Children.Add(v);
            _before.Children.Add(row);
        }
    }

    private void RenderAfter(FolderClassificationTaskFile[] files, FolderClassificationRule? rule)
    {
        _after.Children.Clear();
        if (rule is null) return;
        var decided = files.Where(f => f.Result is not null && f.Error is null)
            .Select(f => (File: f, Decision: f.Result!.Decisions.FirstOrDefault(d => d.RuleId == rule.Id))).Where(x => x.Decision is not null).ToArray();
        var groups = decided.Where(x => !x.Decision!.NeedsReview)
            .GroupBy(x => (x.Decision!.CategoryId, x.Decision.CategoryName, x.Decision.OutfitShootName))
            .Select(g => (g.Key, Items: g.OrderByDescending(x => x.Decision!.Scores.FirstOrDefault()?.Similarity ?? 0).ToArray())).ToArray();
        var shoots = groups.GroupBy(g => g.Key.OutfitShootName ?? "").OrderBy(s => s.Key).ToArray();
        var pending = decided.Where(x => x.Decision!.NeedsReview).ToArray();
        _afterTitle.Text = rule.ByOutfit
            ? $"{L("分类结果")} · {shoots.Length} {L("个拍摄组")} · {groups.Length} {L("个服装分组")} · {L("另有")} {L("待确认")} {pending.Length}"
            : $"{L("分类结果")} · {groups.Length} {L("个分类")} · {L("另有")} {L("待确认")} {pending.Length}";
        var columns = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 14 };
        var cols = new[] { new StackPanel { Spacing = 14 }, new StackPanel { Spacing = 14 } };
        columns.Children.Add(cols[0]); Grid.SetColumn(cols[1], 1); columns.Children.Add(cols[1]);
        var index = 0; var shootIndex = 0;
        foreach (var shoot in shoots)
        {
            var block = new StackPanel { Spacing = 8 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(new LucideIcon { Kind = "folder-closed", Width = 13, Height = 13, [!LucideIcon.ForegroundProperty] = Resource(Blue) });
            head.Children.Add(T(shoot.Key.Length > 0 ? shoot.Key : L(rule.Name), 12.5, FontWeight.Bold, raw: true));
            head.Children.Add(T(shoot.Sum(g => g.Items.Length).ToString(), 12, brush: Muted, raw: true));
            block.Children.Add(head);
            var row = new WrapPanel { Margin = new(14, 0, 0, 0) };
            foreach (var group in shoot.OrderByDescending(g => g.Items.Length))
            {
                index++;
                var card = new StackPanel { Width = 92, Spacing = 2, Margin = new(0, 0, 10, 6) };
                card.Children.Add(new Mosaic(88, 100, group.Items.Take(3).Select(x => x.File.Path).ToArray()));
                card.Children.Add(new TextBlock { Text = $"{index:00} {group.Key.CategoryName}", FontSize = 11.5, [!TextBlock.ForegroundProperty] = Resource(Ink), TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new(0, 4, 0, 0) });
                card.Children.Add(T($"{group.Items.Length} {L("个文件")}", 11, brush: Muted, raw: true));
                row.Children.Add(card);
            }
            block.Children.Add(row);
            cols[shootIndex++ % 2].Children.Add(block);
        }
        _after.Children.Add(columns);
        if (pending.Length > 0)
        {
            var banner = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10 };
            banner.Children.Add(new LucideIcon { Kind = "folder-closed", Width = 15, Height = 15, [!LucideIcon.ForegroundProperty] = Resource(Orange) });
            var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines!.Add(new Avalonia.Controls.Documents.Run(L("待确认")) { FontWeight = FontWeight.Bold, [!Avalonia.Controls.Documents.Run.ForegroundProperty] = Resource(Ink) });
            text.Inlines.Add(new Avalonia.Controls.Documents.Run($"  {pending.Length} {L("个文件")} · {L("确认前不会整理")}") { [!Avalonia.Controls.Documents.Run.ForegroundProperty] = Resource(Muted) });
            Grid.SetColumn(text, 1); banner.Children.Add(text);
            var mosaic = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var item in pending.Take(3)) { var t = new ClassifyThumb(40) { Width = 28, Height = 28 }; t.SetSource(item.File.Path, VideoFormats.IsVideo(item.File.Path) ? 1 : null); mosaic.Children.Add(t); }
            Grid.SetColumn(mosaic, 2); banner.Children.Add(mosaic);
            _after.Children.Add(new Border { [!Border.BackgroundProperty] = Resource(Banner), CornerRadius = new(10), Padding = new(12, 8), Child = banner });
        }
    }

    private void RenderPending(FolderClassificationTaskFile[] files, FolderClassificationTaskOptions spec)
    {
        _pendingList.Children.Clear();
        var pending = files.Where(f => f.Result is not null && f.Error is null && f.Result.Decisions.Any(d => d.NeedsReview)).ToArray();
        _pendingTitle.Text = $"{pending.Length} {L("个")} · {L("点选归属")}";
        if (pending.Length == 0) { _pendingList.Children.Add(T("没有需要确认的文件", 12, brush: Muted)); return; }
        foreach (var file in pending.Take(100))
        {
            var decision = file.Result!.Decisions.First(d => d.NeedsReview);
            var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, Margin = new(0, 6) };
            var thumb = new ClassifyThumb(60) { Width = 46, Height = 46 }; thumb.SetSource(file.Path, VideoFormats.IsVideo(file.Path) ? 1 : null);
            row.Children.Add(thumb);
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(T(System.IO.Path.GetFileName(file.Path), 12.5, FontWeight.SemiBold, raw: true));
            var reason = new TextBlock { Text = decision.Evidence, FontSize = 11, [!TextBlock.ForegroundProperty] = Resource(Muted), TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
            Localization.SetIsUserText(reason, true); text.Children.Add(reason);
            var choices = new WrapPanel { Margin = new(0, 2, 0, 0) };
            foreach (var score in decision.Scores.OrderByDescending(s => s.Similarity).Take(2))
            {
                var choice = Pill(score.Name, height: 22); choice.Padding = new(8, 0); choice.Margin = new(0, 0, 4, 4);
                var category = score.CategoryId;
                choice.Click += async (_, _) => await ResolveAsync(file.Path, decision.RuleId, category);
                ToolTip.SetTip(choice, $"{L("相似度")} {score.Similarity:0.00}"); choices.Children.Add(choice);
            }
            var keep = Pill("保持待确认", height: 22); keep.Padding = new(8, 0); keep.Margin = new(0, 0, 4, 4); keep.IsHitTestVisible = false; choices.Children.Add(keep);
            text.Children.Add(choices);
            Grid.SetColumn(text, 1); row.Children.Add(text);
            _pendingList.Children.Add(row); _pendingList.Children.Add(new Rectangle { Height = 1, [!Rectangle.FillProperty] = Resource(Hairline) });
        }
    }

    private async Task ResolveAsync(string path, string ruleId, string categoryId)
    {
        try
        {
            await FolderClassificationTaskStore.ResolveAsync(_job, path, ruleId, categoryId, CancellationToken.None);
            _resultKey = null; Invalidate();
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        { await Ui.Message(this, "归属失败", error.Message); }
    }

    private sealed class RelayCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
