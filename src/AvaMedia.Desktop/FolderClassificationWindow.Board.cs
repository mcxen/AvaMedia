using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow
{
    private const string AllBasket = "@all", PendingBasket = "@pending", ReviewBasket = "@review";
    private readonly ComboBox _boardRule = new() { MinWidth = 130 };
    private readonly StackPanel _baskets = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBox _search = Ui.Input();
    private readonly CheckBox _onlyIncluded = new() { Content = "仅看已选" };
    private readonly Button _allFilter = new();
    private readonly Button _pendingFilter = new();
    private readonly TextBlock _boardEmpty = Ui.Text("添加文件夹后，在这里查看封面与分类", "caption");
    private readonly DispatcherTimer _boardTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private string _basketId = AllBasket;
    private string? _boardRuleId;
    private bool _renderingBoard;
    private MediaFileEntry? _dragEntry;
    private readonly string _dragToken = "AvaMedia-classification-" + Guid.NewGuid().ToString("N");

    private Control BuildBoard()
    {
        var board = new Grid { RowDefinitions = new("Auto,Auto,Auto,*"), RowSpacing = 8 };
        var toolbar = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8 };
        _boardRule.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<FolderClassificationRule>((rule, _) => UserText(rule?.Name ?? ""));
        toolbar.Children.Add(_boardRule); _search.Watermark = Localization.Text("搜索文件或标签");
        Localization.SetIsUserText(_search, true); Grid.SetColumn(_search, 1); toolbar.Children.Add(_search);
        Grid.SetColumn(_onlyIncluded, 2); toolbar.Children.Add(_onlyIncluded); board.Children.Add(toolbar);
        var basketsScroll = new ScrollViewer { Content = _baskets, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, MaxHeight = 170 };
        Grid.SetRow(basketsScroll, 1); board.Children.Add(basketsScroll);
        var summary = new StackPanel { Spacing = 5 };
        var filters = new WrapPanel();
        foreach (var filter in new[] { _allFilter, _pendingFilter }) { filter.Margin = new(0, 0, 6, 0); filters.Children.Add(filter); }
        summary.Children.Add(filters);
        _allFilter.Click += (_, _) => { _basketId = AllBasket; RenderBoard(); };
        _pendingFilter.Click += (_, _) => { _basketId = PendingBasket; RenderBoard(); };

        Grid.SetRow(summary, 2); board.Children.Add(summary);
        var filesPanel = new Grid(); filesPanel.Children.Add(_files);
        _boardEmpty.HorizontalAlignment = HorizontalAlignment.Center; _boardEmpty.VerticalAlignment = VerticalAlignment.Center;
        filesPanel.Children.Add(_boardEmpty); Grid.SetRow(filesPanel, 3); board.Children.Add(filesPanel);
        _files.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(PaddingProperty, new Thickness(6)), new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _files.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MediaFileEntry>((entry, _) => entry is null ? null : BuildFileCard(entry));
        _boardRule.SelectionChanged += (_, _) =>
        {
            if (_renderingBoard) return;
            _boardRuleId = (_boardRule.SelectedItem as FolderClassificationRule)?.Id;
            _basketId = AllBasket; RenderBoard(); RenderDetails();
        };
        _search.TextChanged += (_, _) => QueueBoardRefresh(); _onlyIncluded.IsCheckedChanged += (_, _) => RenderBoard();
        _boardTimer.Tick += (_, _) => { _boardTimer.Stop(); if (!_closed) RenderBoard(); };
        return board;
    }

    private Control BuildFileCard(MediaFileEntry entry)
    {
        var row = new Grid { ColumnDefinitions = new("Auto,128,*"), ColumnSpacing = 10, MinHeight = 86 };
        var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, IsEnabled = !_busy };
        check.Bind(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = _imports });
        check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(MediaFileEntry.Include)) { Mode = BindingMode.TwoWay }); row.Children.Add(check);
        var cover = new ClassificationCover { Height = 80, Seconds = CoverSeconds(entry) };
        cover.Bind(ClassificationCover.PathProperty, new Binding(nameof(MediaFileEntry.Path)));
        cover.Bind(ClassificationCover.SecondsProperty, new Binding(nameof(MediaFileEntry.Details))
        { Converter = new FuncValueConverter<string?, double>(_ => CoverSeconds(entry)) });
        Grid.SetColumn(cover, 1); row.Children.Add(cover); AddCoverDrag(cover, entry);
        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var name = UserText(""); name.FontWeight = FontWeight.SemiBold;
        name.Bind(TextBlock.TextProperty, new Binding(nameof(MediaFileEntry.Name))); name.Bind(ToolTip.TipProperty, new Binding(nameof(MediaFileEntry.Path)));
        name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; info.Children.Add(name);
        var kindAndState = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var kind = Ui.Text(entry.Kind, "caption"); var state = Ui.Text("", "caption"); state.Bind(TextBlock.TextProperty, new Binding(nameof(MediaFileEntry.Status)));
        kindAndState.Children.Add(kind); kindAndState.Children.Add(state); info.Children.Add(kindAndState);
        var destination = UserText(""); destination.TextWrapping = TextWrapping.NoWrap; destination.TextTrimming = TextTrimming.CharacterEllipsis;
        destination.Bind(TextBlock.TextProperty, new Binding(nameof(MediaFileEntry.Details))); destination.Bind(ToolTip.TipProperty, new Binding(nameof(MediaFileEntry.Details))); info.Children.Add(destination);
        var planned = UserText(""); planned.TextWrapping = TextWrapping.NoWrap; planned.TextTrimming = TextTrimming.CharacterEllipsis;
        planned.Bind(TextBlock.TextProperty, new Binding(nameof(MediaFileEntry.NewName))); planned.Bind(ToolTip.TipProperty, new Binding(nameof(MediaFileEntry.NewName))); info.Children.Add(planned);
        Grid.SetColumn(info, 2); row.Children.Add(info); return row;
    }

    private void QueueBoardRefresh()
    { if (!_closed && _dragEntry is null && !_boardTimer.IsEnabled) _boardTimer.Start(); }

    private bool OutfitPending(FolderClassifiedFile result, FolderClassificationRule rule)
        => rule.ByOutfit && result.Decisions.FirstOrDefault(decision => decision.RuleId == rule.Id)?.Manual != true
            && !OutfitAppearanceService.IsComplete(result.Media);

    private bool AnalysisPending(MediaFileEntry entry, FolderClassificationRule? rule = null)
        => !_results.TryGetValue(entry.Path, out var result) || _analysisPending.Contains(entry.Path)
            || (rule is null ? _rules.Any(active => OutfitPending(result, active)) : OutfitPending(result, rule));

    private string BasketFor(MediaFileEntry entry, FolderClassificationRule? rule)
    {
        if (AnalysisPending(entry, rule)) return PendingBasket;
        var result = _results[entry.Path];
        if (rule is null) return result.Decisions.Any(decision => decision.NeedsReview) ? ReviewBasket : AllBasket;
        return result.Decisions.FirstOrDefault(decision => decision.RuleId == rule.Id)?.CategoryId ?? ReviewBasket;
    }

    private void RenderBoard()
    {
        if (_closed || _renderingBoard || _dragEntry is not null) return;
        _boardTimer.Stop(); _renderingBoard = true;
        var selected = _files.SelectedItem as MediaFileEntry;
        try
        {
            var rule = _rules.FirstOrDefault(item => item.Id == _boardRuleId) ?? _rules.FirstOrDefault();
            _boardRuleId = rule?.Id;
            _boardRule.ItemsSource = _rules.ToArray(); _boardRule.SelectedItem = rule;
            _boardRule.IsVisible = _rules.Count > 1;
            var categories = rule is null ? [] : GroupCategories(rule);
            var categoriesById = categories.ToDictionary(category => category.Id);
            var membersByBasket = _entries.GroupBy(entry => BasketFor(entry, rule)).ToDictionary(group => group.Key, group => group.ToArray());
            var options = new List<(string Id, string Name)> { (AllBasket, Localization.Text("全部")), (PendingBasket, Localization.Text("待分析")), (ReviewBasket, Localization.Text("待确认")) };
            options.AddRange(categories.Select(category => (category.Id, category.Name)));
            if (!options.Any(option => option.Id == _basketId)) _basketId = AllBasket;
            _baskets.Children.Clear();
            _baskets.IsVisible = rule is not null;
            foreach (var option in options.Where(option => option.Id != AllBasket && option.Id != PendingBasket).OrderBy(option => option.Id == ReviewBasket))
            {
                var members = membersByBasket.GetValueOrDefault(option.Id) ?? [];
                _baskets.Children.Add(BuildBasket(option.Id, option.Name, members, rule, categoriesById.GetValueOrDefault(option.Id)));
            }
            _allFilter.Content = Localization.Format($"全部 {_entries.Count}");
            _pendingFilter.Content = Localization.Format($"待分析 {_entries.Count(entry => AnalysisPending(entry, rule))}");
            _allFilter.Classes.Set("primary", _basketId == AllBasket); _pendingFilter.Classes.Set("primary", _basketId == PendingBasket);
            var search = _search.Text?.Trim() ?? "";
            var visible = _entries.Where(entry => (_basketId == AllBasket || (_basketId == PendingBasket
                    ? AnalysisPending(entry, rule) : BasketFor(entry, rule) == _basketId))
                && (_onlyIncluded.IsChecked != true || entry.Include)
                && (search.Length == 0 || entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || entry.Details.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
            // Preserve selection and realized rows while only scores/counts change.
            if (_files.ItemsSource is not MediaFileEntry[] old || !old.SequenceEqual(visible)) _files.ItemsSource = visible;
            _files.SelectedItem = selected is not null && visible.Contains(selected) ? selected : visible.FirstOrDefault();
            _boardEmpty.IsVisible = visible.Length == 0;
            _boardEmpty.Text = Localization.Text(_entries.Count == 0 ? "添加文件夹后，在这里查看封面与分类" : "此分类筐暂无匹配文件");
        }
        finally { _renderingBoard = false; }
        RenderDetails();
    }

    private Button BuildBasket(string id, string name, MediaFileEntry[] members, FolderClassificationRule? rule, FolderClassificationCategory? category)
    {
        var content = new StackPanel { Spacing = 3, Width = 126 };
        var fan = new Canvas { Height = 49, ClipToBounds = false };
        var samples = members.Take(3).ToArray();
        for (var index = 0; index < samples.Length; index++)
        {
            var cover = new ClassificationCover { Width = 63, Height = 43, Path = samples[index].Path, Seconds = CoverSeconds(samples[index]),
                ResolutionWidth = 120, IsHitTestVisible = false, RenderTransform = new RotateTransform((index - 1) * 7) };
            Canvas.SetLeft(cover, 8 + index * 23); Canvas.SetTop(cover, index == 1 ? 1 : 5); fan.Children.Add(cover);
        }
        content.Children.Add(fan);
        var weave = new ClassificationBasketWeave { Height = 24, IsHitTestVisible = false };
        weave.Bind(ClassificationBasketWeave.StrokeProperty, new DynamicResourceExtension("UiBorder")); content.Children.Add(weave);
        var title = UserText(name); title.FontWeight = FontWeight.SemiBold; title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(title, name); content.Children.Add(title);
        content.Children.Add(Ui.Text(Localization.Format($"{members.Length} 个 · 已选 {members.Count(entry => entry.Include)} 个"), "caption"));
        var button = new Button { Content = content, Padding = new(8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        button.Bind(BorderBrushProperty, new DynamicResourceExtension(id == _basketId ? "UiAccent" : "UiBorder"));
        button.BorderThickness = new(id == _basketId ? 2 : 1);
        button.Click += (_, _) => { _basketId = id; RenderBoard(); };
        if (rule is null || id is AllBasket or PendingBasket) return button;
        DragDrop.SetAllowDrop(button, true);
        button.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (_busy || _dragEntry is null || e.DataTransfer.TryGetText() != _dragToken) return;
            e.DragEffects = DragDropEffects.Move; e.Handled = true; button.Classes.Set("primary", true);
        });
        button.AddHandler(DragDrop.DragLeaveEvent, (_, _) => button.Classes.Set("primary", false));
        button.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            button.Classes.Set("primary", false);
            if (_busy || _dragEntry is not { } entry || e.DataTransfer.TryGetText() != _dragToken) return;
            e.Handled = true; await GuardAsync(() => PlaceInBasketAsync(entry, rule, category));
        });
        return button;
    }

    private void AddCoverDrag(Control cover, MediaFileEntry entry)
    {
        Point? start = null;
        cover.PointerPressed += (_, e) =>
        {
            if (!_busy && e.GetCurrentPoint(cover).Properties.IsLeftButtonPressed) { start = e.GetPosition(cover); _files.SelectedItem = entry; }
        };
        cover.PointerReleased += (_, _) => start = null;
        cover.PointerMoved += async (_, e) =>
        {
            if (_busy || start is not { } point || !e.GetCurrentPoint(cover).Properties.IsLeftButtonPressed
                || Math.Abs(e.GetPosition(cover).X - point.X) + Math.Abs(e.GetPosition(cover).Y - point.Y) < 8) return;
            start = null; _boardTimer.Stop(); _dragEntry = entry;
            try
            {
                var transfer = new DataTransfer(); transfer.Add(DataTransferItem.CreateText(_dragToken));
                await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
            }
            finally { _dragEntry = null; RenderBoard(); }
        };
    }

    private Task PlaceInBasketAsync(MediaFileEntry entry, FolderClassificationRule rule, FolderClassificationCategory? category)
    {
        if (_busy || !_entries.Contains(entry)) return Task.CompletedTask;
        if (!_results.TryGetValue(entry.Path, out var result))
        {
            var info = new FileInfo(entry.Path);
            if (!info.Exists) throw new FileNotFoundException("源文件不存在。", entry.Path);
            var media = new MediaTagResult(entry.Path, [], 0, 0, "manual", info.Length, info.LastWriteTimeUtc);
            result = FolderClassification.Classify(media, _rules.ToArray(), (double)(_tagThreshold.Value ?? .5m), _settings.EnableNsfwContent);
        }
        MediaTagService.ValidateSource(result.Media);
        _results[entry.Path] = result with { Decisions = result.Decisions.Select(decision => decision.RuleId == rule.Id
            ? decision with { CategoryId = category?.Id, CategoryName = category?.Name ?? "待确认", Manual = true, Evidence = "人工确认" } : decision).ToArray() };
        UpdateEntry(entry); InvalidatePlan();
        _boardRuleId = rule.Id; _basketId = category?.Id ?? ReviewBasket; RenderBoard(); _files.SelectedItem = entry;
        _status.Text = Localization.Format($"{entry.Name} → {category?.Name ?? Localization.Text("待确认")}");
        return Task.CompletedTask;
    }

    private void RestoreAutomatic(MediaFileEntry entry, FolderClassificationRule rule)
    {
        if (_busy || !_results.TryGetValue(entry.Path, out var previous)) return;
        var automatic = FolderClassification.Classify(previous.Media, _rules.ToArray(), (double)(_tagThreshold.Value ?? .5m), _settings.EnableNsfwContent);
        _results[entry.Path] = previous with { Decisions = previous.Decisions.Select(decision => decision.RuleId == rule.Id
            ? automatic.Decisions.First(item => item.RuleId == rule.Id) : decision).ToArray() };
        if (rule.ByOutfit) RegroupOutfits();
        _basketId = AllBasket; UpdateEntry(entry); InvalidatePlan(); RenderBoard();
    }

    private FolderClassificationCategory[] GroupCategories(FolderClassificationRule rule)
        => FolderOutfitClassification.Categories(rule, _results.Values);

    private void RegroupOutfits()
    {
        foreach (var result in FolderOutfitClassification.Apply(_results.Values, _rules.ToArray(), _settings.EnableNsfwContent))
            _results[result.Media.Path] = result;
        foreach (var entry in _entries) UpdateEntry(entry);
    }
}
