using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public enum TaskSectionKind { Active, Queued, Finished }

/// <summary>Display-only group header in the task list (UISPEC 7.4): 进行中 / 排队 / 已完成. Never selectable.</summary>
public sealed class TaskSection(TaskSectionKind kind)
{
    public TaskSectionKind Kind { get; } = kind;
    public int Count { get; private set; }
    public int Failed { get; private set; }
    public bool Collapsed { get; private set; }
    public event Action? Changed;

    public static TaskSectionKind KindOf(Job job) => job.State switch
    {
        JobState.Running or JobState.Stopping or JobState.Paused or JobState.AwaitingUser => TaskSectionKind.Active,
        JobState.Waiting => TaskSectionKind.Queued,
        _ => TaskSectionKind.Finished
    };

    public void Update(int count, int failed, bool collapsed)
    {
        if (Count == count && Failed == failed && Collapsed == collapsed) return;
        Count = count; Failed = failed; Collapsed = collapsed;
        Changed?.Invoke();
    }
}

/// <summary>Small section header; the 已完成 header folds or unfolds finished tasks.</summary>
public sealed class TaskSectionHeader : Border
{
    private readonly LucideIcon _chevron = new() { Width = 11, Height = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _failed = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private TaskSection? _section;
    private bool _pressed;

    public TaskSectionHeader()
    {
        Classes.Add("task-section-header");
        Background = Brushes.Transparent; Padding = new(12, 10, 12, 4);
        _title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
        _count.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
        _failed.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiDanger"));
        _chevron.Bind(LucideIcon.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
        _count.FontFeatures = _failed.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _chevron, _title, _count, _failed } };
        DataContextChanged += (_, _) => Bind(DataContext as TaskSection);
        DetachedFromVisualTree += (_, _) => { Localization.Changed -= LanguageChanged; Bind(null); };
        AttachedToVisualTree += (_, _) => { Localization.Changed += LanguageChanged; Bind(DataContext as TaskSection); };
        // The header is not a row: presses never change the task selection.
        PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _pressed = _section?.Kind == TaskSectionKind.Finished && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        };
        PointerReleased += (_, e) =>
        {
            if (!_pressed) return;
            _pressed = false; e.Handled = true;
            if (new Rect(Bounds.Size).Contains(e.GetPosition(this))) this.FindAncestorOfType<MainWindow>()?.ToggleCompletedTasks();
        };
    }

    private void LanguageChanged(object? sender, EventArgs e) => Refresh();

    private void Bind(TaskSection? section)
    {
        if (ReferenceEquals(_section, section)) return;
        if (_section is not null) _section.Changed -= Refresh;
        _section = section;
        if (_section is not null) _section.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        if (_section is not { } section) return;
        var finished = section.Kind == TaskSectionKind.Finished;
        _title.Text = Localization.Text(section.Kind switch { TaskSectionKind.Active => "进行中", TaskSectionKind.Queued => "排队", _ => "已完成" });
        _count.Text = section.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _failed.Text = section.Failed > 0 ? Localization.Format($"失败 {section.Failed}") : "";
        _failed.IsVisible = section.Failed > 0;
        _chevron.IsVisible = finished;
        _chevron.Kind = section.Collapsed ? "chevron-right" : "chevron-down";
        Cursor = finished ? new Cursor(StandardCursorType.Hand) : null;
        var name = finished ? Localization.Text(section.Collapsed ? "展开已完成任务" : "折叠已完成任务") : _title.Text;
        AutomationProperties.SetName(this, name);
        ToolTip.SetTip(this, finished ? name : null);
    }
}
