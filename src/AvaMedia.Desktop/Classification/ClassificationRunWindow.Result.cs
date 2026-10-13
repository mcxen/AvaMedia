using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;
using static AvaMedia.Desktop.Classification.ClassifyUi;

namespace AvaMedia.Desktop.Classification;

public sealed partial class ClassificationRunWindow
{
    private string? _resultRule, _resultKey;
    private readonly StackPanel _before = new() { Spacing = 6 }, _after = new() { Spacing = 20 }, _pendingList = new();
    private readonly TextBlock _afterTitle = T("", 12, brush: Muted, raw: true), _pendingTitle = T("", 12, brush: Muted, raw: true),
        _destination = T("", 12, brush: Muted, raw: true);
    private readonly Grid _histogram = new() { Height = 80, Margin = new(0, 4, 0, 0) };
    private readonly Grid _histAxis = new() { ColumnDefinitions = new("*,*,*") };
    private readonly StackPanel _histCounts = new() { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new(0, 6, 0, 0) };
    private readonly Dictionary<(string Rule, string Category), int> _resultCategoryNumbers = [];
    private bool _moveMode;
    private Button? _copyMove;

    private Control BuildResult()
    {
        _resultRule ??= (_job.Options.FolderClassification?.Rules.FirstOrDefault(r => r.ByOutfit) ?? _job.Options.FolderClassification?.Rules.FirstOrDefault())?.Id;
        _moveMode = _job.Options.FolderClassification?.Move == true;
        var grid = new Grid { RowDefinitions = new("*,Auto"), ColumnDefinitions = new("1.05*,2.65*,1.25*"), ColumnSpacing = 14, RowSpacing = 12, Margin = new(22, 0, 22, 14) };
        grid.ColumnDefinitions[0].MinWidth = 236;
        grid.ColumnDefinitions[2].MinWidth = 300;
        ConfigureResultSegments(grid);
        grid.Children.Add(ResultCard(new ScrollViewer { Content = _before, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }));
        var afterHead = new Grid { ColumnDefinitions = new("Auto,Auto,*"), ColumnSpacing = 8, Margin = new(0, 0, 0, 10) };
        afterHead.Children.Add(new LucideIcon { Kind = "arrow-right", Width = 14, Height = 14, [!LucideIcon.ForegroundProperty] = Resource(Blue) });
        var afterLabel = T("整理后", 15, FontWeight.Bold); Grid.SetColumn(afterLabel, 1); afterHead.Children.Add(afterLabel);
        Grid.SetColumn(_afterTitle, 2); afterHead.Children.Add(_afterTitle);
        var afterPanel = new DockPanel(); DockPanel.SetDock(afterHead, Dock.Top); afterPanel.Children.Add(afterHead);
        afterPanel.Children.Add(new ScrollViewer { Content = _after, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var afterCard = ResultCard(afterPanel); Grid.SetColumn(afterCard, 1); grid.Children.Add(afterCard);

        var right = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 12 };
        var hist = new StackPanel { Spacing = 6 };
        var histHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        histHead.Children.Add(T("归组把握", 15, FontWeight.Bold)); histHead.Children.Add(T("相似度分布", 12, brush: Muted));
        hist.Children.Add(histHead); hist.Children.Add(_histogram); hist.Children.Add(_histAxis); hist.Children.Add(_histCounts);
        right.Children.Add(ResultCard(hist));
        var pendingHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 0, 0, 2) };
        pendingHead.Children.Add(T("待你确认", 15, FontWeight.Bold)); pendingHead.Children.Add(_pendingTitle);
        var pendingPanel = new DockPanel(); DockPanel.SetDock(pendingHead, Dock.Top); pendingPanel.Children.Add(pendingHead);
        pendingPanel.Children.Add(new ScrollViewer { Content = _pendingList, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var pendingCard = ResultCard(pendingPanel); Grid.SetRow(pendingCard, 1); right.Children.Add(pendingCard);
        Grid.SetColumn(right, 2); grid.Children.Add(right);

        // Bottom action bar: destination note left, organize actions right with one primary.
        var bar = new Grid { ColumnDefinitions = new("*,Auto") };
        var note = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6, Margin = new(0, 0, 12, 0) };
        Grid.SetColumn(_destination, 1);
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

    private static Border ResultCard(Control content)
    {
        var card = Card(content);
        card.Bind(Border.BorderBrushProperty, Resource(Hairline)); card.BorderThickness = new(1);
        return card;
    }

    private void ConfigureResultSegments(Control result)
    {
        StackPanel? segments = null;
        void SizeSegments(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (segments is null) return;
            foreach (var button in segments.Children.OfType<Button>())
            {
                button.Bind(Button.HeightProperty, Resource("AcButtonHeight")); button.MinHeight = 0; button.Padding = new(12, 0);
                button.VerticalContentAlignment = VerticalAlignment.Center;
                button.Bind(Border.BorderBrushProperty, Resource(Hairline)); button.BorderThickness = new(button.Classes.Contains("selected") ? 1 : 0);
                if (button.Content is TextBlock label)
                {
                    label.FontSize = 13;
                    label.FontWeight = button.Classes.Contains("selected") ? FontWeight.Bold : FontWeight.Normal;
                }
            }
        }
        void FindSegments(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (segments is not null) segments.Children.CollectionChanged -= SizeSegments;
            segments = _headerActions.Children.OfType<Border>().FirstOrDefault()?.Child as StackPanel;
            if (segments is null) return;
            segments.Children.CollectionChanged += SizeSegments;
            SizeSegments(sender, e);
        }
        result.AttachedToVisualTree += (_, _) =>
        {
            _headerActions.Children.CollectionChanged += FindSegments;
            FindSegments(null, new(NotifyCollectionChangedAction.Reset));
        };
        result.DetachedFromVisualTree += (_, _) =>
        {
            _headerActions.Children.CollectionChanged -= FindSegments;
            if (segments is not null) segments.Children.CollectionChanged -= SizeSegments;
        };
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
                var bar = new Border { [!Border.BackgroundProperty] = Resource(low < histogram.Threshold ? Orange : Blue), CornerRadius = new(3, 3, 0, 0), Margin = new(1.5, 0),
                    VerticalAlignment = VerticalAlignment.Bottom, Height = Math.Max(1, 78 * histogram.Bins[i] / (double)max) };
                ToolTip.SetTip(bar, $"{low:0.00} · {histogram.Bins[i]}"); Grid.SetColumn(bar, i); _histogram.Children.Add(bar);
            }
            _histAxis.Children.Clear();
            var labels = new[] { ($"{histogram.Min:0.00}", HorizontalAlignment.Left), ($"{histogram.Threshold:0.00} {L("阈值")}", HorizontalAlignment.Center), ($"{histogram.Max:0.00}", HorizontalAlignment.Right) };
            for (var i = 0; i < 3; i++) { var text = T(labels[i].Item1, 12, brush: Muted, raw: true); text.HorizontalAlignment = labels[i].Item2; Grid.SetColumn(text, i); _histAxis.Children.Add(text); }
            _histCounts.Children.Clear();
            void Add(int value, string brush, string label)
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                item.Children.Add(T(value.ToString(), 13, FontWeight.Bold, brush, raw: true));
                item.Children.Add(T(label, 12));
                _histCounts.Children.Add(item);
            }
            Add(histogram.Grouped, GreenText, "已归组"); Add(histogram.Pending, Orange, "待确认"); Add(histogram.Failed, Ink, "失败");
        }
        // Use the same shoot/count ordering as the tiles, including for other rules awaiting review.
        _resultCategoryNumbers.Clear();
        foreach (var resultRule in spec.Rules)
        {
            var categories = files.Where(f => f.Result is not null && f.Error is null)
                .Select(f => f.Result!.Decisions.FirstOrDefault(d => d.RuleId == resultRule.Id))
                .OfType<FolderClassificationDecision>().Where(d => !d.NeedsReview)
                .GroupBy(d => (d.CategoryId, d.CategoryName, d.OutfitShootName))
                .OrderBy(g => g.Key.OutfitShootName ?? "").ThenByDescending(g => g.Count());
            var number = 0;
            foreach (var category in categories)
                _resultCategoryNumbers.TryAdd((resultRule.Id, category.Key.CategoryId!), ++number);
        }
        RenderPending(files);
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
        head.Children.Add(T("整理前", 15, FontWeight.Bold)); head.Children.Add(T($"{summary.Sources.Length} {L("个源文件夹")}", 12, brush: Muted, raw: true));
        _before.Children.Add(head);
        _before.Children.Add(T($"{summary.Images + summary.Videos} {L("个文件混在一起")}", 12, brush: Muted, raw: true));
        var wrap = new UniformGrid { Columns = 5, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(4, 12, 0, 16) };
        var samples = summary.Sources.SelectMany(s => s.SamplePaths).Take(50).ToArray();
        var random = new Random(7);
        foreach (var path in samples)
        {
            var thumb = new ClassifyThumb(48) { Width = 36, Height = 36, Margin = new(2), [!Border.CornerRadiusProperty] = Resource("AcThumbRadius"),
                RenderTransform = new RotateTransform(random.Next(-8, 9)), [!ClassifyThumb.BoxShadowProperty] = Resource("UiThumbnailShadow") };
            thumb.SetSource(path, VideoFormats.IsVideo(path) ? 1 : null); wrap.Children.Add(thumb);
        }
        _before.Children.Add(wrap);
        foreach (var (label, value) in new[] { ("图片", summary.Images.ToString()), ("视频", summary.Videos.ToString()), ("总大小", Size(summary.Bytes)) })
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto") };
            row.Children.Add(T(label, 12, brush: Muted)); var v = T(value, 12, raw: true); Grid.SetColumn(v, 1); row.Children.Add(v);
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
        var columns = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 20, RowSpacing = 24 };
        var index = 0; var shootIndex = 0;
        foreach (var shoot in shoots)
        {
            var block = new StackPanel { Spacing = 8 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(new LucideIcon { Kind = "folder-closed", Width = 14, Height = 14, [!LucideIcon.ForegroundProperty] = Resource(Blue) });
            head.Children.Add(T(shoot.Key.Length > 0 ? shoot.Key : L(rule.Name), 13, FontWeight.Bold, raw: true));
            head.Children.Add(T(shoot.Sum(g => g.Items.Length).ToString(), 12, brush: Muted, raw: true));
            block.Children.Add(head);
            var row = new WrapPanel { Margin = new(20, 0, 0, 0) };
            foreach (var group in shoot.OrderByDescending(g => g.Items.Length))
            {
                index++;
                var card = new StackPanel { Width = 112, Spacing = 3, Margin = new(0, 0, 10, 6) };
                card.Children.Add(new Mosaic(112, 128, group.Items.Take(3).Select(x => x.File.Path).ToArray()));
                card.Children.Add(new TextBlock { Text = $"{index:00} {group.Key.CategoryName}", [!TextBlock.FontSizeProperty] = Resource("UiFontBody"), [!TextBlock.ForegroundProperty] = Resource(Ink), TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new(0, 4, 0, 0) });
                card.Children.Add(T($"{group.Items.Length} {L("个文件")}", 12, brush: Muted, raw: true));
                row.Children.Add(card);
            }
            block.Children.Add(row);
            if (shootIndex % 2 == 0) columns.RowDefinitions.Add(new(GridLength.Auto));
            Grid.SetColumn(block, shootIndex % 2); Grid.SetRow(block, shootIndex / 2);
            columns.Children.Add(block); shootIndex++;
        }
        _after.Children.Add(columns);
        if (pending.Length > 0)
        {
            var banner = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10 };
            banner.Children.Add(new LucideIcon { Kind = "folder-closed", Width = 15, Height = 15, [!LucideIcon.ForegroundProperty] = Resource(Orange) });
            var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            text.Inlines!.Add(new Avalonia.Controls.Documents.Run(L("待确认")) { FontWeight = FontWeight.Bold, [!Avalonia.Controls.Documents.Run.ForegroundProperty] = Resource(Ink) });
            text.Inlines.Add(new Avalonia.Controls.Documents.Run($"  {pending.Length} {L("个文件")} · {L("确认前不会整理")}") { [!Avalonia.Controls.Documents.Run.ForegroundProperty] = Resource(Muted) });
            Grid.SetColumn(text, 1); banner.Children.Add(text);
            var mosaic = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var item in pending.Take(3)) { var t = new ClassifyThumb(40) { Width = 36, Height = 36 }; t.SetSource(item.File.Path, VideoFormats.IsVideo(item.File.Path) ? 1 : null); mosaic.Children.Add(t); }
            Grid.SetColumn(mosaic, 2); banner.Children.Add(mosaic);
            _after.Children.Add(new Border { [!Border.BackgroundProperty] = Resource(Banner), [!Border.CornerRadiusProperty] = Resource("AcPreviewRadius"), Padding = new(14, 12), Child = banner });
        }
    }

    private void RenderPending(FolderClassificationTaskFile[] files)
    {
        _pendingList.Children.Clear();
        var pending = files.Where(f => f.Result is not null && f.Error is null && f.Result.Decisions.Any(d => d.NeedsReview)).ToArray();
        _pendingTitle.Text = $"{pending.Length} {L("个")} · {L("点选归属")}";
        if (pending.Length == 0) { _pendingList.Children.Add(T("没有需要确认的文件", 12, brush: Muted)); return; }
        foreach (var file in pending.Take(100))
        {
            var decision = file.Result!.Decisions.First(d => d.NeedsReview);
            var row = new Grid { ColumnDefinitions = new("60,*"), ColumnSpacing = 10, Margin = new(0, 9) };
            var thumb = new ClassifyThumb(64) { Width = 60, Height = 60, [!Border.CornerRadiusProperty] = Resource("AcThumbRadius"), VerticalAlignment = VerticalAlignment.Top };
            thumb.SetSource(file.Path, VideoFormats.IsVideo(file.Path) ? 1 : null); row.Children.Add(thumb);
            var text = new StackPanel { Spacing = 4 };
            var name = T(System.IO.Path.GetFileName(file.Path), 13, FontWeight.SemiBold, raw: true);
            ToolTip.SetTip(name, name.Text); text.Children.Add(name);
            var reason = new TextBlock { Text = decision.Evidence, [!TextBlock.FontSizeProperty] = Resource("UiFontCaption"), [!TextBlock.ForegroundProperty] = Resource(Muted),
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            Localization.SetIsUserText(reason, true); ToolTip.SetTip(reason, decision.Evidence); text.Children.Add(reason);
            var choices = new Grid { ColumnSpacing = 4 };
            foreach (var score in decision.Scores.OrderByDescending(s => s.Similarity).Take(2))
            {
                // Short category names keep both suggestions and the keep action on one line.
                var shortName = score.Name.Split('·', StringSplitOptions.TrimEntries)[0];
                var label = _resultCategoryNumbers.TryGetValue((decision.RuleId, score.CategoryId), out var number)
                    ? $"{number:00} {shortName}" : shortName;
                var choice = ResultChoice(label, raw: true);
                var category = score.CategoryId;
                choice.Click += async (_, _) => await ResolveAsync(file.Path, decision.RuleId, category);
                ToolTip.SetTip(choice, $"{score.Name} · {L("相似度")} {score.Similarity:0.00}");
                Grid.SetColumn(choice, choices.ColumnDefinitions.Count);
                choices.ColumnDefinitions.Add(new(1, GridUnitType.Star)); choices.Children.Add(choice);
            }
            var keep = ResultChoice("保持待确认"); keep.IsHitTestVisible = false; keep.Focusable = false;
            Grid.SetColumn(keep, choices.ColumnDefinitions.Count);
            choices.ColumnDefinitions.Add(new(GridLength.Auto)); choices.Children.Add(keep);
            text.Children.Add(choices);
            Grid.SetColumn(text, 1); row.Children.Add(text);
            _pendingList.Children.Add(new Rectangle { Height = 1, [!Rectangle.FillProperty] = Resource(Hairline) }); _pendingList.Children.Add(row);
        }
    }

    private static Button ResultChoice(string label, bool raw = false)
    {
        var choice = Pill(label, height: 23);
        // A direct text child can trim in its grid cell; Pill's horizontal panel cannot.
        choice.Content = T(label, 12, raw: raw);
        choice.Padding = new(7, 0); choice.MinWidth = 0; choice.MinHeight = 0;
        choice.HorizontalAlignment = HorizontalAlignment.Stretch;
        choice.HorizontalContentAlignment = HorizontalAlignment.Center;
        return choice;
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
