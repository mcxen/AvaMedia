using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;
using AvaMedia.Desktop.MultiStage;

namespace AvaMedia.Desktop.PersonClipAdjust;

internal sealed partial class PersonClipAdjustWindow
{
    private Playback? _player;
    private readonly Image _frame = new() { Stretch = Stretch.Uniform };
    private MediaFrameView? _frameView;
    private readonly MediaPreviewSurface _surface = new() { UseAmbientLighting = false };
    private readonly PreviewChip _chip = new(HorizontalAlignment.Left);
    private readonly TextBlock _previewStatus = new() { Classes = { "ms-caption" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly AvaMedia.MediaUi.CropOverlay _crop = new() { IsVisible = false };
    private readonly PreviewRequest _seekRequest = new();
    private readonly TextBlock _time = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _total = new() { Classes = { "ms-caption" }, VerticalAlignment = VerticalAlignment.Center };
    private Button _play = null!;
    private readonly LucideIcon _playIcon = new() { Kind = "play", Width = 16, Height = 16 };
    private readonly ToggleButton _loop = new() { Classes = { "ac", "icon-only" }, Padding = new(6), VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _result = new() { Classes = { "ac", "small" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _precision = new() { Classes = { "ac" }, VerticalAlignment = VerticalAlignment.Center };
    private Button _mute = null!;
    private readonly LucideIcon _muteIcon = new() { Kind = "volume-2", Width = 16, Height = 16 };
    private double? _playEnd;
    private int _playRevision;
    private bool _playing;
    private bool _cropping;
    private int? _cropSegment;
    private readonly DispatcherTimer _cropCommit = new() { Interval = TimeSpan.FromMilliseconds(350) };

    partial void BuildPreviewContent() => _previewSlot.Children.Add(BuildPreview());
    partial void InitializeMedia() => _ = LoadMediaAsync();
    partial void PlaySelected() { if (Selected is { } segment) _ = PlaySegmentAsync(segment.Id); }
    partial void TrackPosition(double seconds);
    partial void ScheduleStrip();

    private Control BuildPreview()
    {
        var grid = new Grid { RowDefinitions = new("*,Auto") };
        grid.Bind(Grid.RowSpacingProperty, new DynamicResourceExtension("AcMsGap"));
        var frame = new Grid();
        frame.Bind(BackgroundProperty, new DynamicResourceExtension("AcPreviewFill"));
        frame.Children.Add(_frame); frame.Children.Add(_previewStatus);
        MsUi.Brush(_crop, AvaMedia.MediaUi.CropOverlay.ShadeBrushProperty, "UiCropShade");
        MsUi.Brush(_crop, AvaMedia.MediaUi.CropOverlay.SelectionBrushProperty, "UiCropSelection");
        MsUi.Brush(_crop, AvaMedia.MediaUi.CropOverlay.BorderBrushProperty, "UiCropBorder");
        MsUi.Brush(_crop, AvaMedia.MediaUi.CropOverlay.HandleBrushProperty, "UiMediaText");
        _crop.ShowGrid = true;
        frame.Children.Add(_crop); frame.Children.Add(_chip);
        _surface.PreviewContent = frame;
        _surface.Bind(MediaPreviewSurface.ContentSizeProperty, new Binding("Source.Size") { Source = _frame });
        _surface.Title = Path.GetFileName(_session.SourcePath);
        _previewStatus.Text = Localization.Text("正在读取视频…");
        _frameView = new(_frame);
        grid.Children.Add(_surface);

        var transport = new Grid { Height = 38, ColumnDefinitions = new("Auto,*,Auto,*,Auto"), Margin = new(2, 0) };
        _time.FontSize = 15;
        _time.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiText"));
        _time.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        _total.FontFeatures = _time.FontFeatures;
        transport.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _time, _total } });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(IconButton("skip-back", "上一段", () => JumpSegment(-1)));
        buttons.Children.Add(IconButton("chevron-left", "后退", () => _ = StepAsync(-1)));
        _play = IconButton("play", "播放 / 暂停", () => _ = TogglePlayAsync());
        _play.Width = 34; _play.Height = 34; _play.CornerRadius = new(17);
        _play.Content = _playIcon; _play.Classes.Add("primary-round");
        buttons.Children.Add(_play);
        buttons.Children.Add(IconButton("chevron-right", "前进", () => _ = StepAsync(1)));
        buttons.Children.Add(IconButton("skip-forward", "下一段", () => JumpSegment(1)));
        buttons.Children.Add(IconButton("square", "停止", () => _ = StopToSegmentAsync()));
        var loopIcon = new LucideIcon { Kind = "repeat", Width = 16, Height = 16 };
        loopIcon.Bind(LucideIcon.ForegroundProperty, new Binding(nameof(ToggleButton.Foreground)) { Source = _loop });
        _loop.Content = loopIcon; ToolTip.SetTip(_loop, Localization.Text("循环播放片段"));
        Avalonia.Automation.AutomationProperties.SetName(_loop, Localization.Text("循环播放片段"));
        buttons.Children.Add(_loop);
        Grid.SetColumn(buttons, 2); transport.Children.Add(buttons);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        _result.Content = Localization.Text("预览结果"); ToolTip.SetTip(_result, Localization.Text("只播放保留的片段，按输出顺序"));
        _result.IsCheckedChanged += (_, _) => { if (_playing) _ = StopPlaybackAsync(); };
        right.Children.Add(_result);
        _precision.ItemsSource = new[] { "0.01 秒", "0.1 秒", "1 秒", "1 帧" }.Select(Localization.Text).ToArray();
        _precision.SelectedIndex = 1;
        Avalonia.Automation.AutomationProperties.SetName(_precision, Localization.Text("微调精度"));
        right.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { MsUi.Caption(Localization.Text("精度")), _precision } });
        _mute = IconButton("volume-2", "静音", ToggleMute);
        _mute.Content = _muteIcon;
        right.Children.Add(_mute);
        Grid.SetColumn(right, 4); transport.Children.Add(right);
        Grid.SetRow(transport, 1); grid.Children.Add(transport);

        _cropCommit.Tick += (_, _) => { _cropCommit.Stop(); CommitCrop(); };
        _crop.Changed += _ => { if (_cropping) { _cropCommit.Stop(); _cropCommit.Start(); } };
        return grid;
    }

    private double Precision => _precision.SelectedIndex switch
    {
        0 => .01, 2 => 1, 3 => 1 / Math.Max(1, _info?.FrameRate is > 0 ? _info.FrameRate : 25), _ => .1
    };

    private async Task LoadMediaAsync()
    {
        try
        {
            _info = await _engine.Probe(_session.SourcePath, _lifetime.Token);
            if (_closed) return;
            _player = new Playback(_engine, _session.SourcePath);
            _player.Configure(_info);
            _player.Updated += PlayerUpdated;
            _player.Finished += PlayerFinished;
            _player.Error += message => { if (!_closed) Notify(message); };
            _crop.SourceWidth = Math.Max(1, _info.Width); _crop.SourceHeight = Math.Max(1, _info.Height);
            _crop.PixelAspectRatio = MediaStreams.PixelAspectRatio(_info);
            _mute.IsEnabled = _info.HasAudio;
            RefreshTimeline();
            var start = Selected?.Start ?? 0;
            SeekTo(start);
            _ = LoadRowThumbsAsync();
            ScheduleStrip();
            if (!_info.HasVideo) _previewStatus.Text = Localization.Text("音频预览");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            _previewStatus.Text = Localization.Format($"媒体读取失败：{error.Message}");
            AppDiagnostics.Record("Person clip adjust probe", error);
        }
    }

    partial void DisposeMedia()
    {
        _seekRequest.Dispose(); _cropCommit.Stop(); _surface.Dispose();
        if (_player is { } player) { player.Updated -= PlayerUpdated; player.Finished -= PlayerFinished; player.Dispose(); }
        _frameView?.Dispose();
    }

    private void SetPosition(double seconds)
    {
        _position = Math.Clamp(seconds, 0, _session.Duration);
        _time.Text = AdjustTime.Clock(_position, 1);
        _total.Text = "/ " + AdjustTime.Clock(_session.Duration, 1);
        TrackPosition(_position);
        var frameRate = _info?.FrameRate is > 0 ? _info.FrameRate : 25;
        _chip.Text = AdjustTime.Clock(_position, 1) + " · " + Localization.Format($"第 {(int)Math.Round(_position * frameRate)} 帧");
    }

    partial void SeekTo(double seconds)
    {
        SetPosition(seconds);
        if (_playing) { _ = PlayFromAsync(_position); return; }
        _ = ShowFrameAsync(_position);
    }

    private async Task ShowFrameAsync(double seconds)
    {
        if (_info?.HasVideo != true || _frameView is null) return;
        var token = _seekRequest.Restart(_lifetime.Token);
        try
        {
            var size = VideoPreviewSizes.Bound(_info, VideoPreviewSizes.Preview);
            var time = Math.Clamp(seconds, 0, _info.Duration);
            var data = await _engine.Thumbnail(_session.SourcePath, time, size.Width, size.Height, token, pad: false,
                videoStreamIndex: _info.VideoStreamIndex, endExclusive: time >= _info.Duration);
            if (!_seekRequest.IsCurrent(token) || _closed || _playing) return;
            _frameView.Show(data); _previewStatus.IsVisible = false;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) { _previewStatus.Text = error.Message; _previewStatus.IsVisible = true; } }
    }

    // ------------------------------------------------------------------ playback
    private (double Start, double End)? PlayRange(double from)
    {
        if (_result.IsChecked == true)
        {
            var kept = _session.Segments.Where(s => s.Keep).ToList();
            var current = kept.FirstOrDefault(s => from >= s.Start - 1e-6 && from < s.End - .02);
            if (current is null)
            {
                // Output order: continue with the first kept segment after the one containing the playhead, else the first.
                current = kept.FirstOrDefault(s => s.Start >= from) ?? kept.FirstOrDefault();
                if (current is null) return null;
                from = current.Start;
            }
            return (from, current.End);
        }
        if (_loop.IsChecked == true && Selected is { } selected) return (from >= selected.Start && from < selected.End - .02 ? from : selected.Start, selected.End);
        var end = _session.Duration;
        return from < end - .02 ? (from, end) : (0, end);
    }

    private async Task TogglePlayAsync()
    {
        if (_player is null) return;
        if (_playing) { await StopPlaybackAsync(); return; }
        await PlayFromAsync(_position);
    }

    private async Task PlayFromAsync(double from)
    {
        if (_player is null || _info is null || _closed) return;
        if (PlayRange(from) is not { } range) { Notify(Localization.Text("没有保留的片段")); return; }
        var revision = ++_playRevision;
        _playing = true; _playIcon.Kind = "pause"; _playEnd = range.End;
        _seekRequest.Cancel();
        try
        {
            _player.Speed = Selected?.Options.Speed ?? 1;
            await _player.Play(range.Start, _info.HasVideo, range.End);
            if (revision != _playRevision) return;
            SetPosition(range.Start);
        }
        catch (Exception error) { _playing = false; _playIcon.Kind = "play"; Notify(error.Message); }
    }

    private async Task PlaySegmentAsync(int id)
    {
        if (_session.Segments.FirstOrDefault(s => s.Id == id) is not { } segment || _player is null || _info is null) return;
        var revision = ++_playRevision;
        _playing = true; _playIcon.Kind = "pause"; _playEnd = segment.End;
        try { _player.Speed = segment.Options.Speed; await _player.Play(segment.Start, _info.HasVideo, segment.End); if (revision == _playRevision) SetPosition(segment.Start); }
        catch (Exception error) { _playing = false; _playIcon.Kind = "play"; Notify(error.Message); }
    }

    private async Task StopPlaybackAsync()
    {
        _playRevision++;
        if (_player is not null && _playing) { _playing = false; await _player.Stop(); }
        _playing = false; _playIcon.Kind = "play";
    }

    private async Task StopToSegmentAsync()
    {
        await StopPlaybackAsync();
        SeekTo(Selected?.Start ?? 0);
    }

    private void PlayerUpdated(double position)
    {
        if (_closed || !_playing) return;
        SetPosition(position);
        if (_player is not null) { _frameView?.Show(_player.Frame); _previewStatus.IsVisible = false; }
    }

    private void PlayerFinished()
    {
        if (_closed || !_playing) return;
        var end = _playEnd ?? _session.Duration;
        _playing = false; _playIcon.Kind = "play";
        if (_result.IsChecked == true)
        {
            var kept = _session.Segments.Where(s => s.Keep).ToList();
            var index = kept.FindIndex(s => Math.Abs(s.End - end) < 1e-3);
            if (index >= 0 && index + 1 < kept.Count) { _ = PlayFromAsync(kept[index + 1].Start); return; }
            if (_loop.IsChecked == true && kept.Count > 0) { _ = PlayFromAsync(kept[0].Start); return; }
            return;
        }
        if (_loop.IsChecked == true && Selected is { } selected && Math.Abs(selected.End - end) < 1e-3) { _ = PlayFromAsync(selected.Start); return; }
        SetPosition(end);
    }

    private async Task StepAsync(int direction)
    {
        if (_playing) await StopPlaybackAsync();
        SeekTo(Math.Round((_position + direction * Precision) * 1000) / 1000);
    }

    private void JumpSegment(int direction)
    {
        var ordered = _session.Segments.OrderBy(s => s.Start).ToList();
        if (ordered.Count == 0) return;
        PersonClipEditSegment? target = direction < 0
            ? ordered.LastOrDefault(s => s.Start < _position - .05) ?? ordered[0]
            : ordered.FirstOrDefault(s => s.Start > _position + .05) ?? ordered[^1];
        if (Selected is { } current && direction < 0 && _position > current.Start + .05) target = current;
        Select(target.Id, seek: false); SeekTo(target.Start);
    }

    private void ToggleMute()
    {
        if (_player is null) return;
        _player.Muted = !_player.Muted;
        _muteIcon.Kind = _player.Muted ? "volume-x" : "volume-2";
        ToolTip.SetTip(_mute, Localization.Text(_player.Muted ? "取消静音" : "静音"));
        Avalonia.Automation.AutomationProperties.SetName(_mute, Localization.Text(_player.Muted ? "取消静音" : "静音"));
    }

    partial void HandleEditorKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: _ = TogglePlayAsync(); break;
            case Key.Left: _ = StepAsync(-1); break;
            case Key.Right: _ = StepAsync(1); break;
            case Key.Up when !e.KeyModifiers.HasFlag(KeyModifiers.Alt): JumpSegment(-1); break;
            case Key.Down when !e.KeyModifiers.HasFlag(KeyModifiers.Alt): JumpSegment(1); break;
            default: HandleSegmentKey(e); return;
        }
        e.Handled = true;
    }
    partial void HandleSegmentKey(KeyEventArgs e);

    // ------------------------------------------------------------------ crop on the preview
    private void BeginCrop()
    {
        if (Selected is not { } segment || _info?.HasVideo != true) return;
        _cropping = true; _cropSegment = segment.Id;
        var o = segment.Options;
        _crop.Selection = o.CropWidth > 0 ? new Rect(o.CropX, o.CropY, o.CropWidth, o.CropHeight) : new Rect(0, 0, _info.Width, _info.Height);
        _crop.Enabled = true; _crop.IsVisible = true; _crop.InvalidateVisual();
        Notify(Localization.Text("在预览上拖动裁剪框，完成后再次点击「裁剪」"), error: false);
        RefreshInspector();
    }

    private void EndCrop()
    {
        if (!_cropping) return;
        _cropCommit.Stop(); CommitCrop();
        _cropping = false; _cropSegment = null; _crop.Enabled = false; _crop.IsVisible = false;
        RefreshInspector();
    }

    private void CommitCrop()
    {
        if (!_cropping || _cropSegment is not { } id || _info is null || _session.Segments.FirstOrDefault(s => s.Id == id) is not { } segment) return;
        var rect = _crop.Selection;
        var options = segment.Options.Clone();
        var full = rect.Width >= _info.Width - 1 && rect.Height >= _info.Height - 1;
        options.CropX = full ? 0 : (int)rect.X; options.CropY = full ? 0 : (int)rect.Y;
        options.CropWidth = full ? 0 : (int)rect.Width; options.CropHeight = full ? 0 : (int)rect.Height;
        if (options.CropX == segment.Options.CropX && options.CropY == segment.Options.CropY
            && options.CropWidth == segment.Options.CropWidth && options.CropHeight == segment.Options.CropHeight) return;
        Do(() => _session.SetEffects(id, options));
    }
}
