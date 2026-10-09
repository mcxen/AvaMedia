using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace AvaMedia.Desktop.Notifications;

public sealed partial class NotificationPanel : UserControl
{
    private readonly NotificationCenter _center = NotificationCenter.Shared;
    private string? _actionSignature, _selectedKey;
    private Window? _owner;
    private bool _refreshing, _attached;
    private readonly DispatcherTimer _actionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _autoCloseTimer = new();
    private int _autoCloseVersion = -1;
    public NotificationPanel()
    {
        InitializeComponent();
        Localization.SetIsUserText(MessageText, true); Localization.SetIsUserText(Timestamp, true);
        HistoryList.ItemTemplate = new FuncDataTemplate<NotificationEntry>((entry, _) =>
        {
            if (entry is null) return new Border();
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Margin = new(4, 6) };
            var labels = new StackPanel { Spacing = 4 };
            var title = Ui.Text(NotificationCenter.Text(entry.Message.Title)); title.FontWeight = entry.Read ? FontWeight.Normal : FontWeight.SemiBold;
            labels.Children.Add(title); labels.Children.Add(Ui.Text(entry.Created.ToString("HH:mm"), "caption")); row.Children.Add(labels);
            var remove = new Button { Content = Localization.Text("移除"), Classes = { "tool", "notification-action" } };
            AutomationProperties.SetName(remove, Localization.Text("移除通知"));
            remove.Click += (_, args) => { args.Handled = true; _center.Remove(entry); };
            Grid.SetColumn(remove, 1); row.Children.Add(remove); return row;
        });
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true; _center.Changed += Refresh; Localization.Changed += LanguageChanged;
            _owner = TopLevel.GetTopLevel(this) as Window;
            if (_owner is not null) _owner.PropertyChanged += HostStateChanged;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false; _actionTimer.Stop(); _autoCloseTimer.Stop(); _center.Changed -= Refresh; Localization.Changed -= LanguageChanged;
            if (_owner is not null) _owner.PropertyChanged -= HostStateChanged;
            _owner = null;
        };
        _actionTimer.Tick += (_, _) => RefreshActions();
        _autoCloseTimer.Tick += (_, _) => { _autoCloseTimer.Stop(); _center.AutoCollapse(); };
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty) RefreshActionTimer();
            if (args.Property == IsVisibleProperty || args.Property == IsPointerOverProperty || args.Property == IsKeyboardFocusWithinProperty)
                RefreshAutoCloseTimer();
        };
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { ClosePanel(); args.Handled = true; }
        };
        Refresh();
    }
    private void RefreshActionTimer()
    { if (_attached && IsVisible && _owner is { IsVisible: true, WindowState: not WindowState.Minimized }) _actionTimer.Start(); else _actionTimer.Stop(); }
    private void RefreshAutoCloseTimer()
    {
        if (_autoCloseVersion != _center.AutoCloseVersion)
        {
            _autoCloseVersion = _center.AutoCloseVersion; _autoCloseTimer.Stop();
            _autoCloseTimer.Interval = TimeSpan.FromSeconds(_center.Selected?.Message.Kind is NotificationKind.Error or NotificationKind.Warning ? 12 : 8);
        }
        if (!_attached || !IsVisible || !_center.CanAutoClose || IsPointerOver || IsKeyboardFocusWithin
            || _owner is not { IsVisible: true, WindowState: not WindowState.Minimized }) _autoCloseTimer.Stop();
        else if (!_autoCloseTimer.IsEnabled) _autoCloseTimer.Start();
    }
    private void HostStateChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property != IsVisibleProperty && args.Property != Window.WindowStateProperty) return;
        RefreshActionTimer(); RefreshAutoCloseTimer();
    }
    private void LanguageChanged(object? sender, EventArgs args) { _actionSignature = null; Refresh(); }
    private void Refresh()
    {
        _refreshing = true;
        try
        {
            IsVisible = _center.Expanded;
            MessageScroll.IsVisible = !_center.History; HistoryList.IsVisible = _center.History && _center.Entries.Count > 0;
            EmptyText.IsVisible = _center.History && _center.Entries.Count == 0;
            Previous.IsVisible = Next.IsVisible = !_center.History && _center.Entries.Count > 1;
            HistoryButton.Content = Localization.Text(_center.History ? "收起" : "所有通知");
            RemoveButton.IsVisible = !_center.History && _center.Selected is not null;
            ClearButton.IsEnabled = _center.Entries.Count > 0;
            var selected = _center.Selected;
            if (_center.History)
            {
                Heading.Classes.Remove("error"); Heading.Text = Localization.Text("通知中心"); Timestamp.Text = "";
                HistoryList.ItemsSource = _center.Entries.ToArray(); HistoryList.SelectedItem = null;
                Counter.Text = Localization.Format($"{_center.Entries.Count} 条通知"); ProgressPanel.IsVisible = false;
            }
            else if (selected is not null)
            {
                var message = selected.Message; Heading.Text = NotificationCenter.Text(message.Title); Timestamp.Text = selected.Created.ToString("HH:mm");
                var text = NotificationCenter.Text(message.Body); if (MessageText.Text != text) MessageText.Text = text;
                if (_selectedKey != message.Key) { _selectedKey = message.Key; MessageScroll.Offset = default; }
                Heading.Classes.Set("error", message.Kind == NotificationKind.Error);
                ProgressPanel.IsVisible = message.Progress is not null || message.Indeterminate;
                ProgressBar.IsIndeterminate = message.Indeterminate; ProgressBar.Value = Math.Clamp(message.Progress ?? 0, 0, 100);
                ProgressText.Text = message.Progress is { } percent ? $"{percent:0}%" : "";
                Counter.Text = $"{_center.Entries.ToList().IndexOf(selected) + 1} / {_center.Entries.Count}";
            }
            RefreshActions(); RefreshActionTimer(); RefreshAutoCloseTimer();
        }
        finally { _refreshing = false; }
    }
    private void RefreshActions()
    {
        var entry = _center.Selected;
        var actions = _center.History || entry is null ? [] : entry.Message.Actions ?? [];
        var signature = entry?.Message.Key + "|" + string.Join("|", actions.Select(action => NotificationCenter.Text(action.Label) + action.Primary +
            (entry is { Busy: false } && action.Enabled?.Invoke() != false)));
        if (signature == _actionSignature) return;
        _actionSignature = signature; Actions.Children.Clear();
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index]; var actionIndex = index;
            var button = new Button { Content = Localization.Text(action.Label), Classes = { "notification-action" }, Margin = new(0, 0, 8, 4),
                IsEnabled = entry is { Busy: false } && action.Enabled?.Invoke() != false };
            if (action.Primary) button.Classes.Add("primary");
            button.Click += async (_, _) => { if (_center.Selected is { } current) await _center.InvokeAsync(current, actionIndex); };
            Actions.Children.Add(button);
        }
    }
    private void ClosePanel() { if (_center.History) _center.Collapse(); else _center.DismissSelected(); }
    private void DismissClick(object? sender, RoutedEventArgs args) => ClosePanel();
    private void RemoveClick(object? sender, RoutedEventArgs args) { if (_center.Selected is {} entry) _center.Remove(entry); }
    private void ClearClick(object? sender, RoutedEventArgs args) => _center.ClearAll();
    private void PreviousClick(object? sender, RoutedEventArgs args) => _center.Step(-1);
    private void NextClick(object? sender, RoutedEventArgs args) => _center.Step(1);
    private void HistoryClick(object? sender, RoutedEventArgs args) { if (_center.History) _center.Collapse(); else _center.OpenHistory(); }
    private void HistorySelectionChanged(object? sender, SelectionChangedEventArgs args)
    { if (!_refreshing && HistoryList.SelectedItem is NotificationEntry entry) _center.Select(entry); }
}
