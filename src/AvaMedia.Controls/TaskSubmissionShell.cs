using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;

namespace AvaMedia.Controls;

/// <summary>Task form layout. The host owns validation, task creation and window lifetime.</summary>
public sealed class TaskSubmissionShell : ContentControl
{
    public static readonly StyledProperty<Control?> OutputContentProperty =
        AvaloniaProperty.Register<TaskSubmissionShell, Control?>(nameof(OutputContent));
    public static readonly StyledProperty<Control?> StatusContentProperty =
        AvaloniaProperty.Register<TaskSubmissionShell, Control?>(nameof(StatusContent));
    public static readonly StyledProperty<Button?> CancelActionProperty =
        AvaloniaProperty.Register<TaskSubmissionShell, Button?>(nameof(CancelAction));
    public static readonly StyledProperty<Button?> PrimaryActionProperty =
        AvaloniaProperty.Register<TaskSubmissionShell, Button?>(nameof(PrimaryAction));
    public static readonly StyledProperty<bool> StartImmediatelyProperty =
        AvaloniaProperty.Register<TaskSubmissionShell, bool>(nameof(StartImmediately), true);
    public static readonly StyledProperty<bool> IsEditingProperty =
        AvaloniaProperty.Register<TaskSubmissionShell, bool>(nameof(IsEditing));

    private ComboBox? _mode;
    private ContentPresenter? _output;
    private bool _syncing;

    public Control? OutputContent { get => GetValue(OutputContentProperty); set => SetValue(OutputContentProperty, value); }
    public Control? StatusContent { get => GetValue(StatusContentProperty); set => SetValue(StatusContentProperty, value); }
    public Button? CancelAction { get => GetValue(CancelActionProperty); set => SetValue(CancelActionProperty, value); }
    public Button? PrimaryAction { get => GetValue(PrimaryActionProperty); set => SetValue(PrimaryActionProperty, value); }
    public bool StartImmediately { get => GetValue(StartImmediatelyProperty); set => SetValue(StartImmediatelyProperty, value); }
    public bool IsEditing { get => GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value); }

    public TaskSubmissionShell() => ComponentResources.BindResource(this, ThemeProperty, "AcTaskSubmissionShell");

    protected override void OnApplyTemplate(TemplateAppliedEventArgs args)
    {
        if (_mode is not null) _mode.SelectionChanged -= ModeChanged;
        base.OnApplyTemplate(args);
        _mode = args.NameScope.Find<ComboBox>("PART_ExecutionMode");
        _output = args.NameScope.Find<ContentPresenter>("PART_OutputPresenter");
        if (_mode is not null)
        {
            _mode.ItemsSource = new[] { new TextBlock { Text = "立即开始" }, new TextBlock { Text = "仅加入队列" } };
            AutomationProperties.SetName(_mode, "任务执行方式");
            _mode.SelectionChanged += ModeChanged;
        }
        RefreshMode();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PrimaryActionProperty)
        {
            if (change.OldValue is Button oldButton) oldButton.PropertyChanged -= PrimaryChanged;
            if (change.NewValue is Button newButton) newButton.PropertyChanged += PrimaryChanged;
        }
        if (change.Property == PrimaryActionProperty || change.Property == StartImmediatelyProperty
            || change.Property == IsEditingProperty || change.Property == OutputContentProperty) RefreshMode();
    }

    private void PrimaryChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsEnabledProperty) RefreshMode();
    }

    private void ModeChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (!_syncing && _mode?.SelectedIndex is 0 or 1)
            SetCurrentValue(StartImmediatelyProperty, _mode.SelectedIndex == 0);
    }

    private void RefreshMode()
    {
        if (_output is not null) _output.IsVisible = OutputContent is not null;
        if (_mode is null) return;
        _syncing = true;
        try
        {
            _mode.IsVisible = !IsEditing;
            _mode.IsEnabled = PrimaryAction?.IsEnabled == true;
            _mode.SelectedIndex = StartImmediately ? 0 : 1;
        }
        finally { _syncing = false; }
    }
}
