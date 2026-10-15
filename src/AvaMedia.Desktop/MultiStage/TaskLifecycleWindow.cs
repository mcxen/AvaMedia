using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop.MultiStage;

internal sealed record PageAction(string Title, Func<Task> Execute, bool IsEnabled = true);

/// <summary>Tool-owned page content and actions. Only the common bar owns primary button styling.</summary>
internal sealed class TaskPageViewModel(string pageId, Func<Control> createView)
{
    public string PageId { get; } = pageId;
    public Func<Control> CreateView { get; } = createView;
    public PageAction? PrimaryAction { get; set; }
    public PageAction? SecondaryAction { get; set; }
    public string Hint { get; set; } = "";
    public Control? LeadingContent { get; set; }
    public IReadOnlyList<Control> AdditionalActions { get; set; } = [];
    public bool IsReadOnly { get; set; }
    public Control? View { get; set; }
}

internal sealed class PageActionBar : Grid
{
    private readonly ContentControl _leading = new();
    private readonly TextBlock _hint = new() { Classes = { "ms-caption" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _primary, _secondary;
    private PageAction? _primaryAction, _secondaryAction;
    public StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _additional = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private bool _busy;
    public PageActionBar()
    {
        ColumnDefinitions = new("Auto,*,Auto"); ColumnSpacing = 12;
        Bind(MinHeightProperty, new DynamicResourceExtension("AcMsFooterHeight"));
        Bind(MarginProperty, new DynamicResourceExtension("AcMsPadding"));
        _primary = Ui.Button("", () => _ = InvokeAsync(_primaryAction)); _primary.Classes.Add("primary");
        _secondary = Ui.Button("", () => _ = InvokeAsync(_secondaryAction));
        foreach (var button in new[] { _primary, _secondary }) { button.Classes.Add("ac"); button.Classes.Add("text-action"); }
        Children.Add(_leading); Grid.SetColumn(_hint, 1); Children.Add(_hint);
        Actions.Children.Add(_additional); Actions.Children.Add(_secondary); Actions.Children.Add(_primary);
        Grid.SetColumn(Actions, 2); Children.Add(Actions);
    }
    public void Update(TaskPageViewModel page)
    {
        _primaryAction = page.PrimaryAction; _secondaryAction = page.SecondaryAction;
        _hint.Text = page.Hint; _leading.Content = page.LeadingContent;
        if (!_additional.Children.SequenceEqual(page.AdditionalActions))
        { _additional.Children.Clear(); foreach (var action in page.AdditionalActions) _additional.Children.Add(action); }
        Set(_primary, _primaryAction); Set(_secondary, _secondaryAction);
    }
    private void Set(Button button, PageAction? action)
    { button.IsVisible = action is not null; button.Content = Localization.Text(action?.Title ?? ""); button.IsEnabled = !_busy && action?.IsEnabled == true; }
    private async Task InvokeAsync(PageAction? action)
    {
        if (_busy || action?.IsEnabled != true) return;
        _busy = true; Set(_primary, _primaryAction); Set(_secondary, _secondaryAction);
        try { await action.Execute(); }
        catch (Exception error) { _hint.Text = error.Message; }
        finally { _busy = false; Set(_primary, _primaryAction); Set(_secondary, _secondaryAction); }
    }
}

/// <summary>One task, one header, one page presenter and one action bar. Viewing never changes the execution cursor.</summary>
internal class TaskLifecycleWindow : Window
{
    protected MultiStageTaskShell Shell { get; } = new();
    protected PageActionBar ActionBar { get; } = new();
    private readonly SegmentedControl _tabs = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ContentPresenter _presenter = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly Dictionary<string, TaskPageViewModel> _views = [];
    private ITaskLifecycle? _lifecycle;
    private string? _currentId, _viewedId;
    private bool _syncing;
    private bool _closed;
    public event EventHandler? ViewedPageChanged;
    protected string? ViewedPageId => _viewedId;
    protected ITaskLifecycle? Lifecycle => _lifecycle;

    public TaskLifecycleWindow()
    {
        Classes.Add("multistage-task-window");
        Styles.Add(new StyleInclude(new Uri("avares://AvaMedia.Desktop/")) { Source = new("avares://AvaMedia.Desktop/Styles/MultiStageStyles.axaml") });
        Width = 1240; Height = 800; MinWidth = 1000; MinHeight = 680;
        Bind(BackgroundProperty, new DynamicResourceExtension("UiCanvas"));
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto") };
        Shell.Children.Remove(Shell.Header); root.Children.Add(Shell.Header);
        _tabs.Margin = new Thickness(20, 0, 20, 12);
        Avalonia.Automation.AutomationProperties.SetName(_tabs, Localization.Text("任务页"));
        Grid.SetRow(_tabs, 1); root.Children.Add(_tabs);
        _presenter.Bind(MarginProperty, new DynamicResourceExtension("AcMsBodyMargin"));
        Grid.SetRow(_presenter, 2); root.Children.Add(_presenter);
        Grid.SetRow(ActionBar, 3); root.Children.Add(ActionBar);
        Content = WindowChrome.WrapDialog(this, root);
        _tabs.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _tabs.SelectedIndex >= 0 && _lifecycle is { } lifecycle)
                ShowPage(lifecycle.Pages[_tabs.SelectedIndex].Page.Id);
        };
        Closed += (_, _) => { _closed = true; if (_lifecycle is not null) _lifecycle.Changed -= LifecycleChanged; };
    }
    protected void SetLifecycle(ITaskLifecycle lifecycle, IEnumerable<TaskPageViewModel> views)
    {
        if (_lifecycle is not null) _lifecycle.Changed -= LifecycleChanged;
        _lifecycle = lifecycle; _views.Clear();
        foreach (var view in views) _views.Add(view.PageId, view);
        _presenter.DataTemplates.Clear();
        foreach (var view in _views.Values)
            _presenter.DataTemplates.Add(new FuncDataTemplate<TaskPageViewModel>(item => item?.PageId == view.PageId,
                (item, _) => item!.View ??= item.CreateView()));
        _currentId = _viewedId = null;
        lifecycle.Changed += LifecycleChanged; RefreshLifecycle();
    }
    private void LifecycleChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshLifecycle();
        else Dispatcher.UIThread.Post(() => { if (!_closed) RefreshLifecycle(); });
    }
    protected void RefreshLifecycle()
    {
        if (_closed || _lifecycle is not { } lifecycle) return;
        _syncing = true;
        try
        {
            _tabs.Items.Clear();
            foreach (var (page, index) in lifecycle.Pages.Select((page, index) => (page, index)))
            {
                var glyph = new StageGlyph { Number = index + 1, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center,
                    State = page.State switch { PageState.Running => "active", PageState.AwaitingUser => "awaiting", PageState.Completed => "done", PageState.Failed => "failed", _ => "pending" } };
                _tabs.Items.Add(new ListBoxItem { IsEnabled = page.State != PageState.Pending || page.Page.Id == lifecycle.CurrentPage.Page.Id,
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { glyph, new TextBlock { Text = Localization.Text(page.Page.Title) } } } });
            }
            if (_currentId != lifecycle.CurrentPage.Page.Id) { _currentId = lifecycle.CurrentPage.Page.Id; _viewedId = _currentId; }
            ShowPage(_viewedId ?? _currentId!);
        }
        finally { _syncing = false; }
    }
    public void ShowCurrentPage() { if (_lifecycle is not null) ShowPage(_lifecycle.CurrentPage.Page.Id); }
    protected void ShowPage(string id)
    {
        if (_lifecycle is null || !_views.TryGetValue(id, out var view)) return;
        var index = _lifecycle.Pages.ToList().FindIndex(p => p.Page.Id == id);
        if (index < 0 || _lifecycle.Pages[index].State == PageState.Pending && id != _lifecycle.CurrentPage.Page.Id) return;
        _viewedId = id;
        var syncing = _syncing; _syncing = true;
        try { _tabs.SelectedIndex = index; } finally { _syncing = syncing; }
        view.IsReadOnly = _lifecycle.Pages[index].State == PageState.Completed && _lifecycle.Pages[index].Page.Kind == PageKind.Interaction;
        _presenter.Content = view;
        ActionBar.Update(view); ViewedPageChanged?.Invoke(this, EventArgs.Empty);
    }
    protected void RefreshPageActions()
    { if (_viewedId is not null && _views.TryGetValue(_viewedId, out var view)) ActionBar.Update(view); }
}
