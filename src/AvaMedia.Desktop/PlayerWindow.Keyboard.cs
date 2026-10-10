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
    private IPointer? _chromePointer;
    private Avalonia.Point? _lastPointerPosition;
    private Control? _pointerControl;
    private int _menuGeneration;
    private ComboBox[] _playbackSelectors = [];

    private void InitializeKeyboard()
    {
        AddHandler(KeyDownEvent, KeyPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, KeyReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            _chromePointer = e.Pointer;
            _keyboardNavigation = false;
            var source = e.Source as Control;
            _pointerControl = IsPlaybackControl(source) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                ? (Control?)ControlWithin<Button>(source) ?? ControlWithin<Slider>(source) : null;
            ShowChrome();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (_pointerControl is { } control) RestorePointerFocus(control);
            _pointerControl = null;
            ShowChrome();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, e) =>
        {
            // Popup focus restoration can report Tab even when the user selected with the mouse.
            if (e.NavigationMethod == NavigationMethod.Pointer) _keyboardNavigation = false;
            else if (e.NavigationMethod == NavigationMethod.Directional) _keyboardNavigation = true;
            ShowChrome();
        });
        AddHandler(LostFocusEvent, (_, _) => Dispatcher.UIThread.Post(RestoreMissingFocus), handledEventsToo: true);
        _playbackSelectors = [PlayerVrMode, PlayerVrLayout, PlayerVrEye, PlayerVrProjection];
        foreach (var combo in _playbackSelectors)
            combo.DropDownClosed += (_, _) => RestorePointerFocus(combo);
        Activated += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var focused = FocusManager?.GetFocusedElement() as Control;
            RestoreMissingFocus();
            if (CanRestorePlaybackFocus && !_keyboardNavigation && IsPlaybackControl(focused)
                && (Within<Button>(focused) || Within<Slider>(focused) || Within<ComboBox>(focused))) FocusPlayback();
        });
        Deactivated += (_, _) => _pressedKeys.Clear();
    }

    private void KeyReleased(object? sender, KeyEventArgs e) => _pressedKeys.Remove(e.Key);

    private void KeyPressed(object? sender, KeyEventArgs e)
    {
        var source = e.Source as Control ?? FocusManager?.GetFocusedElement() as Control;
        if (!IsEnabled || Within<Notifications.NotificationPanel>(source)) return;
        if (e.Key == Key.Tab) _keyboardNavigation = true;
        ShowChrome(); // Reveal the controls before Tab navigation chooses its next target.
        if (_openMenu?.IsOpen == true || Within<MenuItem>(source) || _playbackSelectors.Any(combo => combo.IsDropDownOpen)) return;
        var resolved = PlayerShortcuts.Resolve(e.Key, e.KeyModifiers);
        // Panel and window commands remain available from closed selectors and editable controls.
        if (resolved is PlayerCommand.CaptureFrame or PlayerCommand.ExitFullscreen or PlayerCommand.Open
            or PlayerCommand.Stop or PlayerCommand.Help or PlayerCommand.Playlist or PlayerCommand.Settings
            || resolved == PlayerCommand.ToggleFullscreen && (e.Key == Key.F11 || e.KeyModifiers == KeyModifiers.Alt))
        {
            ExecuteKeyboardCommand(e, resolved.Value);
            return;
        }
        if (Within<TextBox>(source) || Within<NumericUpDown>(source)) return;
        if (Within<ComboBox>(source) && e.KeyModifiers == KeyModifiers.None
            && (IsNavigationKey(e.Key) || _keyboardNavigation && e.Key is (Key.Space or Key.Enter))) return;

        if (Within<ListBox>(source))
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                if (_pressedKeys.Add(e.Key)) CommandReady = HandleKeyboardAsync(PlaySelectedFileAsync);
                return;
            }
            if (e.KeyModifiers == KeyModifiers.None && e.Key is (Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Delete)) return;
        }
        if (_keyboardNavigation && Within<Slider>(source) && IsNavigationKey(e.Key) && e.KeyModifiers == KeyModifiers.None) return;
        if (_keyboardNavigation && Within<Button>(source) && e.Key is (Key.Space or Key.Enter) && e.KeyModifiers == KeyModifiers.None)
        {
            if (!_pressedKeys.Add(e.Key)) e.Handled = true;
            return;
        }
        if (resolved is not { } command) return;
        ExecuteKeyboardCommand(e, command);
    }

    private void ExecuteKeyboardCommand(KeyEventArgs e, PlayerCommand command)
    {
        e.Handled = true;
        var repeated = !_pressedKeys.Add(e.Key);
        if (repeated && !PlayerShortcuts.CanRepeat(command)) return;
        if (command is PlayerCommand.Playlist or PlayerCommand.VrSettings or PlayerCommand.Settings) _keyboardNavigation = true;
        CommandReady = HandleKeyboardAsync(() => ExecuteAsync(command));
    }

    private static T? ControlWithin<T>(Control? source) where T : Control
        => source as T ?? source?.GetVisualAncestors().OfType<T>().FirstOrDefault();

    private static bool Within<T>(Control? source) where T : Control
        => ControlWithin<T>(source) is not null;

    private bool IsPlaybackControl(Control? source)
        => source is not null && source.GetVisualAncestors().Prepend(source)
            .Any(control => control == ControlsBar || control == HeaderBar || control == PlaylistPanel || control == VrPanel);

    private void FocusPlayback()
    {
        _keyboardNavigation = false;
        VideoArea.Focus(NavigationMethod.Pointer);
    }

    private bool CanRestorePlaybackFocus => !_closed && IsActive && IsEnabled && _openMenu?.IsOpen != true
        && !_playbackSelectors.Any(combo => combo.IsDropDownOpen);

    private void RestoreMissingFocus()
    {
        if (!CanRestorePlaybackFocus) return;
        var focused = FocusManager?.GetFocusedElement() as Control;
        if (focused is null || !focused.IsEffectivelyVisible || !focused.IsEffectivelyEnabled) FocusPlayback();
    }

    private void RestorePointerFocus(Control control)
    {
        if (_keyboardNavigation) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!CanRestorePlaybackFocus || _keyboardNavigation) return;
            var focused = FocusManager?.GetFocusedElement() as Control;
            if (focused == control || focused?.GetVisualAncestors().Contains(control) == true) FocusPlayback();
        });
    }

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
        FocusPlayback();
        return OpenAsync(_playlist[index]);
    }

    private void DismissPlayerOverlay()
    {
        if (ShortcutHelp.IsVisible) ShortcutHelp.IsVisible = false;
        else if (VrPanel.IsVisible) { ToggleVrPanel(); return; }
        else if (PlaylistPanel.IsVisible) { TogglePlaylist(); return; }
        else if (WindowState == WindowState.FullScreen) ToggleFullscreen();
        FocusPlayback();
        ShowChrome();
    }

    private void HideChrome()
    {
        if (WindowState != WindowState.FullScreen) { _chromeTimer.Stop(); return; }
        var captured = _chromePointer?.Captured as Control;
        if (ShortcutHelp.IsVisible || PlaylistPanel.IsVisible || VrPanel.IsVisible || PanoramaImage.IsDragging
            || ControlsBar.IsPointerOver || captured == ControlsBar || captured?.GetVisualAncestors().Contains(ControlsBar) == true
            || Within<Notifications.NotificationPanel>(FocusManager?.GetFocusedElement() as Control)
            || _openMenu?.IsOpen == true || _keyboardNavigation && ControlsBar.IsKeyboardFocusWithin) return;
        if (ControlsBar.IsKeyboardFocusWithin) FocusPlayback();
        _chromeTimer.Stop();
        ControlsBar.IsVisible = false;
        Cursor = new(StandardCursorType.None);
    }

    private void PlayerPointerMoved(object? sender, PointerEventArgs e)
    {
        _chromePointer = e.Pointer;
        var position = e.GetPosition(this);
        if (_lastPointerPosition == position) return;
        _lastPointerPosition = position;
        ShowChrome();
    }

    private void OpenMenu(ContextMenu menu, Control target)
    {
        _openMenu?.Close();
        var generation = ++_menuGeneration;
        _openMenu = menu;
        menu.AddHandler(KeyUpEvent, KeyReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        menu.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_openMenu, menu)) return;
            _openMenu = null;
            // The popup finishes restoring focus after Closed has been raised.
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || !IsActive || !IsEnabled || generation != _menuGeneration || _openMenu is not null) return;
                var focused = FocusManager?.GetFocusedElement() as Control;
                if (focused == target || Within<MenuItem>(focused))
                {
                    FocusPlayback();
                }
                ShowChrome();
            });
        };
        ShowChrome();
        menu.Open(target);
    }

    private async Task StepFrameAsync(int direction)
    {
        if (_info is not { HasVideo: true } info || _player is not { } player) return;
        CancelFrameStep(); _seek.Cancel(); _pendingSeek = true;
        var token = _frameStep.Restart(_lifetime.Token);
        var revision = _revision;
        _playIntent = false; player.Pause(); RefreshTransport();
        try
        {
            var frame = await _engine.AdjacentFrameTime(CurrentPath, _position, direction, token, info.VideoStreamIndex);
            token.ThrowIfCancellationRequested();
            if (Current(revision)) await SeekAsync(frame, false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_frameStep.IsCurrent(token))
            {
                _frameStep.Cancel(); _pendingSeek = false;
                if (!_closed) RefreshCapture();
            }
        }
    }

    private void CancelFrameStep() => _frameStep.Cancel();
}
