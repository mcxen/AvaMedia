using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class EditorWindow
{
    private sealed record SegmentSnapshot(ConversionOptions[] Drafts, int SelectedIndex);
    private readonly List<SegmentSnapshot> _segmentUndo = [], _segmentRedo = [];
    private readonly DispatcherTimer _segmentEditTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly SemaphoreSlim _segmentThumbnailGate = new(2, 2);
    private Task _segmentReady = Task.CompletedTask;
    private bool _restoringSegments, _previewAllSegments, _loadingSegmentControls;

    private void InitializeSegmentEditing()
    {
        _segmentEditTimer.Tick += (_, _) =>
        {
            _segmentEditTimer.Stop();
            try { CommitActiveSegment(); SegmentError.Text = ""; }
            catch (ArgumentException) { } // Keep invalid text in the editor until the user fixes it.
        };
        SegmentTrack.NavigateRequested += (index, seconds) => _segmentReady = NavigateSegment(index, seconds);
        AddHandler(KeyDownEvent, SegmentKeyDown, RoutingStrategies.Tunnel);
        foreach (var combo in new[] { FadeInCombo, FadeOutCombo }) combo.SelectionChanged += (_, _) => ScheduleSegmentUpdate();
        Opened += (_, _) => RefreshSegments();
    }

    internal async Task<byte[]> ReadSegmentThumbnail(ConversionOptions options, CancellationToken ct)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _ready.Task.WaitAsync(request.Token);
        await _segmentThumbnailGate.WaitAsync(request.Token);
        try { return await _previewFrames.Thumbnail(_path, options.Start, 176, 100, request.Token, pad: false, videoStreamIndex: options.VideoStreamIndex); }
        finally { _segmentThumbnailGate.Release(); }
    }

    private void ScheduleSegmentUpdate()
    {
        if (!QuickWorkflow || _updating || _restoringSegments || _loadingSegmentControls || _info is null || _closed) return;
        UpdateSegmentActions();
        try { if (_activeSegment is null || SameSegmentOptions(_activeSegment.Options, ReadDraft())) return; }
        catch (ArgumentException) { return; }
        CancelSegmentSequence();
        if (_player.IsPlaying || _player.IsPaused) Seek(_position);
        _segmentEditTimer.Stop(); _segmentEditTimer.Start();
        UpdateSegmentActions();
    }
    private static bool SameSegmentOptions(ConversionOptions a, ConversionOptions b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    private SegmentSnapshot SegmentState() => new(_segments.Select(s => s.Options.Clone()).ToArray(), Math.Max(0, _activeSegment is null ? 0 : _segments.IndexOf(_activeSegment)));
    private void RememberSegmentEdit()
    {
        if (_restoringSegments) return;
        _segmentUndo.Add(SegmentState());
        if (_segmentUndo.Count > 40) _segmentUndo.RemoveAt(0);
        _segmentRedo.Clear(); UpdateSegmentActions();
    }
    private void UpdateSegmentActions()
    {
        if (!QuickWorkflow) return;
        UndoSegmentButton.IsEnabled = _segmentUndo.Count > 0;
        RedoSegmentButton.IsEnabled = _segmentRedo.Count > 0;
        var ready = _info?.Duration > 0 && _activeSegment is not null;
        PlayAllSegmentsButton.IsEnabled = ready;
        AddSegmentButton.IsEnabled = SplitSegmentButton.IsEnabled = ready && ConfirmButton.IsEnabled;
        var validRange = ready && string.IsNullOrEmpty(TimeError.Text);
        SplitAtPositionButton.IsEnabled = validRange && EditorTime.TryRead(StartTime.Text, _options.Start, out var start)
            && EditorTime.TryRead(EndTime.Text, _options.End, out var end) && _position > start && _position < end;
    }

    private async Task<bool> SelectSegment(ClipSegmentEntry entry, bool play = false, bool sequence = false, bool skipCommit = false)
    {
        if (_closed || _info is null || !_segments.Contains(entry)) return false;
        try
        {
            if (!skipCommit) CommitActiveSegment();
            if (ReferenceEquals(entry, _activeSegment))
            {
                return !play || await PlaySelection(keepSequence: sequence);
            }
            var (revision, token) = BeginPreview(sequence);
            var tracksChanged = entry.Options.VideoStreamIndex != _options.VideoStreamIndex || entry.Options.AudioStreamIndex != _options.AudioStreamIndex;
            _activeSegment = entry; _options = entry.Options.Clone();
            if (_options.End == 0) _options.End = _info.Duration;
            _selectingSegment = true; SegmentList.SelectedItem = entry; _selectingSegment = false;
            SegmentList.ScrollIntoView(entry);
            _loadingSegmentControls = true;
            try
            {
                RefreshOptionControls(); UpdateTimes();
                _updating = true; _regionDelogo = false; DelogoMode.IsChecked = false; LoadRegion(); _updating = false;
                SyncDirectionControls(); RefreshSegments(); SegmentError.Text = "";
            }
            finally { _loadingSegmentControls = false; }
            await _player.Stop(); token.ThrowIfCancellationRequested();
            if (tracksChanged)
            {
                SegmentPane.IsEnabled = EditTabs.IsEnabled = false;
                try { ClearDirectionDetection(); await _audioReady; token.ThrowIfCancellationRequested(); await Load(); }
                finally { SegmentPane.IsEnabled = EditTabs.IsEnabled = true; }
            }
            if (!CurrentPreview(revision)) return false;
            SetPlaybackButton(false);
            if (play) { if (!await PlaySelection(keepSequence: sequence)) return false; }
            else { Seek(_options.Start, sequence); await _previewReady; }
            ScheduleThumbs(); return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _selectingSegment = true; SegmentList.SelectedItem = _activeSegment; _selectingSegment = false;
                CancelSegmentSequence(); SegmentError.Text = ex.Message;
            }
            return false;
        }
    }

    private async Task NavigateSegment(int index, double seconds)
    {
        if (index < 0 || index >= _segments.Count) return;
        if (await SelectSegment(_segments[index]))
        {
            Seek(seconds); await _previewReady;
        }
    }
    private void PreviewSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ClipSegmentEntry entry }) _segmentReady = SelectSegment(entry, play: true);
        e.Handled = true;
    }
    private void SegmentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source && (source is Button || source.GetVisualAncestors().Any(v => v is Button))) return;
        if (_activeSegment is not null) _segmentReady = SelectSegment(_activeSegment, play: true);
        e.Handled = true;
    }
    private void PlayAllSegmentsClick(object? sender, RoutedEventArgs e)
    {
        if (_previewAllSegments) { Seek(_options.Start); return; }
        _playbackReady = StartSegmentSequence();
    }
    private async Task StartSegmentSequence()
    {
        try
        {
            CommitActiveSegment();
            if (_segments.Count == 0) return;
            _previewAllSegments = true; PlayAllSegmentsButton.Content = "停止预览";
            if (!await SelectSegment(_segments[0], play: true, sequence: true)) CancelSegmentSequence();
        }
        catch (Exception ex) { CancelSegmentSequence(); SegmentError.Text = ex.Message; }
    }
    private void CancelSegmentSequence()
    {
        _previewAllSegments = false;
        if (PlayAllSegmentsButton is not null) PlayAllSegmentsButton.Content = "预览全部";
    }
    private async Task<bool> ContinueSegmentPreview()
    {
        if (!QuickWorkflow || _activeSegment is null || _previewFailed) return false;
        if (_previewAllSegments)
        {
            var index = _segments.IndexOf(_activeSegment) + 1;
            if (index < _segments.Count) return await SelectSegment(_segments[index], play: true, sequence: true);
            CancelSegmentSequence(); return false;
        }
        if (LoopSegmentCheck.IsChecked == true) return await PlaySelection();
        return false;
    }

    private void SplitAtPositionClick(object? sender, RoutedEventArgs e)
    {
        if (_activeSegment is null || _info is null) return;
        try
        {
            CommitActiveSegment();
            var draft = _activeSegment.Options; var end = draft.End > 0 ? draft.End : _info.Duration;
            if (_position <= draft.Start || _position >= end) { SegmentError.Text = "请将播放头移到当前片段内部。"; return; }
            var left = draft.Clone(); left.End = _position;
            var right = draft.Clone(); right.Start = _position;
            ReplaceSegment(_segments.IndexOf(_activeSegment), [left, right]);
        }
        catch (Exception ex) { SegmentError.Text = ex.Message; }
    }
    private void ReplaceSegment(int index, IReadOnlyList<ConversionOptions> parts)
    {
        if (parts.Count == 0) return;
        RememberSegmentEdit();
        _selectingSegment = true; _segments.RemoveAt(index);
        for (var i = 0; i < parts.Count; i++) _segments.Insert(index + i, new(parts[i]));
        _activeSegment = null; _selectingSegment = false;
        _segmentReady = SelectSegment(_segments[index], skipCommit: true);
    }

    private void UndoSegmentClick(object? sender, RoutedEventArgs e) => _segmentReady = RestoreSegmentEdit(false);
    private void RedoSegmentClick(object? sender, RoutedEventArgs e) => _segmentReady = RestoreSegmentEdit(true);
    private async Task RestoreSegmentEdit(bool redo)
    {
        if (_restoringSegments || _closed) return;
        try
        {
            try { CommitActiveSegment(); } catch (ArgumentException) { }
            var source = redo ? _segmentRedo : _segmentUndo; var target = redo ? _segmentUndo : _segmentRedo;
            if (source.Count == 0) return;
            _restoringSegments = true; _segmentEditTimer.Stop();
            var snapshot = source[^1]; source.RemoveAt(source.Count - 1); target.Add(SegmentState());
            BeginPreview();
            _selectingSegment = true; _segments.Clear();
            foreach (var draft in snapshot.Drafts) _segments.Add(new(draft));
            _activeSegment = null; _selectingSegment = false;
            await SelectSegment(_segments[Math.Clamp(snapshot.SelectedIndex, 0, _segments.Count - 1)], skipCommit: true);
        }
        finally { _restoringSegments = false; UpdateSegmentActions(); }
    }

    private void SegmentKeyDown(object? sender, KeyEventArgs e)
    {
        if (!QuickWorkflow || _info is null || _restoringSegments || !SegmentPane.IsEnabled || OwnedWindows.Any()) return;
        var source = e.Source as Control;
        bool Within<T>() where T : Control => source is T || source?.GetVisualAncestors().Any(v => v is T) == true;
        if (Within<TextBox>() || Within<ComboBox>() || Within<MenuItem>()) return;
        var command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (command && e.Key == Key.Z) _segmentReady = RestoreSegmentEdit(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        else if (command && e.Key == Key.Y) _segmentReady = RestoreSegmentEdit(true);
        else if (command && e.Key == Key.D) AddSegmentClick(null, e);
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Up) MoveSegment(-1);
        else if (e.KeyModifiers == KeyModifiers.Alt && e.Key == Key.Down) MoveSegment(1);
        else if (e.KeyModifiers != KeyModifiers.None) return;
        else if (e.Key == Key.I) SetStartClick(null, e);
        else if (e.Key == Key.O) SetEndClick(null, e);
        else if (e.Key == Key.S) SplitAtPositionClick(null, e);
        else if (e.Key == Key.Space && !Within<Button>()) _playbackReady = TogglePlayback();
        else if (e.Key == Key.Enter && Within<ListBox>()) { if (_activeSegment is not null) _segmentReady = SelectSegment(_activeSegment, play: true); }
        else if (e.Key == Key.Delete && Within<ListBox>()) RemoveSegmentClick(null, e);
        else return;
        e.Handled = true;
    }

    private void JumpStartClick(object? sender, RoutedEventArgs e)
    {
        try { Seek(ReadTimes().Start); } catch (ArgumentException) { ValidateInputs(); }
    }
    private void JumpEndClick(object? sender, RoutedEventArgs e) => _positionReady = JumpSelectionEnd();
    private async Task JumpSelectionEnd()
    {
        if (_info is null) return;
        try
        {
            var end = ReadTimes().End; var (revision, token) = BeginPreview();
            await _player.Stop(); token.ThrowIfCancellationRequested();
            if (!CurrentPreview(revision)) return;
            SetPosition(end); SetPlaybackButton(false);
            await Frame(PreviewImage, end, token, endExclusive: true, revision: revision);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) { PreviewStatus.Text = ex.Message; PreviewStatus.IsVisible = true; } }
    }
}
