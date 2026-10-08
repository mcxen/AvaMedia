using Avalonia.Controls;
using Avalonia.Threading;
using System.Text.Json;
using AvaMedia.Desktop.Notifications;
using AvaMedia.Desktop.Player;

namespace AvaMedia.Desktop;

public partial class PlayerWindow
{
    private bool _nativeBusy, _nativeStarted, _nativePlaying, _preferNative, _nativeSdr;
    private CancellationTokenSource? _nativeCancellation;
    private readonly Dictionary<string, JsonElement> _nativeDiagnostics = [];
    private Task _nativeLifetime = Task.CompletedTask;
    private DiscPlayback? _nativeDisc;
    private DiscPlayback? DetectDisc(string path) => _nativeDisc is { } known && VideoFolderScanner.PathComparer.Equals(known.Path, path) ? known : NativePlayerRunner.Detect(path);
    private async Task OpenDiscAsync()
    {
        if (_nativeBusy || _closed) return;
        if (await new DiscOpenWindow().ShowDialog<DiscPlayback?>(this) is { } source)
        {
            _load?.Cancel(); _seek?.Cancel(); _folderLoad?.Cancel(); CancelFrameStep(); _revision++; _folderGeneration++;
            _firstFrame.TrySetCanceled(); _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _opening.Restart(); FirstFrameUtc = null; FirstFrameLatencyMs = 0; PlaybackError = "";
            SetFiles([source.Path]); CurrentPath = source.Path; _info = null; _position = 0;
            FileName.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(source.Path));
            Title = FileName.Text + " — " + AvaMedia.Core.AppIdentity.PlayerTitle; ToolTip.SetTip(FileName, source.Path);
            RefreshPlaylist(); await PlayNativeAsync(source.Path, source, 0);
        }
    }
    private async Task PlayNativeAsync(string path, DiscPlayback? disc = null, double? position = null, bool playing = true)
    {
        if (_nativeBusy || _closed) return;
        _nativeBusy = true;
        _load?.Cancel(); _seek?.Cancel(); CancelFrameStep();
        _nativeDisc = disc;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _nativeLifetime = NativeLifetimeAsync(path, disc, position, playing, started);
        await Task.WhenAny(started.Task, _nativeLifetime);
    }
    private async Task NativeLifetimeAsync(string path, DiscPlayback? disc, double? position, bool playing, TaskCompletionSource started)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _nativeCancellation = cancellation;
        var notificationKey = "native-player-runtime"; var downloading = false; var runtimeReady = false;
        try
        {
            if (_player is { } previous)
            { _player = null; await previous.Stop(); VideoImage.Source = null; previous.Dispose(); }
            _playIntent = false; RefreshTransport();
            var progress = new Progress<PlayerRuntimeProgress>(value =>
            {
                if (_closed || runtimeReady || cancellation.IsCancellationRequested || _nativeCancellation != cancellation) return;
                downloading = true;
                NotificationCenter.Shared.Publish(this, new(notificationKey, value.Stage,
                    (FormattableString)$"{value.Received / 1048576d:0.0} / {value.Total / 1048576d:0.0} MB", NotificationKind.Progress,
                    [new("停止下载", () => { cancellation.Cancel(); return Task.CompletedTask; })],
                    value.Total > 0 ? 100d * value.Received / value.Total : null, value.Total == 0));
            });
            var executable = await NativePlayerRuntime.EnsureAsync(progress, cancellation.Token); cancellation.Token.ThrowIfCancellationRequested();
            runtimeReady = true;
            if (downloading) NotificationCenter.Shared.Publish(this, new(notificationKey, "原生播放引擎已就绪", "mpv " + NativePlayerRuntime.Version, NotificationKind.Success));
            PlayerStatus.Text = Localization.Text("原生 GPU 播放"); PlayerStatus.IsVisible = true;
            _nativeDiagnostics.Clear();
            await NativePlayerRunner.RunAsync(executable, NativePlayerRunner.Arguments(path, position ?? _position, _speed, PlayerVolume.Value, _muted, playing, _nativeSdr, disc),
                () => { _nativeStarted = true; _nativePlaying = playing; Hide(); started.TrySetResult(); },
                () => Dispatcher.UIThread.Post(() => { if (!_closed && !cancellation.IsCancellationRequested) MarkFirstFrame(); }),
                (name, value) => Dispatcher.UIThread.Post(() =>
                {
                    if (_closed || cancellation.IsCancellationRequested) return;
                    _nativeDiagnostics[name] = value;
                    if (name == "time-pos" && value.ValueKind == JsonValueKind.Number) _position = value.GetDouble();
                    if (name == "pause" && value.ValueKind is JsonValueKind.True or JsonValueKind.False) _nativePlaying = !value.GetBoolean();
                    if (name == "path" && value.ValueKind == JsonValueKind.String && File.Exists(value.GetString()))
                    { CurrentPath = value.GetString()!; FileName.Text = Path.GetFileName(CurrentPath); Title = FileName.Text + " — " + AvaMedia.Core.AppIdentity.PlayerTitle; }
                }),
                async token =>
                {
                    if (disc is not null) return [];
                    await PlaylistReady.WaitAsync(token);
                    return await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var index = Array.FindIndex(_playlist, file => VideoFolderScanner.PathComparer.Equals(file, path));
                        return index < 0 ? [] : _playlist.Skip(index + 1).Where(File.Exists).ToArray();
                    });
                }, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (downloading) NotificationCenter.Shared.Publish(this, new(notificationKey, "原生播放已停止", Path.GetFileName(path)), show: false);
        }
        catch (Exception error)
        {
            PlaybackError = error.Message; AppDiagnostics.Record("Native player", error);
            NotificationCenter.Shared.Publish(this, new(notificationKey, "原生播放失败", error.Message, NotificationKind.Error, [
                new("重试播放", () => { _ = PlayNativeAsync(path, disc, position, playing); return Task.CompletedTask; }, Primary: true, DismissOnSuccess: true, Enabled: () => !_closed && !_nativeBusy),
                new("查看播放器", () => NotificationCenter.ShowOwnerAsync(this), Enabled: () => !_closed)]), reopen: true);
        }
        finally
        {
            _firstFrame.TrySetCanceled();
            try
            {
                if (!_closed)
                {
                    Show(); Activate(); PlayerStatus.Text = Localization.Text("原生播放已关闭"); PlayerStatus.IsVisible = true;
                    if (disc is null && File.Exists(CurrentPath))
                        try
                        {
                            var index = Array.FindIndex(_playlist, file => VideoFolderScanner.PathComparer.Equals(file, CurrentPath));
                            if (index >= 0) { _fileIndex = index; RefreshPlaylist(); }
                            _info = await _engine.Probe(CurrentPath, _lifetime.Token);
                            _updating = true; PlayerSeek.Maximum = _info.Duration; _updating = false;
                            SetPosition(_position); PlayerTotal.Text = EditorTime.Format(_info.Duration);
                        }
                        catch (Exception error) when (error is not OperationCanceledException) { AppDiagnostics.Record("Native player final media info", error); }
                }
            }
            catch (OperationCanceledException) { }
            finally { _nativeBusy = _nativeStarted = _nativePlaying = false; _nativeCancellation = null; if (!_closed) RefreshTransport(); }
        }
    }
    private bool PreferNative(AvaMedia.Core.MediaInfo info) => _preferNative && NativePlayerRuntime.Supported &&
        (info.Width >= 3840 || info.Height >= 2160 || PlaybackVideoProfile.IsHdr(info));
    private void AddNativeMenu(List<object> items)
    {
        var native = new MenuItem { Header = "原生 GPU / HDR 播放…", IsEnabled = !_nativeBusy && !string.IsNullOrEmpty(CurrentPath) && NativePlayerRuntime.Supported };
        native.Click += (_, _) => CommandReady = PlayNativeAsync(CurrentPath, DetectDisc(CurrentPath));
        var disc = new MenuItem { Header = "打开原盘…", IsEnabled = !_nativeBusy && NativePlayerRuntime.Supported };
        disc.Click += (_, _) => CommandReady = OpenDiscAsync();
        var prefer = new MenuItem { Header = "4K / HDR 优先原生播放", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _preferNative, IsEnabled = NativePlayerRuntime.Supported };
        prefer.Click += (_, _) => { _preferNative = prefer.IsChecked; var preferences = _preferences.LoadSettings(); preferences.PlayerNativeHighResolution = _preferNative; _preferences.SaveSettings(preferences); };
        var sdr = new MenuItem { Header = "原生播放输出为 SDR", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _nativeSdr };
        sdr.Click += (_, _) => { _nativeSdr = sdr.IsChecked; var preferences = _preferences.LoadSettings(); preferences.PlayerNativeSdr = _nativeSdr; _preferences.SaveSettings(preferences); };
        var diagnostics = new MenuItem { Header = "解码与色彩信息…", IsEnabled = _info is not null || _nativeDiagnostics.Count > 0 };
        diagnostics.Click += async (_, _) =>
        {
            if (_nativeDiagnostics.Count > 0) await Ui.Message(this, "解码与色彩信息", "mpv " + NativePlayerRuntime.Version + "\n" + string.Join("\n", _nativeDiagnostics.Select(pair => pair.Key + ": " + pair.Value)));
            else if (_info is { } info)
            {
                var profile = PlaybackVideoProfile.Inspect(info);
                await Ui.MessageFormatted(this, "解码与色彩信息", $"{info.VideoCodec} · {profile.PixelFormat}\n{info.Width} × {info.Height}\n{Localization.Key(profile.Color.ToneMap ? "HDR → SDR 色调映射" : "SDR 播放")}");
            }
        };
        items.Add(new Separator()); items.Add(native); items.Add(disc); items.Add(prefer); items.Add(sdr); items.Add(diagnostics); items.Add(new Separator());
    }
}
