using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop.Player;

namespace AvaMedia.Desktop;

public partial class PlayerWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly Func<IMediaEngine, string, IPlaybackSession> _factory;
    private readonly IVideoFolderScanner _folderScanner;
    private readonly IRecycleBin _recycleBin;
    private readonly Storage _preferences;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _load, _seek, _folderLoad, _frameStep;
    private IPlaybackSession? _player;
    private Bitmap? _still;
    private MediaInfo? _info;
    private readonly Stopwatch _opening = new();
    private TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string[] _playlist = [];
    private string[] _explicitFiles = [];
    private string? _loadedFolder;
    private int _folderGeneration;
    private int _fileIndex, _revision, _seekGeneration;
    private bool _updating, _closed, _muted, _playIntent, _pendingSeek, _deleting;
    private double _position, _speed = 1, _lastSpeed = 1;
    private long _positionPublished;
    private string[]? _displayedPlaylist;
    private WindowState _windowedState;
    private readonly double[] _speeds = [.25, .5, .75, 1, 1.25, 1.5, 2, 3, 4];
    public string CurrentPath { get; private set; } = "";
    public string PlaybackError { get; private set; } = "";
    public double SourcePosition => _position;
    public double PlaybackSpeed => _speed;
    public bool CanOpenFiles => !_closed && !_deleting && !_nativeBusy;
    public bool IsPlaying => _nativePlaying || _player?.IsPlaying == true;
    public bool IsPaused => _nativeStarted && !_nativePlaying || _player?.IsPaused == true;
    public bool ConfirmDeletion { get; private set; }
    public double FirstFrameLatencyMs { get; private set; }
    public DateTimeOffset? FirstFrameUtc { get; private set; }
    public Task Ready { get; private set; } = Task.CompletedTask;
    public Task CommandReady { get; private set; } = Task.CompletedTask;
    public Task PlaylistReady { get; private set; } = Task.CompletedTask;
    public Task FirstFrameReady => _firstFrame.Task;

    public PlayerWindow() : this(new MediaEngine(new())) { }
    public PlayerWindow(IMediaEngine engine, IEnumerable<string>? files = null, Func<IMediaEngine, string, IPlaybackSession>? factory = null, IVideoFolderScanner? folderScanner = null, IRecycleBin? recycleBin = null, Storage? preferences = null)
    {
        InitializeComponent(); _engine = engine; _factory = factory ?? ((e, p) => new Playback(e, p) { MaximumVideoSize = new(4096, 2160) });
        _folderScanner = folderScanner ?? new VideoFolderScanner();
        _recycleBin = recycleBin ?? new RecycleBin(); _preferences = preferences ?? new Storage();
        var playerSettings = _preferences.LoadSettings(); ConfirmDeletion = playerSettings.ConfirmPlayerDeletion;
        _preferNative = factory is null && playerSettings.PlayerNativeHighResolution; _nativeSdr = playerSettings.PlayerNativeSdr;
        Localization.Changed += LanguageChanged;
        SetFiles(files ?? []);
        PlayerSeek.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && !_updating && _info is not null) CommandReady = SeekAsync(PlayerSeek.Value); };
        PlayerVolume.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && _player is not null) { _player.Volume = (float)(PlayerVolume.Value / 100); NoticeFormatted($"音量 {PlayerVolume.Value:0}%"); } };
        HeaderBar.PointerPressed += (_, e) =>
        {
            if (e.Source is Control source && (source is Button || source.GetVisualAncestors().Any(a => a is Button))) return;
            if (!e.GetCurrentPoint(HeaderBar).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
        };
        VideoArea.ContextRequested += (_, e) => { if (IsPlayerOverlay(e.Source)) return; OpenMenu(BuildMenu(), VideoArea); e.Handled = true; };
        InitializeKeyboard();
        InitializePanorama();
        VideoArea.PointerPressed += (_, e) =>
        {
            if (IsPlayerOverlay(e.Source)) return;
            if (!e.GetCurrentPoint(VideoArea).Properties.IsLeftButtonPressed) return;
            FocusPlayback();
            if (e.ClickCount == 2) { ToggleFullscreen(); e.Handled = true; }
        };
        VideoArea.PointerWheelChanged += (_, e) => { if (IsPlayerOverlay(e.Source)) return; PlayerVolume.Value = Math.Clamp(PlayerVolume.Value + e.Delta.Y * 5, 0, 100); e.Handled = true; };
        AddHandler(PointerMovedEvent, PlayerPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        _chromeTimer.Tick += (_, _) => HideChrome();
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); PlayerOsd.IsVisible = false; };
        ActualThemeVariantChanged += (_, _) => ShowChrome();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) ShowChrome();
            if (e.Property == WindowStateProperty || e.Property == IsVisibleProperty)
                if (_player is not null) _player.PresentationVisible = IsVisible && WindowState != WindowState.Minimized;
            if (e.Property == BoundsProperty) { PlayerVolume.IsVisible = Bounds.Width >= 900; PlayerTotalGroup.IsVisible = Bounds.Width >= 880; }
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, (_, e) => { if (_deleting || _closed) return; var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray(); if (paths?.Length > 0) { SetFiles(paths); Ready = OpenAsync(_playlist[0]); } });
        Opened += (_, _) => { FocusPlayback(); if (_playlist.Length > 0) Ready = OpenAsync(_playlist[0]); };
        Closed += (_, _) =>
        {
            Localization.Changed -= LanguageChanged;
            _closed = true; _revision++; _folderGeneration++; _chromeTimer.Stop(); _noticeTimer.Stop(); _lifetime.Cancel(); _load?.Cancel(); _seek?.Cancel(); _folderLoad?.Cancel(); CancelFrameStep();
            _player?.Dispose(); VideoImage.Source = null; PanoramaImage.Dispose(); _still?.Dispose(); _firstFrame.TrySetCanceled();
            _nativeCancellation?.Cancel();
            _load?.Dispose(); _seek?.Dispose(); _folderLoad?.Dispose(); _lifetime.Dispose();
        };
        RefreshTransport(); RefreshPlaylist(); ShowChrome();
    }

    private void LanguageChanged(object? sender, EventArgs e)
    {
        Title = CurrentPath.Length == 0 ? AppIdentity.PlayerTitle : Path.GetFileName(CurrentPath) + " — " + AppIdentity.PlayerTitle;
        if (CurrentPath.Length == 0) { FileName.Text = AppIdentity.PlayerTitle; PlayerStatus.Text = AppIdentity.PlayerWelcome; }
    }

    public Task OpenAsync(string path)
        => StartOpen(path, 0, 0, 0, true);
    public Task OpenAtAsync(string path, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        return StartOpen(path, 0, 0, seconds, true);
    }
    private Task StartOpen(string path, int video, int audio, double position, bool playing, bool allowDeleting = false)
    {
        if (_closed || _nativeBusy || _deleting && !allowDeleting) return Task.CompletedTask;
        _load?.Cancel(); _load?.Dispose(); _seek?.Cancel(); CancelFrameStep();
        _load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _seekGeneration++; _pendingSeek = false;
        var revision = ++_revision;
        var index = Array.FindIndex(_playlist, p => VideoFolderScanner.PathComparer.Equals(p, Path.GetFullPath(path)));
        if (index >= 0) _fileIndex = index; else SetFiles([path]);
        RefreshPlaylist();
        _firstFrame.TrySetCanceled(); _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (NativePlayerRunner.Detect(path) is null) StartFolderLoad(Path.GetFullPath(path), _firstFrame.Task);
        FirstFrameUtc = null; FirstFrameLatencyMs = 0; _opening.Restart();
        return Ready = OpenCore(Path.GetFullPath(path), revision, _load.Token, video, audio, position, playing);
    }
    private async Task OpenCore(string path, int revision, CancellationToken token, int video, int audio, double position, bool playing)
    {
        try
        {
            var old = _player; _player = null; _info = null; RefreshTransport();
            if (old is not null) { await old.Stop(); old.Dispose(); }
            if (!Current(revision)) return;
            CurrentPath = path; Title = Path.GetFileName(path) + " — " + AppIdentity.PlayerTitle; FileName.Text = Path.GetFileName(path); ToolTip.SetTip(FileName, path);
            PlaybackError = ""; _nativeDiagnostics.Clear(); PlayerStatus.Text = "正在打开…"; PlayerStatus.IsVisible = true; ClearVideoFrame(); _still?.Dispose(); _still = null;
            if (DetectDisc(path) is { } disc) { await PlayNativeAsync(path, disc, position, playing); return; }
            var info = await _engine.Probe(path, token, video, audio); token.ThrowIfCancellationRequested();
            if (!Current(revision)) return;
            if (info.Duration <= 0 || !info.HasVideo && !info.HasAudio) throw new InvalidDataException("该文件没有可播放的音视频轨。");
            _info = info;
            RestorePanorama(path, info.HasVideo);
            if (!Panorama.IsImmersive && PreferNative(info)) { await PlayNativeAsync(path, position: position, playing: playing); return; }
            var player = _factory(_engine, path); _player = player;
            _playIntent = playing;
            PreparePanoramaPlayback(player); player.Configure(info); player.Speed = _speed; player.Volume = (float)(PlayerVolume.Value / 100); player.Muted = _muted;
            player.PresentationVisible = IsVisible && WindowState != WindowState.Minimized;
            player.Updated += position =>
            {
                if (!Current(revision) || !ReferenceEquals(player, _player) || _pendingSeek) return;
                SetPosition(position, false);
                if (info.HasVideo) { PresentFrame(player.Frame); PlayerStatus.IsVisible = false; }
                else { PlayerStatus.Text = "音频播放"; }
                RefreshCapture(); MarkFirstFrame();
            };
            player.Error += message => { if (Current(revision)) { PlaybackError = message; Notice(message); } };
            player.Finished += () => { if (Current(revision)) CommandReady = Ended(player); };
            _updating = true; PlayerSeek.Maximum = info.Duration; _updating = false;
            PlayerTotal.Text = EditorTime.Format(info.Duration); SetPosition(Math.Clamp(position, 0, Math.Max(0, info.Duration - .001)));
            await player.Play(_position, info.HasVideo, info.Duration); if (!playing) player.Pause(); token.ThrowIfCancellationRequested();
            if (!Current(revision)) return;
            RefreshTransport(); Notice($"{info.Width} × {info.Height} · {MediaEngine.Number(info.FrameRate)} fps");
            await player.FirstFrame.WaitAsync(token); if (!Current(revision)) return; MarkFirstFrame();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!Current(revision)) return;
            PlaybackError = ex.Message; PlayerStatus.Text = Localization.Format($"播放失败：{ex.Message}"); PlayerStatus.IsVisible = true;
            _firstFrame.TrySetException(ex); RefreshTransport();
            Notifications.NotificationCenter.Shared.Publish(this, new(Guid.NewGuid().ToString("N"), "播放失败", ex.Message, Notifications.NotificationKind.Error,
                [new("原生 GPU / HDR 播放…", () => PlayNativeAsync(path, NativePlayerRunner.Detect(path)), Primary: true, Enabled: () => !_closed && !_nativeBusy && NativePlayerRuntime.Supported)]));
        }
    }
    private void MarkFirstFrame()
    {
        if (_firstFrame.Task.IsCompleted) return;
        FirstFrameUtc = DateTimeOffset.UtcNow; FirstFrameLatencyMs = _opening.Elapsed.TotalMilliseconds; _firstFrame.TrySetResult();
    }
    private bool Current(int revision) => !_closed && revision == _revision;
    private async Task Ended(IPlaybackSession player)
    {
        await player.Stop();
        if (_closed || !ReferenceEquals(_player, player)) return;
        _playIntent = false;
        RefreshTransport();
        if (_deleting) return;
        if (_fileIndex + 1 >= _playlist.Length) await PlaylistReady;
        if (_closed || _deleting || !ReferenceEquals(_player, player)) return;
        if (_fileIndex + 1 < _playlist.Length && string.IsNullOrEmpty(PlaybackError)) await ChangeFile(1);
        else Notice(string.IsNullOrEmpty(PlaybackError) ? "播放结束" : PlaybackError);
    }
    private void SetPosition(double position, bool immediate = true)
    {
        _position = Math.Clamp(position, 0, _info?.Duration ?? 0);
        var now = Stopwatch.GetTimestamp();
        if (!immediate && (!ControlsBar.IsVisible || now - _positionPublished < Stopwatch.Frequency / 10)) return;
        _positionPublished = now;
        _updating = true; PlayerSeek.Value = _position; _updating = false; PlayerTime.Text = EditorTime.Format(_position);
    }
    private void Notice(string message) { PlayerNotice.Text = message; ShowNotice(); }
    private void NoticeFormatted(FormattableString message) { Localization.SetText(PlayerNotice, message); ShowNotice(); }
    private void ShowNotice() { PlayerOsd.IsVisible = true; _noticeTimer.Stop(); _noticeTimer.Start(); }
    private void RefreshTransport()
    {
        var playing = _info is not null && _playIntent;
        PlayerPlayIcon.Kind = playing ? "pause" : "play";
        Avalonia.Automation.AutomationProperties.SetName(PlayerPlayButton, playing ? "暂停" : "播放");
        ToolTip.SetTip(PlayerPlayButton, playing ? "暂停（Space）" : "播放（Space）");
        PlayerPlayButton.IsEnabled = !_deleting && !_nativeBusy && (_info is not null || !string.IsNullOrEmpty(CurrentPath) && DetectDisc(CurrentPath) is not null);
        PlayerStopButton.IsEnabled = PlayerSeek.IsEnabled = _info is not null && !_deleting && !_nativeBusy;
        PlayerMuteButton.IsEnabled = _info?.HasAudio == true;
        PreviousFileButton.IsEnabled = !_deleting && _fileIndex > 0;
        NextFileButton.IsEnabled = !_deleting && _fileIndex + 1 < _playlist.Length;
        PlayerOpenButton.IsEnabled = PlaylistList.IsEnabled = !_deleting;
        RefreshPanoramaControls();
        RefreshCapture();
    }
    public async Task TogglePlaybackAsync()
    {
        if (_deleting) return;
        if (_player is null && !_nativeBusy && !string.IsNullOrEmpty(CurrentPath))
        { await StartOpen(CurrentPath, 0, 0, _info is not null && _position >= _info.Duration - .1 ? 0 : _position, true); return; }
        if (_player is not { } player || _info is not { } info) return;
        _seek?.Cancel(); CancelFrameStep(); _seekGeneration++; _pendingSeek = false;
        _playIntent = !_playIntent;
        if (!_playIntent) { player.Pause(); SetPosition(_position); }
        else if (player.IsPaused) player.Resume();
        else { if (_position >= info.Duration - .001) SetPosition(0); await player.Play(_position, info.HasVideo, info.Duration); }
        RefreshTransport();
    }
    public async Task SeekAsync(double seconds, bool? resume = null)
    {
        if (_deleting) return;
        if (_player is not { } player || _info is not { } info) return;
        CancelFrameStep();
        var playing = resume ?? _playIntent; var revision = _revision;
        _playIntent = playing; var generation = ++_seekGeneration; _pendingSeek = true;
        RefreshCapture();
        _seek?.Cancel(); _seek?.Dispose(); _seek = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _seek.Token;
        var position = Math.Clamp(seconds, 0, Math.Max(0, info.Duration - (info.FrameRate > 0 ? 1 / info.FrameRate : .001)));
        SetPosition(position);
        try
        {
            await Task.Delay(60, token); await player.Stop(); token.ThrowIfCancellationRequested();
            if (!Current(revision)) return;
            player.Configure(info);
            player.Speed = _speed;
            if (playing) await player.Play(position, info.HasVideo, info.Duration);
            else if (info.HasVideo)
            {
                await player.Play(position, true, info.Duration); player.Pause();
                await player.FirstFrame.WaitAsync(token); token.ThrowIfCancellationRequested(); if (!Current(revision)) return;
                PresentFrame(player.Frame); _still?.Dispose(); _still = null; PlayerStatus.IsVisible = false;
            }
            token.ThrowIfCancellationRequested(); _pendingSeek = false; RefreshTransport();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current(revision)) { PlaybackError = ex.Message; Notice(ex.Message); } }
        finally { if (generation == _seekGeneration) { _pendingSeek = false; if (!_closed) RefreshCapture(); } }
    }
    public async Task SetSpeedAsync(double speed)
    {
        _speed = Math.Clamp(Math.Round(speed, 2), .25, 4);
        PlayerSpeed.Content = MediaEngine.Number(_speed) + "×";
        if (_player is { HasSession: true }) await SeekAsync(_position, _playIntent);
        else if (_player is not null) _player.Speed = _speed;
        NoticeFormatted($"速度 {MediaEngine.Number(_speed)}×");
    }
    public async Task ExecuteAsync(PlayerCommand command)
    {
        if (_deleting) return;
        ShowChrome();
        switch (command)
        {
            case PlayerCommand.TogglePlayback: await TogglePlaybackAsync(); break;
            case PlayerCommand.ToggleFullscreen: ToggleFullscreen(); break;
            case PlayerCommand.ExitFullscreen: DismissPlayerOverlay(); break;
            case PlayerCommand.Back5: await SeekAsync(_position - 5); break;
            case PlayerCommand.Forward5: await SeekAsync(_position + 5); break;
            case PlayerCommand.Back30: await SeekAsync(_position - 30); break;
            case PlayerCommand.Forward30: await SeekAsync(_position + 30); break;
            case PlayerCommand.Back60: await SeekAsync(_position - 60); break;
            case PlayerCommand.Forward60: await SeekAsync(_position + 60); break;
            case PlayerCommand.VolumeUp: PlayerVolume.Value = Math.Min(100, PlayerVolume.Value + 5); break;
            case PlayerCommand.VolumeDown: PlayerVolume.Value = Math.Max(0, PlayerVolume.Value - 5); break;
            case PlayerCommand.Mute:
                if (_player is null) break;
                _muted = !_player.Muted; _player.Muted = _muted; PlayerMuteIcon.Kind = _player.Muted ? "muted" : "speaker";
                Avalonia.Automation.AutomationProperties.SetName(PlayerMuteButton, _player.Muted ? "取消静音" : "静音"); Notice(_player.Muted ? "静音" : "取消静音"); break;
            case PlayerCommand.Slower: await SetSpeedAsync(_speed - .1); break;
            case PlayerCommand.Faster: await SetSpeedAsync(_speed + .1); break;
            case PlayerCommand.NormalSpeed:
                var next = Math.Abs(_speed - 1) < .001 ? _lastSpeed : 1;
                if (Math.Abs(_speed - 1) > .001) _lastSpeed = _speed;
                await SetSpeedAsync(next); break;
            case PlayerCommand.PreviousFrame: case PlayerCommand.NextFrame:
                await StepFrameAsync(command == PlayerCommand.PreviousFrame ? -1 : 1); break;
            case PlayerCommand.Restart: await SeekAsync(0); break;
            case PlayerCommand.PreviousFile: await ChangeFile(-1); break;
            case PlayerCommand.NextFile: await ChangeFile(1); break;
            case PlayerCommand.Open: await Pick(); break;
            case PlayerCommand.Stop: await SeekAsync(0, false); break;
            case PlayerCommand.Help: ShortcutHelp.IsVisible = !ShortcutHelp.IsVisible; ShowChrome(); break;
            case PlayerCommand.Playlist: TogglePlaylist(); break;
            case PlayerCommand.Settings: OpenMenu(BuildMenu(), PlayerSettingsButton); break;
            case PlayerCommand.CaptureFrame: await CaptureFrameAsync(); break;
            case PlayerCommand.DeleteFile: await DeleteCurrentAsync(); break;
            case PlayerCommand.VrSettings: ToggleVrPanel(); break;
            case PlayerCommand.RecenterView: RecenterPanorama(); break;
            case PlayerCommand.ViewLeft: case PlayerCommand.ViewRight: case PlayerCommand.ViewUp: case PlayerCommand.ViewDown:
                ChangePanoramaView(command); break;
        }
    }
    public void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen) WindowState = _windowedState;
        else
        {
            FocusPlayback();
            _windowedState = WindowState;
            WindowState = WindowState.FullScreen;
        }
        Avalonia.Automation.AutomationProperties.SetName(FullscreenButton, WindowState == WindowState.FullScreen ? "退出全屏" : "全屏");
        ToolTip.SetTip(FullscreenButton, WindowState == WindowState.FullScreen ? "退出全屏（Enter / Esc）" : "全屏（Enter / 双击画面）");
        ShowChrome();
    }
    private bool IsPlayerOverlay(object? source) => source is Control control && control.GetVisualAncestors().Prepend(control)
        .Any(ancestor => ancestor == VrPanel || ancestor == PlaylistPanel || ancestor == ShortcutHelp);

    private void ShowChrome()
    {
        var fullscreen = WindowState == WindowState.FullScreen;
        HeaderBar.IsVisible = !fullscreen && !Skin.UsesCustomChrome(ActualThemeVariant);
        // Overlay the fullscreen picture so hiding controls does not resize it or generate pointer movement.
        var overlay = fullscreen && !PlaylistPanel.IsVisible && !VrPanel.IsVisible && !ShortcutHelp.IsVisible;
        Grid.SetRow(ControlsBar, overlay ? 1 : 2);
        ControlsBar.VerticalAlignment = overlay ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        ControlsBar.IsVisible = true;
        SetPosition(_position);
        Cursor = Cursor.Default;
        _chromeTimer.Stop();
        if (fullscreen) _chromeTimer.Start();
    }
    public void SetConfirmDeletion(bool value)
    { var settings = _preferences.LoadSettings(); settings.ConfirmPlayerDeletion = value; _preferences.SaveSettings(settings); ConfirmDeletion = value; }
    public async Task DeleteCurrentAsync()
    {
        if (_closed || _deleting || string.IsNullOrEmpty(CurrentPath)) return;
        var path = CurrentPath; var revision = _revision; var position = _position; var playing = _playIntent; var token = _lifetime.Token;
        _deleting = true; RefreshTransport();
        try
        {
            if (ConfirmDeletion && !await ConfirmRecycle(path)) return;
            Notice("正在移入回收站…");
            await Ready.WaitAsync(token);
            await PlaylistReady.WaitAsync(token);
            if (!Current(revision)) return;
            position = _position; playing = _playIntent;
            _folderLoad?.Cancel(); _folderGeneration++; _seek?.Cancel(); CancelFrameStep(); _seekGeneration++; _pendingSeek = false;
            if (_player is { } player) await player.Stop();
            await _recycleBin.MoveAsync(path, token);
            if (_closed) return;
            var index = _fileIndex;
            _playlist = _playlist.Where(p => !VideoFolderScanner.PathComparer.Equals(p, path)).ToArray();
            _explicitFiles = _explicitFiles.Where(p => !VideoFolderScanner.PathComparer.Equals(p, path)).ToArray();
            _player?.Dispose(); _player = null; _info = null; _playIntent = false;
            ClearVideoFrame(); _still?.Dispose(); _still = null;
            Localization.SetText(PlaylistStatus,$"{_playlist.Length} 个文件");
            if (_playlist.Length > 0)
            {
                _fileIndex = Math.Min(index, _playlist.Length - 1); await StartOpen(_playlist[_fileIndex], 0, 0, 0, true, allowDeleting: true);
                Notice(Localization.Format($"已移入回收站：{Path.GetFileName(path)}"));
            }
            else
            {
                CurrentPath = ""; PlaybackError = ""; _fileIndex = 0; _loadedFolder = null; SetPosition(0); PlayerTotal.Text = EditorTime.Format(0);
                FileName.Text = Title = AppIdentity.PlayerTitle; PlayerStatus.Text = "已移入回收站\n拖入媒体文件，或按 F3 打开"; PlayerStatus.IsVisible = true;
                ToolTip.SetTip(FileName, null);
                RefreshPlaylist(); PlaylistStatus.Text = "0 个文件";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed)
            {
                if (_player is not null && _info is not null) await StartOpen(path, _info.VideoStreamIndex, _info.AudioStreamIndex, position, playing, allowDeleting: true);
                Notice(Localization.Format($"移入回收站失败：{ex.Message}"));
            }
        }
        finally { _deleting = false; if (!_closed) RefreshTransport(); }
    }
    private async Task<bool> ConfirmRecycle(string path)
    {
        var dialog = new Window { Title = "移入回收站", Width = 520, Height = 280, MinWidth = 420, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var body = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), RowSpacing = 12, Margin = new(18) };
        body.Children.Add(new TextBlock { Text = "将原始文件移入回收站？", TextWrapping = TextWrapping.Wrap });
        var fileText = new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap }; Localization.SetIsUserText(fileText, true);
        var file = new ScrollViewer { Content = fileText }; Grid.SetRow(file, 1); body.Children.Add(file);
        var remember = new CheckBox { Content = "以后直接进回收站，不再确认" }; Grid.SetRow(remember, 2); body.Children.Add(remember);
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 12 };
        buttons.Children.Add(Ui.DialogButton("取消", () => dialog.Close(false)));
        var confirm = Ui.DialogButton("移入回收站", () => { if (remember.IsChecked == true) SetConfirmDeletion(false); dialog.Close(true); }); confirm.Classes.Add("primary"); buttons.Children.Add(confirm);
        Grid.SetRow(buttons, 3); body.Children.Add(buttons); dialog.Content = body;
        var confirmed = await dialog.ShowDialog<bool>(this);
        if (!_closed) FocusPlayback();
        return confirmed;
    }
    private async Task Pick()
    {
        var paths = await Ui.Pick(this, "打开视频 / 音频", true);
        if (!_closed) FocusPlayback();
        if (paths.Length == 0 || _closed || _deleting) return;
        SetFiles(paths); await OpenAsync(_playlist[0]);
    }
    public void OpenFiles(IEnumerable<string> files)
    {
        if (!CanOpenFiles) return;
        var paths = files.Where(path => File.Exists(path) || NativePlayerRunner.Detect(path) is not null).ToArray();
        if (paths.Length == 0) return;
        SetFiles(paths);
        Ready = OpenAsync(_playlist[0]);
    }
    private Task ChangeFile(int delta)
    { if (_deleting) return Task.CompletedTask; var index = _fileIndex + delta; if (index < 0 || index >= _playlist.Length) return Task.CompletedTask; _fileIndex = index; return OpenAsync(_playlist[index]); }
    private void SetFiles(IEnumerable<string> files)
    { _explicitFiles = files.Select(Path.GetFullPath).Distinct(VideoFolderScanner.PathComparer).ToArray(); _playlist = _explicitFiles; _fileIndex = 0; _loadedFolder = null; }
    private void StartFolderLoad(string path, Task firstFrame)
    {
        var directory = Path.GetDirectoryName(path)!;
        _folderLoad?.Cancel(); _folderLoad?.Dispose();
        _folderLoad = null;
        var generation = ++_folderGeneration;
        if (VideoFolderScanner.PathComparer.Equals(directory, _loadedFolder)) { PlaylistReady = Task.CompletedTask; return; }
        _folderLoad = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        PlaylistStatus.Text = "正在加载同目录视频…";
        PlaylistReady = LoadFolderAsync(directory, firstFrame, generation, _folderLoad.Token);
    }
    private async Task LoadFolderAsync(string directory, Task firstFrame, int generation, CancellationToken token)
    {
        try { await firstFrame.WaitAsync(token); }
        catch (Exception) { return; } // Opening errors are already displayed by OpenCore.
        try
        {
            var files = await _folderScanner.ScanAsync(directory, token);
            token.ThrowIfCancellationRequested();
            if (_closed || generation != _folderGeneration) return;
            var candidates = _explicitFiles.Length > 1 ? _explicitFiles.Concat(files) : files;
            _playlist = candidates.Append(CurrentPath).Distinct(VideoFolderScanner.PathComparer).ToArray();
            _fileIndex = Array.FindIndex(_playlist, p => VideoFolderScanner.PathComparer.Equals(p, CurrentPath));
            _loadedFolder = directory;
            RefreshPlaylist(); RefreshTransport();
            Localization.SetText(PlaylistStatus,$"{_playlist.Length} 个文件");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (!_closed && generation == _folderGeneration) PlaylistStatus.Text = Localization.Format($"目录加载失败：{ex.Message}"); }
    }
    private void RefreshPlaylist()
    {
        if (!ReferenceEquals(_displayedPlaylist, _playlist))
        { PlaylistList.ItemsSource = _playlist.Select(Path.GetFileName).ToArray(); _displayedPlaylist = _playlist; }
        PlaylistList.SelectedIndex = _fileIndex;
    }
    private void TogglePlaylist()
    {
        PlaylistPanel.IsVisible = !PlaylistPanel.IsVisible;
        if (PlaylistPanel.IsVisible)
        {
            VrPanel.IsVisible = false;
            PlaylistList.ScrollIntoView(PlaylistList.SelectedIndex);
            PlaylistList.Focus(_keyboardNavigation ? NavigationMethod.Tab : NavigationMethod.Pointer);
        }
        else FocusPlayback();
        ShowChrome();
    }
    private void PlaylistDoubleTapped(object? sender, RoutedEventArgs e)
    { CommandReady = PlaySelectedFileAsync(); }
    private ContextMenu SpeedMenu()
    {
        var menu = new ContextMenu();
        menu.ItemsSource = _speeds.Append(_speed).Distinct().Order().Select(speed =>
        {
            var item = new MenuItem { Header = MediaEngine.Number(speed) + "×", ToggleType = MenuItemToggleType.Radio, IsChecked = Math.Abs(speed - _speed) < .001 };
            item.Click += (_, _) => CommandReady = SetSpeedAsync(speed); return item;
        }).ToArray();
        return menu;
    }
    internal ContextMenu BuildMenu()
    {
        MenuItem Command(string title, PlayerCommand command, KeyGesture? key = null)
        { var item = new MenuItem { Header = title, InputGesture = key }; item.Click += (_, _) => CommandReady = ExecuteAsync(command); return item; }
        List<object> items = [Command("打开文件…", PlayerCommand.Open, new(Key.F3)), new Separator(),
            Command(_playIntent ? "暂停" : "播放", PlayerCommand.TogglePlayback, new(Key.Space)), Command("停止", PlayerCommand.Stop, new(Key.F4)),
            Command("上一文件", PlayerCommand.PreviousFile, new(Key.PageUp)), Command("下一文件", PlayerCommand.NextFile, new(Key.PageDown)),
            new MenuItem { Header = "播放速度", ItemsSource = SpeedMenu().ItemsSource }, Command(_muted ? "取消静音" : "静音", PlayerCommand.Mute, new(Key.M))];
        AddNativeMenu(items);
        if (_info is { } info)
        {
            using var json = JsonDocument.Parse(info.RawJson);
            foreach (var type in new[] { "video", "audio" })
            {
                var streams = json.RootElement.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() == type).ToArray();
                if (streams.Length <= 1) continue;
                var choices = streams.Select((stream, index) =>
                {
                    var codec = stream.GetProperty("codec_name").GetString();
                    var item = new MenuItem { Header = $"{index + 1}: {codec}", ToggleType = MenuItemToggleType.Radio, IsChecked = index == (type == "video" ? info.VideoStreamIndex : info.AudioStreamIndex) };
                    item.Click += (_, _) => CommandReady = StartOpen(CurrentPath, type == "video" ? index : info.VideoStreamIndex, type == "audio" ? index : info.AudioStreamIndex, _position, _playIntent);
                    return item;
                }).ToArray();
                items.Add(new MenuItem { Header = type == "video" ? "视频轨" : "音频轨", ItemsSource = choices });
            }
        }
        var uniform = new MenuItem { Header = "保持画面比例", ToggleType = MenuItemToggleType.Radio, IsChecked = VideoImage.Stretch == Stretch.Uniform };
        uniform.Click += (_, _) => VideoImage.Stretch = Stretch.Uniform;
        var fill = new MenuItem { Header = "拉伸填满", ToggleType = MenuItemToggleType.Radio, IsChecked = VideoImage.Stretch == Stretch.Fill };
        fill.Click += (_, _) => VideoImage.Stretch = Stretch.Fill;
        items.Add(new MenuItem { Header = "画面比例", ItemsSource = new[] { uniform, fill } });
        items.Add(PanoramaMenu());
        var capture = Command("截取当前帧（保存到视频目录）", PlayerCommand.CaptureFrame, new(Key.E, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control));
        capture.IsEnabled = CanCaptureFrame; items.Add(capture);
        items.Add(new Separator()); items.Add(Command("全屏 / 窗口", PlayerCommand.ToggleFullscreen, new(Key.Enter)));
        var delete = Command("删除原始文件到回收站", PlayerCommand.DeleteFile, new(Key.Delete)); delete.IsEnabled = !_deleting && !string.IsNullOrEmpty(CurrentPath); items.Add(delete);
        var confirmDeletion = new MenuItem { Header = "删除到回收站前确认", ToggleType = MenuItemToggleType.CheckBox, IsChecked = ConfirmDeletion };
        confirmDeletion.Click += (_, _) => SetConfirmDeletion(confirmDeletion.IsChecked); items.Add(confirmDeletion);
        items.Add(Command("播放列表", PlayerCommand.Playlist, new(Key.F6))); items.Add(Command("快捷键", PlayerCommand.Help, new(Key.F1)));
        var properties = new MenuItem { Header = "媒体信息", IsEnabled = _info is not null };
        properties.Click += (_, _) => { if (_info is { } info) CommandReady = Ui.MessageFormatted(this, "媒体信息", $"{Path.GetFileName(CurrentPath)}\n时长：{EditorTime.Format(info.Duration)}\n画面：{info.Width} × {info.Height}\n视频：{info.VideoCodec} · {MediaEngine.Number(info.FrameRate)} fps\n音频：{info.AudioCodec} · {info.AudioSampleRate} Hz · {info.AudioChannels} 声道"); };
        items.Add(properties);
        var notifications = new MenuItem { Header = "通知中心…" };
        notifications.Click += (_, _) => Notifications.NotificationCenter.Shared.OpenHistory(this); items.Add(notifications);
        var close = new MenuItem { Header = "关闭" }; close.Click += (_, _) => Close(); items.Add(close);
        return new ContextMenu { ItemsSource = items };
    }
    private void MenuClick(object? sender, RoutedEventArgs e) => OpenMenu(BuildMenu(), PlayerMenuButton);
    private void SpeedClick(object? sender, RoutedEventArgs e) => OpenMenu(SpeedMenu(), PlayerSpeed);
    private void SettingsClick(object? sender, RoutedEventArgs e) => OpenMenu(BuildMenu(), PlayerSettingsButton);
    private void PlaylistClick(object? sender, RoutedEventArgs e) => TogglePlaylist();
    private void MinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
    private void OpenClick(object? sender, RoutedEventArgs e) => CommandReady = Pick();
    private void CaptureClick(object? sender, RoutedEventArgs e) => CommandReady = CaptureFrameAsync();
    private void PlayClick(object? sender, RoutedEventArgs e) => CommandReady = TogglePlaybackAsync();
    private void StopClick(object? sender, RoutedEventArgs e) => CommandReady = ExecuteAsync(PlayerCommand.Stop);
    private void MuteClick(object? sender, RoutedEventArgs e) => CommandReady = ExecuteAsync(PlayerCommand.Mute);
    private void PreviousClick(object? sender, RoutedEventArgs e) => CommandReady = ChangeFile(-1);
    private void NextClick(object? sender, RoutedEventArgs e) => CommandReady = ChangeFile(1);
    private void FullscreenClick(object? sender, RoutedEventArgs e) => ToggleFullscreen();
}
