using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class AiTaskWindow
{
    private ListBox? _longSegmentList;
    private SummarySegmentProgress[]? _shownLongProgress;
    private void BuildLongSummaryProgress(Grid outcome)
    {
        _longSegmentList = new ListBox { Name = "SummarySegmentProgress", MaxHeight = 320, IsVisible = false,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<SummarySegmentProgress>((segment, _) =>
            {
                if (segment is null) return null;
                var row = new StackPanel { Spacing = 3, Margin = new(0, 4) };
                var state = Localization.Text(segment.State switch { "running" => "理解中", "cached" => "已复用", "done" => "已完成", "degraded" => "降级结果", "failed" => "失败", _ => "等待" });
                var title = Ui.Text($"{MediaTime.Format(segment.Start)} – {MediaTime.Format(segment.End)} · {state}", "caption"); Localization.SetIsUserText(title, true); row.Children.Add(title);
                if (segment.Summary.Length > 0) { var text = Ui.Text(segment.Summary); Localization.SetIsUserText(text, true); row.Children.Add(text); }
                return row;
            }) };
        ScrollViewer.SetHorizontalScrollBarVisibility(_longSegmentList, ScrollBarVisibility.Disabled);
        Grid.SetRow(_longSegmentList, 2); outcome.Children.Add(_longSegmentList);
    }
    private void RefreshLongSummaryProgress()
    {
        if (_longSegmentList is null) return;
        var snapshot = SummaryProgressStore.Get(_job.Id.ToString());
        if (snapshot is null || snapshot.SequenceEqual(_shownLongProgress ?? [])) return;
        _shownLongProgress = snapshot; _longSegmentList.ItemsSource = snapshot; _longSegmentList.IsVisible = snapshot.Length > 0;
        if (_longSegmentList.Parent is Grid outcome)
            outcome.RowDefinitions[2].Height = snapshot.Length > 0 ? new(1, GridUnitType.Star) : new(0);
    }
}
