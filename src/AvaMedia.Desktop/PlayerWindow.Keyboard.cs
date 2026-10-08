using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AvaMedia.Desktop;

public partial class PlayerWindow
{
    private readonly HashSet<Key> _pressedKeys = [];
    private bool _keyboardNavigation;
    private ContextMenu? _openMenu;

    private void InitializeKeyboard()
    {
        AddHandler(KeyDownEvent, KeyPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, KeyReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => _keyboardNavigation = false, RoutingStrategies.Tunnel);
        AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional) _keyboardNavigation = true;
            ShowChrome();
        });
        Deactivated += (_, _) => _pressedKeys.Clear();
    }

    private void KeyReleased(object? sender, KeyEventArgs e) => _pressedKeys.Remove(e.Key);

    private void KeyPressed(object? sender, KeyEventArgs e)
    {
        var repeated = !_pressedKeys.Add(e.Key);
        _keyboardNavigation = true;
        ShowChrome(); // Reveal the controls before Tab navigation chooses its next target.
        var source = e.Source as Control ?? FocusManager?.GetFocusedElement() as Control;
        if (_openMenu?.IsOpen == true || Within<TextBox>(source) || Within<ComboBox>(source)
            || Within<NumericUpDown>(source) || Within<MenuItem>(source)) return;

        if (Within<ListBox>(source))
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                if (!repeated) CommandReady = HandleKeyboardAsync(PlaySelectedFileAsync);
                return;
            }
            if (IsNavigationKey(e.Key) || e.Key is Key.Space or Key.Delete
                || e.KeyModifiers == KeyModifiers.None && e.Key >= Key.A && e.Key <= Key.Z) return;
        }
        if (Within<Slider>(source) && IsNavigationKey(e.Key) && e.KeyModifiers == KeyModifiers.None) return;
        if (Within<Button>(source) && e.Key is (Key.Space or Key.Enter) && e.KeyModifiers == KeyModifiers.None)
        {
            if (repeated) e.Handled = true;
            return;
        }
        if (PlayerShortcuts.Resolve(e.Key, e.KeyModifiers) is not { } command) return;
        e.Handled = true;
        if (repeated && !PlayerShortcuts.CanRepeat(command)) return;
        CommandReady = HandleKeyboardAsync(() => ExecuteAsync(command));
    }

    private static bool Within<T>(Control? source) where T : Control
        => source is T || source?.GetVisualAncestors().Any(a => a is T) == true;

    private static bool IsNavigationKey(Key key)
        => key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private async Task HandleKeyboardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) Notice(ex.Message); }
    }

    private Task PlaySelectedFileAsync()
    {
        var index = PlaylistList.SelectedIndex;
        if (index < 0 || index >= _playlist.Length || _deleting) return Task.CompletedTask;
        VideoArea.Focus();
        return OpenAsync(_playlist[index]);
    }

    private void DismissPlayerOverlay()
    {
        if (ShortcutHelp.IsVisible) ShortcutHelp.IsVisible = false;
        else if (VrPanel.IsVisible) { ToggleVrPanel(); return; }
        else if (PlaylistPanel.IsVisible) { TogglePlaylist(); return; }
        else if (WindowState == WindowState.FullScreen) ToggleFullscreen();
        VideoArea.Focus();
        ShowChrome();
    }

    private void HideChrome()
    {
        _chromeTimer.Stop();
        if (WindowState != WindowState.FullScreen || ShortcutHelp.IsVisible || PlaylistPanel.IsVisible || VrPanel.IsVisible || PanoramaImage.IsDragging
            || _openMenu?.IsOpen == true || _keyboardNavigation && ControlsBar.IsKeyboardFocusWithin) return;
        if (ControlsBar.IsKeyboardFocusWithin) VideoArea.Focus(NavigationMethod.Pointer);
        _chromeTimer.Stop();
        ControlsBar.IsVisible = false;
        Cursor = new(StandardCursorType.None);
    }

    private void OpenMenu(ContextMenu menu, Control target)
    {
        _openMenu?.Close();
        _openMenu = menu;
        menu.AddHandler(KeyDownEvent, (_, e) => _pressedKeys.Add(e.Key), RoutingStrategies.Tunnel, handledEventsToo: true);
        menu.AddHandler(KeyUpEvent, KeyReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        menu.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_openMenu, menu)) return;
            _openMenu = null;
            // The popup finishes restoring focus after Closed has been raised.
            Dispatcher.UIThread.Post(() => { if (!_closed) ShowChrome(); });
        };
        ShowChrome();
        menu.Open(target);
    }

    private async Task StepFrameAsync(int direction)
    {
        if (_info is not { HasVideo: true } info || _player is not { } player) return;
        CancelFrameStep(); _seek?.Cancel(); _seekGeneration++; _pendingSeek = true;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _frameStep = request;
        var revision = _revision;
        _playIntent = false; player.Pause(); RefreshTransport();
        try
        {
            var frame = await _engine.AdjacentFrameTime(CurrentPath, _position, direction, request.Token, info.VideoStreamIndex);
            request.Token.ThrowIfCancellationRequested();
            if (Current(revision)) await SeekAsync(frame, false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_frameStep, request))
            {
                _frameStep = null; _pendingSeek = false;
                if (!_closed) RefreshCapture();
            }
        }
    }

    private void CancelFrameStep()
    {
        _frameStep?.Cancel();
        _frameStep = null;
    }
}
