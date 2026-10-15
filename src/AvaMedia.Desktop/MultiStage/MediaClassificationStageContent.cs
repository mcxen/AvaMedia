using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>Host actions for AI 媒体标签 / 文件夹分类; null hides the action.</summary>
internal sealed record MediaClassificationTaskActions(Func<Task>? OpenResult = null);

/// <summary>媒体标签 / 文件夹分类 content slot: latest analysed item, per-item progress, statistics, item list and category plan.</summary>
internal sealed class MediaClassificationStageContent : Grid, IMultiStageToolContent
{
    private readonly IMediaClassificationTask _task;
    private readonly MediaClassificationTaskActions _actions;
    private readonly MsPreviewCard _preview = new();
    private readonly ProgressBar _progress = new() { Classes = { "ac" }, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _progressText = MsUi.Caption(), _progressRight = MsUi.Caption();
    private readonly TextBlock _current = new() { Classes = { "ms-small" }, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 8, 0, 0) };
    private readonly MsStatGrid _stats = new();
    private readonly StackPanel _runRight = new() { Spacing = 12 }, _configRight = new() { Spacing = 9 };
    private readonly MsSettingsGroup _settings = new();
    private readonly TextBlock _configNote = new() { Classes = { "ms-small" }, TextWrapping = TextWrapping.Wrap, LineHeight = 16 };
    private readonly Grid _bottom;
    private readonly TextBlock _itemsTitle = new() { Classes = { "ms-heading" } }, _itemsNote = MsUi.Small();
    private readonly ListBox _items = new() { Classes = { "ms-segments" } };
    private readonly ObservableCollection<MediaClassificationItemView> _itemRows = [];
    private readonly EmptyState _itemsEmpty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _groupsTitle = new() { Classes = { "ms-heading" } }, _groupsNote = MsUi.Small();
    private readonly StackPanel _groups = new();
    private readonly EmptyState _groupsEmpty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly MsOutputLine _output;
    private readonly Button _reveal, _open;
    private MediaClassificationTaskView _view = new();

    public MediaClassificationStageContent(IMediaClassificationTask task, MediaClassificationTaskActions actions)
    {
        _task = task; _actions = actions;
        RowDefinitions = new("Auto,*");
        MsUi.Brush(this, RowSpacingProperty, "AcMsGap");
        var left = new StackPanel { Spacing = 10 };
        MsUi.Brush(left, WidthProperty, "AcMsPreviewWidth");
        left.Children.Add(_preview); left.Children.Add(_configNote);
        var progress = new StackPanel();
        progress.Children.Add(MsUi.HeadRow(MsUi.Heading("逐项分析"), _progressRight));
        MsUi.Brush(_progress, HeightProperty, "AcProgressHeight");
        progress.Children.Add(MsUi.Columns("*,Auto", _progress, _progressText));
        progress.Children.Add(_current);
        _runRight.Children.Add(MsUi.Card(progress)); _runRight.Children.Add(_stats);
        _configRight.Children.Add(MsUi.HeadRow(MsUi.Heading("整理设置"), null, 0)); _configRight.Children.Add(_settings);
        var right = new Panel(); right.Children.Add(_runRight); right.Children.Add(_configRight);
        Children.Add(MsUi.Columns("Auto,*", left, right));
        _bottom = MsUi.Columns("3*,2*", BuildItems(), BuildGroups()); SetRow(_bottom, 1); Children.Add(_bottom);
        _output = new MsOutputLine(task);
        _reveal = MsUi.Action("在文件夹中显示", () =>
        {
            var target = _view.Output.Path.Length > 0 ? _view.Output.Path : _view.Output.Folder;
            if (File.Exists(target)) PlatformServices.RevealFile(target); else if (Directory.Exists(target)) PlatformServices.OpenFolder(target);
            return Task.CompletedTask;
        });
        _open = MsUi.Action(task.View.IsTagging ? "查看标签结果" : "查看分类结果", actions.OpenResult, primary: true);
    }

    public Control Result => this;
    public Control Output => _output;
    public IReadOnlyList<Button> LeadingActions => [_reveal];
    public IReadOnlyList<Button> TrailingActions => [_open];
    public string SourceTitle => Path.GetFileName(_task.View.SourcePath.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : _task.View.SourcePath;
    public string SourceTooltip => _task.View.SourcePath;
    public Task InitializeAsync() => Task.CompletedTask;

    private Border BuildItems()
    {
        var grid = new Grid { RowDefinitions = new("Auto,*") };
        var head = MsUi.HeadRow(_itemsTitle, _itemsNote, 6); head.Margin = new(14, 11, 14, 6);
        grid.Children.Add(head);
        _items.ItemsSource = _itemRows;
        _items.ItemTemplate = new FuncDataTemplate<MediaClassificationItemView>((item, _) => item is null ? null : ItemRow(item));
        Avalonia.Automation.AutomationProperties.SetName(_items, Localization.Text("文件"));
        var area = new Panel(); area.Children.Add(_items); area.Children.Add(_itemsEmpty);
        SetRow(area, 1); grid.Children.Add(area);
        return MsUi.Card(grid, padded: false);
    }

    private Border BuildGroups()
    {
        var grid = new Grid { RowDefinitions = new("Auto,*") };
        var head = MsUi.HeadRow(_groupsTitle, _groupsNote, 6); head.Margin = new(14, 11, 14, 6);
        grid.Children.Add(head);
        var scroll = new ScrollViewer { Content = _groups, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var area = new Panel(); area.Children.Add(scroll); area.Children.Add(_groupsEmpty);
        SetRow(area, 1); grid.Children.Add(area);
        return MsUi.Card(grid, padded: false);
    }

    private static Control ItemRow(MediaClassificationItemView item)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10, Margin = new(14, 5) };
        var glyph = new StageGlyph { State = MsUi.GlyphName(item.State), Number = 0, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(glyph);
        var text = new StackPanel { Spacing = 1 };
        var name = new TextBlock { Classes = { "ms-body" }, Text = Path.GetFileName(item.Path), TextTrimming = TextTrimming.CharacterEllipsis };
        Localization.SetIsUserText(name, true); ToolTip.SetTip(name, item.Path); text.Children.Add(name);
        var detail = item.State == StageState.Failed ? item.Detail : item.Result.Length > 0 ? item.Result : item.State == StageState.Active ? Localization.Text("分析中") : "";
        if (detail.Length > 0)
        {
            var line = MsUi.Small(detail); Localization.SetIsUserText(line, item.State != StageState.Active || item.Result.Length > 0);
            if (item.State == StageState.Failed) MsUi.Brush(line, TextBlock.ForegroundProperty, "UiDanger");
            else if (item.Result.Length > 0) MsUi.Brush(line, TextBlock.ForegroundProperty, "UiText");
            ToolTip.SetTip(line, item.Detail.Length > 0 ? item.Detail : null);
            text.Children.Add(line);
        }
        SetColumn(text, 1); grid.Children.Add(text);
        if (item.Confidence is { } confidence)
        {
            var value = MsUi.Small(confidence.ToString("0%", CultureInfo.InvariantCulture)); SetColumn(value, 2); grid.Children.Add(value);
        }
        return new Border { Child = grid, Classes = { "ms-row-line" }, BorderThickness = new(0, .5, 0, 0) };
    }

    private static Control GroupRow(MediaClassificationGroupView group, int max, bool first)
    {
        var stack = new StackPanel { Spacing = 5, Margin = new(14, 7) };
        var head = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        var name = new TextBlock { Classes = { "ms-body" }, Text = group.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        Localization.SetIsUserText(name, true); head.Children.Add(name);
        var count = MsUi.Caption(Localization.Format($"{group.Count} 项")); SetColumn(count, 1); head.Children.Add(count);
        stack.Children.Add(head);
        var bar = new ProgressBar { Classes = { "ac" }, Minimum = 0, Maximum = Math.Max(1, max), Value = group.Count };
        MsUi.Brush(bar, HeightProperty, "AcProgressHeight"); stack.Children.Add(bar);
        if (group.Target.Length > 0)
        {
            var target = MsUi.Small("→ " + MsUi.Display(group.Target)); Localization.SetIsUserText(target, true); ToolTip.SetTip(target, group.Target);
            stack.Children.Add(target);
        }
        return new Border { Child = stack, Classes = { "ms-row-line" }, BorderThickness = new(0, first ? 0 : .5, 0, 0) };
    }

    public MultiStageContentState Update(MultiStageTaskSnapshot snapshot)
    {
        var view = _view = _task.View; var state = snapshot.State;
        var configuring = state == MultiStageTaskState.Configuring;
        var done = state == MultiStageTaskState.Completed;
        var running = state is MultiStageTaskState.Running or MultiStageTaskState.Paused or MultiStageTaskState.Stopping;
        _runRight.IsVisible = _bottom.IsVisible = !configuring;
        _configRight.IsVisible = _configNote.IsVisible = configuring;
        _configNote.Text = Localization.Text(view.IsTagging ? "逐个文件识别内容并写入标签，原文件位置不变。" : "逐个文件识别内容，按类别规划目标文件夹后再移动或复制。");
        if (configuring) RefreshSettings(view);

        var total = view.TotalItems;
        _progress.Value = total > 0 ? Math.Clamp((double)view.DoneItems / total, 0, 1) : 0;
        _progressText.Text = total > 0 ? $"{view.DoneItems} / {total}" : "—";
        _progressRight.Text = total > 0 && running ? Localization.Format($"{(double)view.DoneItems / total:0%}") : "";
        var active = view.Items.LastOrDefault(item => item.State == StageState.Active);
        _current.Text = active is not null ? Localization.Format($"正在分析 {Path.GetFileName(active.Path)}")
            : done ? Localization.Text(view.FailedItems > 0 ? "已完成，部分文件未能识别" : "全部文件已分析")
            : total == 0 ? Localization.Text("扫描文件后开始逐项分析") : "";

        _stats.Set(0, done ? "已分析" : "已分析", total > 0 ? view.DoneItems.ToString(CultureInfo.InvariantCulture) : "", total > 0 ? "/ " + total.ToString(CultureInfo.InvariantCulture) : "");
        _stats.Set(1, "未能识别", total > 0 ? view.FailedItems.ToString(CultureInfo.InvariantCulture) : "");
        _stats.Set(2, view.IsTagging ? "标签" : "类别", view.Groups.Count > 0 ? view.Groups.Count.ToString(CultureInfo.InvariantCulture) : "");
        var elapsed = snapshot.Elapsed.TotalSeconds;
        _stats.Set(3, done ? "用时" : "已用时", elapsed > 0 ? Localization.Format($"{(done ? MultiStageTaskWindow.Seconds(elapsed) : Math.Floor(elapsed).ToString(CultureInfo.InvariantCulture))} 秒") : "");

        RefreshItems(view, running, done);
        RefreshGroups(view, done);
        RefreshPreview(view, state, active);

        _output.Update(view.Output, snapshot, view.IsTagging ? "标签保存在媒体库" : "应用内结果");
        var target = view.Output.Path.Length > 0 ? view.Output.Path : view.Output.Folder;
        _reveal.IsVisible = done && view.Output.Exists && !view.Output.IsFixedLocation && (File.Exists(target) || Directory.Exists(target));
        _open.IsVisible = done && view.HasResult && _actions.OpenResult is not null;
        var (errorTitle, recovery) = MsUi.Error(snapshot);
        var note = done ? Localization.Join(" · ", new[] { Localization.Format($"{view.DoneItems} 个文件"),
                view.Groups.Count > 0 ? Localization.Format($"{view.Groups.Count} 个{Localization.Text(view.IsTagging ? "标签" : "类别")}") : "" }.Where(s => s.Length > 0))
            : MsUi.LiveNote(snapshot, view.DoneItems > 0);
        var detail = Localization.Join(" · ", new[] { total > 0 ? Localization.Format($"{total} 个文件") : "", Localization.Text(view.IsTagging ? "媒体标签" : "文件夹分类"), view.ModelName }.Where(s => s.Length > 0));
        return new(detail, note, MsUi.Plan(snapshot, view.ModelName), errorTitle, recovery, _open.IsVisible);
    }

    private void RefreshSettings(MediaClassificationTaskView view) => _settings.Update(
    [
        ("整理方式", view.IsTagging ? "识别内容并写入标签" : "按内容分类到文件夹", ""),
        ("来源", MsUi.Display(view.SourcePath), ""),
        ("文件数", view.TotalItems > 0 ? view.TotalItems.ToString(CultureInfo.InvariantCulture) : "", view.TotalItems > 0 ? " 个" : ""),
        ("识别模型", view.ModelName, "")
    ]);

    private void RefreshItems(MediaClassificationTaskView view, bool running, bool done)
    {
        _itemsTitle.Text = Localization.Text(done ? "文件" : "最近处理");
        _itemsNote.Text = view.FailedItems > 0 ? Localization.Format($"{view.FailedItems} 个未能识别") : view.Items.Count > 0 && running ? Localization.Text("处理中持续更新") : "";
        // Running: newest first so the item being analysed stays on top; finished: processing order.
        var rows = (done ? view.Items : view.Items.Reverse()).ToArray();
        if (!rows.SequenceEqual(_itemRows))
        {
            var common = 0;
            while (common < rows.Length && common < _itemRows.Count && rows[common] == _itemRows[common]) common++;
            if (!done && common == 0 && _itemRows.Count > 0) _itemRows.Clear();
            while (_itemRows.Count > common) _itemRows.RemoveAt(_itemRows.Count - 1);
            foreach (var row in rows.Skip(common)) _itemRows.Add(row);
        }
        _itemsEmpty.IsVisible = rows.Length == 0;
        _itemsEmpty.Text = Localization.Text(done ? "没有可处理的文件" : "尚未分析文件");
    }

    private void RefreshGroups(MediaClassificationTaskView view, bool done)
    {
        _groupsTitle.Text = Localization.Text(view.IsTagging ? "标签" : done ? "分类结果" : "分类计划");
        _groupsNote.Text = view.Groups.Count > 0 ? Localization.Format($"{view.Groups.Count} 个") : "";
        _groupsEmpty.IsVisible = view.Groups.Count == 0;
        _groupsEmpty.Text = Localization.Text(view.IsTagging ? "识别后汇总标签" : "分析完成后规划类别");
        var key = string.Join("|", view.Groups.Select(g => $"{g.Name}:{g.Count}:{g.Target}"));
        if (_groups.Tag as string == key) return;
        _groups.Tag = key; _groups.Children.Clear();
        var max = view.Groups.Count == 0 ? 1 : view.Groups.Max(g => g.Count);
        for (var i = 0; i < view.Groups.Count; i++) _groups.Children.Add(GroupRow(view.Groups[i], max, i == 0));
    }

    private void RefreshPreview(MediaClassificationTaskView view, MultiStageTaskState state, MediaClassificationItemView? active)
    {
        _preview.Dimmed = state == MultiStageTaskState.Failed;
        _preview.SetChips(active is not null ? Localization.Text("分析中") : "", view.TotalItems > 0 && view.DoneItems > 0 ? $"{view.DoneItems} / {view.TotalItems}" : "");
        if (view.Preview is { } bytes) _preview.ShowEncoded(bytes);
        else if (view.Items.LastOrDefault(i => i.State is StageState.Done or StageState.Active) is { } item && IsImage(item.Path) && File.Exists(item.Path)) _preview.ShowFile(item.Path);
        else _preview.ShowEncoded(null);
        var latest = view.Items.LastOrDefault(i => i.State == StageState.Done);
        _preview.SetCaption(state == MultiStageTaskState.Configuring ? "待整理" : active is not null ? "正在分析" : "最近分析",
            view.PreviewCaption.Length > 0 ? view.PreviewCaption : latest is null ? "" : Path.GetFileName(latest.Path) + (latest.Result.Length > 0 ? " · " + latest.Result : ""));
    }

    private static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".gif";

    public void Dispose() => _preview.Dispose();
}
