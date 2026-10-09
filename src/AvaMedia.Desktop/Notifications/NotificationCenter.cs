using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AvaMedia.Desktop.Notifications;

internal enum NotificationKind { Information, Success, Warning, Error, Progress }
internal sealed record NotificationAction(string Label, Func<Task> Execute, bool Primary = false,
    bool DismissOnSuccess = false, Func<bool>? Enabled = null);
internal sealed record NotificationMessage(string Key, object Title, object Body, NotificationKind Kind = NotificationKind.Information,
    IReadOnlyList<NotificationAction>? Actions = null, double? Progress = null, bool Indeterminate = false, Action? OnDismiss = null, bool Active = false);
internal sealed class NotificationEntry(NotificationMessage message)
{
    public NotificationMessage Message { get; set; } = message;
    public DateTime Created { get; } = DateTime.Now;
    public bool Read { get; set; }
    public bool Dismissed { get; set; }
    public bool AutoHidden { get; set; }
    public bool Busy { get; set; }
}

/// <summary>One application-owned stream. Cancellation on dismiss must be explicitly configured by its source.</summary>
internal sealed class NotificationCenter
{
    public static NotificationCenter Shared { get; } = new();
    private readonly List<NotificationEntry> _entries = [];
    private readonly HashSet<string> _removedKeys = [];
    private WeakReference<Window>? _host;
    private WeakReference<Window>? _anchor;
    private bool _stopped, _manualPresentation;
    public IReadOnlyList<NotificationEntry> Entries => _entries;
    public NotificationEntry? Selected { get; private set; }
    public bool Expanded { get; private set; }
    public bool History { get; private set; }
    public int AutoCloseVersion { get; private set; }
    public bool CanAutoClose => Expanded && !History && !_manualPresentation
        && Selected is { Busy: false, Message: { Active: false } };
    public event Action? Changed;
    public Window? Anchor => _anchor?.TryGetTarget(out var window) == true ? window : null;
    public static string Text(object value) => Localization.RenderContent(value);

    public void Publish(Window? owner, NotificationMessage message, bool show = true, bool reopen = false, bool select = false)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Publish(owner, message, show, reopen, select)); return; }
        if (_stopped) return;
        if (_removedKeys.Contains(message.Key))
        {
            if (!reopen) return;
            _removedKeys.Remove(message.Key);
        }
        if (owner is not null) _anchor = new(owner);
        var entry = _entries.FirstOrDefault(item => item.Message.Key == message.Key);
        var added = entry is null;
        var phaseChanged = entry is not null && (entry.Message.Kind != message.Kind || entry.Message.Active != message.Active);
        if (entry is null)
        {
            entry = new(message); _entries.Insert(0, entry);
            // Keep active operations; remove the oldest completed history first.
            while (_entries.Count > 50)
            {
                var old = _entries.LastOrDefault(item => !IsActive(item) && item != Selected);
                if (old is null) break; _entries.Remove(old);
            }
        }
        else
        {
            entry.Message = message;
            if (phaseChanged) entry.AutoHidden = false;
        }
        if (phaseChanged && Selected == entry) AutoCloseVersion++;
        if (reopen) { entry.Dismissed = entry.AutoHidden = entry.Read = false; }
        if (show && !entry.Dismissed && !entry.AutoHidden)
        {
            EnsurePresentation();
            if (select || !Expanded || Selected is null
                || !_manualPresentation && !Selected.Message.Active && (added || reopen || phaseChanged))
            {
                Selected = entry; Expanded = true; History = _manualPresentation = false; AutoCloseVersion++;
                entry.Read = Host is { IsVisible: true, WindowState: not WindowState.Minimized };
            }
        }
        Changed?.Invoke();
    }

    internal void Attach(Window host) => _host = new(host);
    private Window? Host => _host?.TryGetTarget(out var window) == true ? window : null;
    private void EnsurePresentation()
    {
        if (Host is {} host && Available(host)) return;
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not {} primary) return;
        Attach(primary);
        // Insert into the application body without replacing the skin's owned window frame.
        if (primary is MainWindow) return;
        ContentControl body = primary.Content switch
        {
            Controls.PlatinumWindowFrame platinum => platinum.Body,
            Controls.WindowsXPWindowFrame xp => xp.Body,
            _ => primary
        };
        if (body.Content is not Control content) return;
        var notifications = new NotificationPanel { Width = 364, MaxHeight = 420, Margin = new(12),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
        if (content is Grid grid)
        {
            Grid.SetRowSpan(notifications, Math.Max(1, grid.RowDefinitions.Count));
            Grid.SetColumnSpan(notifications, Math.Max(1, grid.ColumnDefinitions.Count));
            grid.Children.Add(notifications);
        }
        else
        {
            body.Content = null;
            var root = new Grid(); root.Children.Add(content); root.Children.Add(notifications); body.Content = root;
        }
    }
    public void OpenHistory(Window? owner = null)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OpenHistory(owner)); return; }
        if (_stopped) return;
        if (owner is not null) _anchor = new(owner);
        EnsurePresentation(); Expanded = History = _manualPresentation = true; Changed?.Invoke();
        if (Host is MainWindow main) main.RestoreFromTray();
        else if (Host is {} host)
        {
            if (!host.IsVisible) host.Show();
            if (host.WindowState == WindowState.Minimized) host.WindowState = WindowState.Normal;
            host.Activate();
        }
    }
    public void Select(NotificationEntry entry)
    { if (!_entries.Contains(entry)) return; Selected = entry; entry.Read = true; Expanded = _manualPresentation = true; History = false; Changed?.Invoke(); }
    public void Step(int direction)
    {
        if (_entries.Count == 0) return;
        var index = Selected is null ? 0 : _entries.IndexOf(Selected);
        Select(_entries[(index + direction + _entries.Count) % _entries.Count]);
    }
    public void DismissSelected()
    {
        if (Selected is { } selected) Dismiss(selected);
        Collapse();
    }
    public void AutoCollapse()
    {
        if (CanAutoClose) Collapse();
    }
    public void Collapse()
    {
        Expanded = History = _manualPresentation = false;
        // Hide existing progress streams together so the next progress tick cannot reopen the panel.
        foreach (var entry in _entries) entry.AutoHidden = true;
        Changed?.Invoke();
    }
    public void Remove(NotificationEntry entry)
    {
        if (!_entries.Remove(entry)) return;
        _removedKeys.Add(entry.Message.Key); Dismiss(entry);
        if (Selected == entry) Selected = _entries.FirstOrDefault(item => !item.Dismissed);
        if (_entries.Count == 0) { Collapse(); return; }
        if (Selected is null) History = _manualPresentation = true;
        else if (Expanded && !History) Selected.Read = true;
        AutoCloseVersion++;
        Changed?.Invoke();
    }
    public void ClearAll()
    {
        var removed = _entries.ToArray();
        foreach (var entry in removed) _removedKeys.Add(entry.Message.Key);
        _entries.Clear(); Selected = null; History = Expanded = _manualPresentation = false;
        foreach (var entry in removed) Dismiss(entry);
        Changed?.Invoke();
    }
    public async Task InvokeAsync(NotificationEntry entry, int index)
    {
        if (!_entries.Contains(entry) || entry.Busy || index < 0 || index >= (entry.Message.Actions?.Count ?? 0)) return;
        var action = entry.Message.Actions![index];
        if (action.Enabled?.Invoke() == false) return;
        entry.Busy = true; Changed?.Invoke();
        try
        {
            await action.Execute();
            if (action.DismissOnSuccess)
            {
                entry.Dismissed = entry.Read = true;
                if (Selected == entry) Collapse();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        { Publish(Anchor, new(Guid.NewGuid().ToString("N"), "通知操作失败", error.Message, NotificationKind.Error)); }
        finally { entry.Busy = false; Changed?.Invoke(); }
    }
    public void Shutdown()
    { _stopped = true; Expanded = History = _manualPresentation = false; _entries.Clear(); _removedKeys.Clear(); Selected = null; _host = _anchor = null; Changed?.Invoke(); }

    private static void Dismiss(NotificationEntry entry)
    {
        var dismissed = entry.Dismissed; entry.Dismissed = entry.Read = true;
        if (!dismissed) entry.Message.OnDismiss?.Invoke();
    }
    private static bool IsActive(NotificationEntry entry) => entry.Busy || entry.Message.Active || entry.Message.Kind == NotificationKind.Progress;

    public static bool Available(Window owner) => Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
        && desktop.Windows.Contains(owner);
    public static Task ShowOwnerAsync(Window owner)
    {
        if (Available(owner))
        {
            if (!owner.IsVisible) { if (owner is MainWindow main) main.RestoreFromTray(); else owner.Show(); }
            owner.Activate();
        }
        return Task.CompletedTask;
    }
}
