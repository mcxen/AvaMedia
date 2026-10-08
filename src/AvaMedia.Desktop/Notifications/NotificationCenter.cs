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
    public bool Busy { get; set; }
}

/// <summary>One application-owned stream. Cancellation on dismiss must be explicitly configured by its source.</summary>
internal sealed class NotificationCenter
{
    public static NotificationCenter Shared { get; } = new();
    private readonly List<NotificationEntry> _entries = [];
    private NotificationBubbleWindow? _window;
    private WeakReference<Window>? _anchor;
    private bool _stopped;
    public IReadOnlyList<NotificationEntry> Entries => _entries;
    public NotificationEntry? Selected { get; private set; }
    public bool Expanded { get; private set; }
    public bool History { get; private set; }
    public event Action? Changed;
    public Window? Anchor => _anchor?.TryGetTarget(out var window) == true ? window : null;
    public static string Text(object value) => Localization.RenderContent(value);

    public void Publish(Window? owner, NotificationMessage message, bool show = true, bool reopen = false, bool select = false)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Publish(owner, message, show, reopen, select)); return; }
        if (_stopped) return;
        if (owner is not null) _anchor = new(owner);
        var entry = _entries.FirstOrDefault(item => item.Message.Key == message.Key);
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
        else entry.Message = message;
        if (reopen) { entry.Dismissed = false; entry.Read = false; }
        if (show && !entry.Dismissed)
        {
            if (select || !Expanded || Selected is null) { Selected = entry; Expanded = true; History = false; entry.Read = true; }
            EnsureWindow();
        }
        Changed?.Invoke();
    }

    private void EnsureWindow()
    {
        if (_window is null)
        {
            _window = new(this);
            _window.Closed += (_, _) => _window = null;
        }
        if (!_window.IsVisible) _window.Show();
    }
    public void OpenHistory(Window? owner = null)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OpenHistory(owner)); return; }
        if (_stopped) return;
        if (owner is not null) _anchor = new(owner);
        Expanded = History = true; EnsureWindow(); Changed?.Invoke(); _window?.Activate();
    }
    public void Select(NotificationEntry entry)
    { Selected = entry; entry.Read = true; Expanded = true; History = false; Changed?.Invoke(); }
    public void Step(int direction)
    {
        if (_entries.Count == 0) return;
        var index = Selected is null ? 0 : _entries.IndexOf(Selected);
        Select(_entries[(index + direction + _entries.Count) % _entries.Count]);
    }
    public void DismissSelected()
    {
        if (Selected is { } selected) Dismiss(selected);
        Selected = _entries.FirstOrDefault(item => !item.Dismissed && item != Selected);
        Expanded = Selected is not null; History = false;
        if (Selected is { } next) next.Read = true;
        Changed?.Invoke();
    }
    public void Collapse() { Expanded = History = false; Changed?.Invoke(); }
    public void HideAll()
    {
        foreach (var item in _entries.ToArray()) Dismiss(item);
        Expanded = History = false; _window?.Hide(); Changed?.Invoke();
    }
    public void ClearHistory()
    {
        foreach (var item in _entries.Where(item => !IsActive(item)).ToArray()) Dismiss(item);
        _entries.RemoveAll(item => !IsActive(item));
        if (Selected is not null && !_entries.Contains(Selected)) Selected = null;
        Changed?.Invoke();
    }
    public async Task InvokeAsync(NotificationEntry entry, int index)
    {
        if (entry.Busy || index < 0 || index >= (entry.Message.Actions?.Count ?? 0)) return;
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
    { _stopped = true; _window?.Close(); _entries.Clear(); Selected = null; }

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
