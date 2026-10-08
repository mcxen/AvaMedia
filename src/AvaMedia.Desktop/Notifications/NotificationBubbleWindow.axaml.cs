using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AvaMedia.Desktop.Notifications;

internal sealed partial class NotificationBubbleWindow : Window
{
    private readonly NotificationCenter _center;
    private string? _actionSignature, _selectedKey;
    private bool _refreshing, _closed;
    private readonly DispatcherTimer _actionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    public NotificationBubbleWindow(NotificationCenter center)
    {
        _center = center; InitializeComponent();
        Localization.SetIsUserText(MessageText, true); Localization.SetIsUserText(Timestamp, true); Localization.SetIsUserText(Badge, true);
        HistoryList.ItemTemplate = new FuncDataTemplate<NotificationEntry>((entry, _) =>
        {
            if (entry is null) return new Border();
            var row = new StackPanel { Spacing = 4, Margin = new(4, 6) };
            var title = Ui.Text(NotificationCenter.Text(entry.Message.Title)); title.FontWeight = entry.Read ? FontWeight.Normal : FontWeight.SemiBold;
            row.Children.Add(title); row.Children.Add(Ui.Text(entry.Created.ToString("HH:mm"), "caption")); return row;
        });
        _center.Changed += Refresh; Localization.Changed += LanguageChanged;
        ActualThemeVariantChanged += (_, _) => ApplySkinClasses();
        Opened += (_, _) => { ApplySkinClasses(); Refresh(); _actionTimer.Start(); PositionBubble(); };
        _actionTimer.Tick += (_, _) => RefreshActions();
        SizeChanged += (_, _) => PositionBubble(); Screens.Changed += ScreensChanged;
        KeyDown += (_, args) => { if (args.Key == Key.Escape) { if (_center.History) _center.Collapse(); else _center.DismissSelected(); args.Handled = true; } };
        PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty) { if (IsVisible) _actionTimer.Start(); else _actionTimer.Stop(); } };
        Closed += (_, _) =>
        {
            _closed = true; _actionTimer.Stop(); _center.Changed -= Refresh; Localization.Changed -= LanguageChanged; Screens.Changed -= ScreensChanged;
        };
    }
    private void ApplySkinClasses()
    { Classes.Set("mac-os9", ActualThemeVariant == Skin.MacOS9); Classes.Set("windows-xp", ActualThemeVariant == Skin.WindowsXP); }
    private void LanguageChanged(object? sender, EventArgs args) { _actionSignature = null; Refresh(); }
    private void ScreensChanged(object? sender, EventArgs args) => PositionBubble();
    private void PositionBubble()
    {
        if (_closed) return;
        var screen = _center.Anchor is { } owner && NotificationCenter.Available(owner) ? Screens.ScreenFromWindow(owner) : Screens.ScreenFromWindow(this);
        screen ??= Screens.Primary; if (screen is null) return;
        var scale = screen.Scaling; var area = screen.WorkingArea;
        Width = _center.Expanded ? Math.Min(364, Math.Max(280, area.Width / scale - 32)) : 80;
        MaxHeight = Math.Max(160, area.Height / scale - 32);
        var height = Bounds.Height > 0 ? Bounds.Height : 100;
        Position = new(Math.Max(area.X, area.Right - (int)Math.Ceiling(Width * scale) - 16),
            Math.Max(area.Y, area.Bottom - (int)Math.Ceiling(height * scale) - 16));
    }
    private void Refresh()
    {
        if (_closed) return;
        _refreshing = true;
        try
        {
            Bubble.IsVisible = Tail.IsVisible = _center.Expanded;
            var unread = _center.Entries.Count(item => !item.Read);
            Badge.Text = (unread > 0 ? unread : _center.Entries.Count).ToString();
            MessageScroll.IsVisible = !_center.History; HistoryList.IsVisible = _center.History;
            Previous.IsVisible = Next.IsVisible = !_center.History && _center.Entries.Count > 1;
            HistoryButton.Content = Localization.Text(_center.History ? "收起" : "所有通知");
            var selected = _center.Selected;
            if (_center.History)
            {
                Heading.Classes.Remove("error");
                Heading.Text = Localization.Text("通知中心"); Timestamp.Text = "";
                HistoryList.ItemsSource = _center.Entries.ToArray(); HistoryList.SelectedItem = null;
                Counter.Text = Localization.Format($"{_center.Entries.Count} 条通知");
                ProgressPanel.IsVisible = false;
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
            RefreshActions(); PositionBubble();
        }
        finally { _refreshing = false; }
    }
    private void RefreshActions()
    {
        var entry = _center.Selected;
        var actions = _center.History || entry is null ? [] : entry.Message.Actions ?? [];
        var signature = string.Join("|", actions.Select(action => NotificationCenter.Text(action.Label) + action.Primary +
            (entry is { Busy: false } && action.Enabled?.Invoke() != false)));
        if (signature == _actionSignature) return;
        _actionSignature = signature; Actions.Children.Clear();
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index]; var actionIndex = index;
            var button = new Button { Content = Localization.Text(action.Label), Classes = { "bubble-action" }, Margin = new(0, 0, 8, 4),
                IsEnabled = entry is { Busy: false } && action.Enabled?.Invoke() != false };
            if (action.Primary) button.Classes.Add("primary");
            button.Click += async (_, _) => { if (_center.Selected is { } current) await _center.InvokeAsync(current, actionIndex); };
            Actions.Children.Add(button);
        }
    }
    private void DismissClick(object? sender, RoutedEventArgs args) { if (_center.History) _center.Collapse(); else _center.DismissSelected(); }
    private void PreviousClick(object? sender, RoutedEventArgs args) => _center.Step(-1);
    private void NextClick(object? sender, RoutedEventArgs args) => _center.Step(1);
    private void HistoryClick(object? sender, RoutedEventArgs args) { if (_center.History) _center.Collapse(); else _center.OpenHistory(); }
    private void LauncherClick(object? sender, RoutedEventArgs args)
    { if (!_center.Expanded) _center.OpenHistory(); else _center.Collapse(); }
    private void HideClick(object? sender, RoutedEventArgs args) => _center.HideAll();
    private void ClearClick(object? sender, RoutedEventArgs args) => _center.ClearHistory();
    private void HistorySelectionChanged(object? sender, SelectionChangedEventArgs args)
    { if (!_refreshing && HistoryList.SelectedItem is NotificationEntry entry) _center.Select(entry); }
}
