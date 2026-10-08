using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class PlayerWindow
{
    private readonly Dictionary<string, PanoramaSettings> _sessionViews = new(VideoFolderScanner.PathComparer);
    private bool _vrUpdating;
    public PanoramaSettings Panorama => PanoramaImage.View;

    private void InitializePanorama()
    {
        PanoramaImage.ViewChanged += () =>
        {
            if (CurrentPath.Length > 0) _sessionViews[CurrentPath] = Panorama;
            if (VrPanel.IsVisible) SyncPanoramaControls();
        };
        PanoramaImage.RenderFailed += error =>
        {
            AppDiagnostics.Record("VR rendering", error);
            CommandReady = HandleKeyboardAsync(async () =>
            {
                await SetPanoramaAsync(Panorama with { Mode = PanoramaMode.Flat });
                if (!_closed) Notice(Localization.Format($"VR 显示失败：{error.Message}"));
            });
        };
        foreach (var combo in new[] { PlayerVrMode, PlayerVrLayout, PlayerVrEye, PlayerVrProjection })
            combo.SelectionChanged += (_, _) =>
            {
                if (_vrUpdating || combo.SelectedIndex < 0) return;
                CommandReady = HandleKeyboardAsync(() => SetPanoramaAsync(Panorama with
                {
                    Mode = (PanoramaMode)PlayerVrMode.SelectedIndex, Layout = (PanoramaLayout)PlayerVrLayout.SelectedIndex,
                    Eye = (PanoramaEye)PlayerVrEye.SelectedIndex, Projection = (PanoramaProjection)PlayerVrProjection.SelectedIndex
                }));
            };
        foreach (var slider in new[] { PlayerVrYaw, PlayerVrPitch, PlayerVrRoll, PlayerVrFov })
            slider.PropertyChanged += (_, change) =>
            {
                if (_vrUpdating || change.Property != Slider.ValueProperty) return;
                PanoramaImage.View = Panorama with { Yaw = PlayerVrYaw.Value, Pitch = PlayerVrPitch.Value,
                    Roll = PlayerVrRoll.Value, FieldOfView = PlayerVrFov.Value };
            };
        Deactivated += (_, _) => PanoramaImage.CancelDrag();
        SyncPanoramaControls();
    }

    public async Task SetPanoramaAsync(PanoramaSettings settings)
    {
        if (_closed || _deleting) return;
        var next = settings.Normalize();
        if (next.IsImmersive) Controls.PanoramaRenderer.Initialize();
        PanoramaImage.CancelDrag(); PanoramaImage.View = next;
        UpdatePanoramaVisibility(); SyncPanoramaControls();
        if (_nativeBusy && next.IsImmersive && _nativeDisc is null)
        {
            var path = CurrentPath;
            var playing = _nativeStarted ? _nativePlaying : _playIntent;
            _nativeCancellation?.Cancel();
            await _nativeLifetime;
            if (_closed || _deleting || Panorama != next || !VideoFolderScanner.PathComparer.Equals(CurrentPath, path)) return;
            if (_info is { HasVideo: true } nativeInfo)
                await StartOpen(path, nativeInfo.VideoStreamIndex, nativeInfo.AudioStreamIndex, _position, playing);
            return;
        }
        if (!_pendingSeek && VideoImage.Source is Bitmap frame) PresentFrame(frame);
        if (_player is Playback playback && next.IsImmersive && playback.MaximumVideoSize.Width < 4096)
        { PreparePanoramaPlayback(playback); await SeekAsync(_position, _playIntent); return; }
        if (next.IsImmersive && _player is null && _info is { HasVideo: true } info && CurrentPath.Length > 0)
            await StartOpen(CurrentPath, info.VideoStreamIndex, info.AudioStreamIndex, _position, _playIntent);
    }

    private void RestorePanorama(string path, bool video)
    {
        var view = new PanoramaSettings();
        try
        {
            if (video) view = _sessionViews.GetValueOrDefault(path) ?? _preferences.LoadPlayerView(path);
            if (view.IsImmersive) Controls.PanoramaRenderer.Initialize();
        }
        catch (Exception error) { AppDiagnostics.Record("VR settings", error); view = new(); Notice(error.Message); }
        PanoramaImage.View = view; UpdatePanoramaVisibility(); SyncPanoramaControls();
    }

    private void PreparePanoramaPlayback(IPlaybackSession player)
    {
        if (Panorama.IsImmersive && player is Playback playback)
            playback.MaximumVideoSize = new(Math.Max(4096, playback.MaximumVideoSize.Width), Math.Max(2160, playback.MaximumVideoSize.Height));
    }

    private void PresentFrame(Bitmap frame)
    {
        VideoImage.Source = frame;
        if (Panorama.IsImmersive)
        {
            try { PanoramaImage.SetFrame(frame); }
            catch (Exception error)
            {
                AppDiagnostics.Record("VR frame", error); PanoramaImage.View = Panorama with { Mode = PanoramaMode.Flat };
                UpdatePanoramaVisibility(); SyncPanoramaControls(); Notice(error.Message);
            }
        }
        else VideoImage.InvalidateVisual();
    }

    private void ClearVideoFrame() { PanoramaImage.ClearFrame(); VideoImage.Source = null; }
    private void UpdatePanoramaVisibility()
    {
        PanoramaImage.IsVisible = _info?.HasVideo == true && Panorama.IsImmersive;
        VideoImage.IsVisible = !PanoramaImage.IsVisible;
        if (!PanoramaImage.IsVisible) PanoramaImage.ClearFrame();
        PlayerVrButton.Content = Panorama.Mode switch { PanoramaMode.HalfSphere => "180°", PanoramaMode.FullSphere => "360°", _ => "VR" };
    }

    private void SyncPanoramaControls()
    {
        _vrUpdating = true;
        try
        {
            var view = Panorama;
            PlayerVrMode.SelectedIndex = (int)view.Mode; PlayerVrLayout.SelectedIndex = (int)view.Layout;
            PlayerVrEye.SelectedIndex = (int)view.Eye; PlayerVrProjection.SelectedIndex = (int)view.Projection;
            PlayerVrYaw.Minimum = view.Mode == PanoramaMode.HalfSphere ? -90 : -180;
            PlayerVrYaw.Maximum = view.Mode == PanoramaMode.HalfSphere ? 90 : 180;
            PlayerVrYaw.Value = view.Yaw; PlayerVrPitch.Value = view.Pitch; PlayerVrRoll.Value = view.Roll; PlayerVrFov.Value = view.FieldOfView;
            Localization.SetText(VrYawValue, $"{view.Yaw:0.0}°"); Localization.SetText(VrPitchValue, $"{view.Pitch:0.0}°");
            Localization.SetText(VrRollValue, $"{view.Roll:0.0}°"); Localization.SetText(VrFovValue, $"{view.FieldOfView:0}°");
            RefreshPanoramaControls();
        }
        finally { _vrUpdating = false; }
    }

    private void RefreshPanoramaControls()
    {
        var available = _info?.HasVideo == true && !_deleting;
        PlayerVrButton.IsEnabled = PlayerVrMode.IsEnabled = PlayerVrRemember.IsEnabled = available;
        VrOptions.IsEnabled = available && Panorama.IsImmersive;
        PlayerVrEye.IsEnabled = Panorama.Layout != PanoramaLayout.Mono;
        PlayerVrProjection.IsEnabled = Panorama.Mode == PanoramaMode.HalfSphere;
    }

    private void ToggleVrPanel()
    {
        if (_info?.HasVideo != true || _deleting) return;
        VrPanel.IsVisible = !VrPanel.IsVisible;
        if (VrPanel.IsVisible)
        {
            PlaylistPanel.IsVisible = false; SyncPanoramaControls();
            PlayerVrMode.Focus(_keyboardNavigation ? NavigationMethod.Tab : NavigationMethod.Pointer);
        }
        else VideoArea.Focus();
        ShowChrome();
    }
    private void RecenterPanorama()
    { if (Panorama.IsImmersive) { PanoramaImage.View = Panorama.Recenter(); Notice("视角已回正"); } }

    private void ChangePanoramaView(PlayerCommand command)
    {
        if (_info?.HasVideo != true || !Panorama.IsImmersive) return;
        PanoramaImage.View = command switch
        {
            PlayerCommand.ViewLeft => Panorama with { Yaw = Panorama.Yaw - 5 },
            PlayerCommand.ViewRight => Panorama with { Yaw = Panorama.Yaw + 5 },
            PlayerCommand.ViewUp => Panorama with { Pitch = Panorama.Pitch + 5 },
            PlayerCommand.ViewDown => Panorama with { Pitch = Panorama.Pitch - 5 }, _ => Panorama
        };
    }
    private async Task RememberPanoramaAsync()
    {
        if (_closed || _deleting || _info?.HasVideo != true) return;
        var path = CurrentPath; var view = Panorama; var revision = _revision;
        try { await Task.Run(() => _preferences.SavePlayerView(path, view)); if (Current(revision)) Notice("已记住此视频设置"); }
        catch (Exception error) { if (Current(revision)) Notice(Localization.Format($"VR 设置保存失败：{error.Message}")); }
    }
    private MenuItem PanoramaMenu()
    {
        var choices = new[] { "普通视频", "180° 半球", "360° 全景" }.Select((label, index) =>
        {
            var mode = (PanoramaMode)index;
            var choice = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, IsChecked = Panorama.Mode == mode };
            choice.Click += (_, _) => CommandReady = HandleKeyboardAsync(() => SetPanoramaAsync(Panorama with { Mode = mode }));
            return (object)choice;
        }).ToList();
        choices.Add(new Separator());
        var settings = new MenuItem { Header = "VR 设置…", InputGesture = new(Key.V) };
        settings.Click += (_, _) => ToggleVrPanel(); choices.Add(settings);
        var reset = new MenuItem { Header = "回正视角", InputGesture = new(Key.R), IsEnabled = Panorama.IsImmersive };
        reset.Click += (_, _) => RecenterPanorama(); choices.Add(reset);
        return new MenuItem { Header = "VR 视频", IsEnabled = _info?.HasVideo == true && !_deleting, ItemsSource = choices };
    }
    private void VrClick(object? sender, RoutedEventArgs e) => ToggleVrPanel();
    private void VrResetClick(object? sender, RoutedEventArgs e) => RecenterPanorama();
    private void VrRememberClick(object? sender, RoutedEventArgs e) => CommandReady = RememberPanoramaAsync();
}
