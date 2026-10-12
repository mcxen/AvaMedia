using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Core;
using AvaMedia.Desktop.AiProgress;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class AiTaskWindow
{
    private readonly Func<AiQueueSummary>? _queueSummary;
    private Grid? _speechRoot;
    private readonly CancellationTokenSource _speechLifetime = new();
    private readonly ObservableCollection<SubtitleCue> _speechCues = [];
    private readonly ObservableCollection<string> _speechStages = [];
    private readonly TextBlock _speechTitle = SpeechUi.Text("", true, 11.5), _backend = SpeechUi.Text("", true, 11);
    private readonly TextBlock _percent = SpeechUi.Heading(""), _speechStep = SpeechUi.Heading(""), _segmentCaption = SpeechUi.Text("", true, 11);
    private readonly TextBlock _cueCount = SpeechUi.Text("", true, 11), _queueCaption = SpeechUi.Text("", true, 11.5);
    private readonly TextBlock _transcriptEmpty = SpeechUi.Text("等待字幕", true), _waveEmpty = SpeechUi.Text("等待音轨", true);
    private readonly SpeechTimeline _segments = new() { SegmentsOnly = true, Height = 28 }, _wave = new();
    private readonly List<SpeechStageCard> _stageCards = [];
    private Button? _speechOutput;
    private AudioEnvelope? _envelope;
    private SubtitleTaskResult? _shownTranscript;
    private AiActivity? _shownSpeechActivity;
    private bool _speechOutputAvailable, _checkingOutput;
    private DateTime _outputChecked;
    private ListBox? _transcript;
    private bool _speechLanguageDirty = true;

    private void BuildSpeechProgress()
    {
        SpeechUi.ApplyWindowStyle(this);
        Width = 1060; Height = 712; MinWidth = 1060; MinHeight = 712;
        foreach (var control in new Control[] { _result, _pause, _stop, _run, _error, _log, _records }) SpeechUi.Detach(control);
        _result.Classes.Remove("primary"); _result.IsDefault = false;
        _stop.Classes.Add("danger");
        var root = new Grid { RowDefinitions = new("50,Auto,*,Auto"), Margin = new(18, 0, 18, 16) };
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        var identity = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        identity.Children.Add(SpeechUi.Heading("任务 · 自动字幕"));
        _speechTitle.TextWrapping = TextWrapping.NoWrap; _speechTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        Localization.SetIsUserText(_speechTitle, true); Grid.SetColumn(_speechTitle, 1); identity.Children.Add(_speechTitle); header.Children.Add(identity);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var backend = SpeechUi.Card(_backend, 6); backend.CornerRadius = new(16); backend.BorderThickness = new(0); backend.IsVisible = false;
        backend.Bind(Border.BackgroundProperty, new DynamicResourceExtension("UiSpeechBackendSurface"));
        _backend.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiSuccess"));
        _backend.TextWrapping = TextWrapping.NoWrap; _backend.MaxWidth = 220; _backend.TextTrimming = TextTrimming.CharacterEllipsis;
        _backend.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) backend.IsVisible = !string.IsNullOrEmpty(_backend.Text); };
        actions.Children.Add(backend); _percent.FontSize = 22; _percent.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(_percent);
        actions.Children.Add(_run); actions.Children.Add(_pause); actions.Children.Add(_stop); Grid.SetColumn(actions, 1); header.Children.Add(actions);
        var top = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center }; top.Children.Add(header);
        top.Children.Add(_error); root.Children.Add(top);
        var stages = new Grid { ColumnDefinitions = new("*,14,*,14,*,14,*"), ColumnSpacing = 4 };
        for (var i = 0; i < 4; i++)
        {
            var card = new SpeechStageCard(i); _stageCards.Add(card); Grid.SetColumn(card, i * 2); stages.Children.Add(card);
            if (i == 3) continue;
            var arrow = new LucideIcon { Kind = "chevron-right", Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
            arrow.Bind(LucideIcon.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary")); Grid.SetColumn(arrow, i * 2 + 1); stages.Children.Add(arrow);
        }
        Grid.SetRow(stages, 1); root.Children.Add(stages);
        var body = new Grid { ColumnDefinitions = new("*,270"), ColumnSpacing = 14, Margin = new(0, 9, 0, 12) };
        var main = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*"), RowSpacing = 8 };
        var timelineTitle = new WrapPanel { Orientation = Orientation.Horizontal }; _speechStep.Margin = new(0, 0, 8, 0); timelineTitle.Children.Add(_speechStep);
        timelineTitle.Children.Add(SpeechUi.Text("每 5 分钟一段，逐段 提取音轨 → 检测语音 → 识别字幕", true, 11.5)); main.Children.Add(timelineTitle);
        Grid.SetRow(_segments, 1); main.Children.Add(_segments);
        var waveform = new Grid(); waveform.Children.Add(_wave); _waveEmpty.HorizontalAlignment = HorizontalAlignment.Center; _waveEmpty.VerticalAlignment = VerticalAlignment.Center; waveform.Children.Add(_waveEmpty); Grid.SetRow(waveform, 2); main.Children.Add(waveform);
        var caption = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 }; caption.Children.Add(_segmentCaption); Grid.SetColumn(_cueCount, 1); caption.Children.Add(_cueCount); Grid.SetRow(caption, 3); main.Children.Add(caption);
        _transcript = new ListBox { ItemsSource = _speechCues, Background = Brushes.Transparent, BorderThickness = new(0),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<SubtitleCue>((cue, _) =>
            {
                if (cue is null) return null;
                var row = new Grid { ColumnDefinitions = new("118,*"), ColumnSpacing = 10, Margin = new(0, 7), MinHeight = 20 };
                var time = SpeechUi.Text(AiActivity.FormatElapsed(cue.Start.TotalSeconds) + " – " + AiActivity.FormatElapsed(cue.End.TotalSeconds), true, 11);
                Localization.SetIsUserText(time, true); row.Children.Add(time);
                time.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") }; time.TextWrapping = TextWrapping.NoWrap;
                var text = SpeechUi.Text(cue.Text); text.TextWrapping = TextWrapping.Wrap; Localization.SetIsUserText(text, true); Grid.SetColumn(text, 1); row.Children.Add(text);
                var line = new Border { Child = row, BorderThickness = new(0, 0, 0, 1) }; line.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("UiDivider")); return line;
            }) };
        SpeechUi.CompactRows(_transcript);
        ScrollViewer.SetHorizontalScrollBarVisibility(_transcript, ScrollBarVisibility.Disabled);
        var cues = new Grid(); cues.Children.Add(_transcript); _transcriptEmpty.HorizontalAlignment = HorizontalAlignment.Center; _transcriptEmpty.VerticalAlignment = VerticalAlignment.Center; cues.Children.Add(_transcriptEmpty); Grid.SetRow(cues, 4); main.Children.Add(cues);
        var mainCard = SpeechUi.Card(main);  body.Children.Add(mainCard);
        var aside = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 14 };
        var records = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 8 }; records.Children.Add(SpeechUi.Heading("过程记录"));
        var history = new ListBox { ItemsSource = _speechStages, BorderThickness = new(0), Background = Brushes.Transparent,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            ItemTemplate = new FuncDataTemplate<string>((entry, _) => StageLogRow(entry ?? "")) };
        SpeechUi.CompactRows(history);
        ScrollViewer.SetHorizontalScrollBarVisibility(history, ScrollBarVisibility.Disabled); Grid.SetRow(history, 1); records.Children.Add(history);
        _records.Header = "完整日志"; _records.Content = _log; _log.MaxHeight = 150; Grid.SetRow(_records, 2); records.Children.Add(_records);
        var recordCard = SpeechUi.Card(records);  aside.Children.Add(recordCard);
        var after = new StackPanel { Spacing = 8 }; after.Children.Add(SpeechUi.Heading("完成后"));
        after.Children.Add(SpeechUi.Text("保存字幕草稿与结构化字幕，可「校对字幕」修改文本和时间，再导出 SRT / ASS / MP4 / MKV", true, 11.5));
        var finish = new WrapPanel(); _result.Margin = new(0, 0, 6, 0); finish.Children.Add(_result);
        _speechOutput = Ui.Button("查看结果", async () => await RunActionAsync(() =>
        {
            if (_speechOutputAvailable) Process.Start(new ProcessStartInfo(_job.Output) { UseShellExecute = true });
            return Task.CompletedTask;
        })); finish.Children.Add(_speechOutput); after.Children.Add(finish);
        var afterCard = SpeechUi.Card(after);  afterCard.MinHeight = 142; Grid.SetRow(afterCard, 1); aside.Children.Add(afterCard); Grid.SetColumn(aside, 1); body.Children.Add(aside);
        Grid.SetRow(body, 2); root.Children.Add(body);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 }; footer.Children.Add(_queueCaption);
        var close = Ui.Button("关闭", Close); Grid.SetColumn(close, 1); footer.Children.Add(close); Grid.SetRow(footer, 3); root.Children.Add(footer);
        foreach (var button in new[] { _run, _pause, _stop, _result, _speechOutput, close })
            SpeechUi.CompactButton(button);
        _speechRoot = root; Content = root;
        AiProgressStore.SpeechChanged += SpeechChanged;
        Localization.Changed += SpeechLanguageChanged;
        Opened += async (_, _) => await LoadSpeechMediaAsync();
    }

    private static Control StageLogRow(string entry)
    {
        var split = entry.IndexOf(" · ", StringComparison.Ordinal);
        var row = new Grid { ColumnDefinitions = new("58,*"), ColumnSpacing = 7, Margin = new(0, 3) };
        var timestamp = SpeechUi.Text(split >= 0 ? entry[..split] : "", true, 10.5); Localization.SetIsUserText(timestamp, true); row.Children.Add(timestamp);
        var text = SpeechUi.Text(SpeechUi.ActivityText(split >= 0 ? entry[(split + 3)..] : entry), size: 11.5); Grid.SetColumn(text, 1); row.Children.Add(text); return row;
    }
    private void SpeechChanged(string jobId)
    { if (jobId == _job.Id.ToString() || jobId == _job.Id.ToString("N")) { if (!_closed) Interlocked.Exchange(ref _refreshPosted, 1); } }
    private void SpeechLanguageChanged(object? sender, EventArgs args) { _speechLanguageDirty = true; Interlocked.Exchange(ref _refreshPosted, 1); }
    private async Task LoadSpeechMediaAsync()
    {
        try
        {
            if (_job.SubtitleResult is null && _job.State is not (JobState.Waiting or JobState.Running or JobState.Stopping))
            {
                var saved = await AiTaskResults.LoadAsync<SubtitleTaskResult>(AiTaskResults.PathFor(_job, "subtitles"));
                if (!_closed && saved is not null && _job.SubtitleResult is null) _job.SubtitleResult = saved;
            }
            Interlocked.Exchange(ref _refreshPosted, 1);
            if (_closed) return;
            var source = _job.Inputs.FirstOrDefault();
            if (source is not null) _envelope = await AudioEnvelopeService.GetAsync(source, ct: _speechLifetime.Token);
            if (!_closed) Interlocked.Exchange(ref _refreshPosted, 1);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) { _actionError = error.Message; Interlocked.Exchange(ref _refreshPosted, 1); } }
    }
    private void RefreshSpeechProgress()
    {
        var activity = _job.Activity;
        var speech = AiProgressStore.GetSpeech(_job.Id.ToString()) ?? AiProgressStore.GetSpeech(_job.Id.ToString("N"));
        var model = _job.Options.Transcription?.Model ?? SpeechModel.Base;
        var languageIndex = Array.IndexOf(TranscriptionOptions.Languages, _job.Options.Transcription?.Language ?? "auto");
        _speechTitle.Text = "· " + string.Join(" · ", _job.Inputs.Select(Path.GetFileName)) + " · " + SpeechUi.Model(model) + " · " + Localization.Text(SpeechUi.Languages[Math.Max(0, languageIndex)]);
        var backend = activity?.Nodes.Select(n => n.Snapshot?.Backend).LastOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? activity?.Backend ?? "";
        _backend.Text = backend; ToolTip.SetTip(_backend, backend);
        _percent.Text = $"{_job.Progress:0}%";
        var current = activity is null ? -1 : Math.Clamp(activity.CurrentNode, 0, 3);
        _speechStep.Text = current < 0 ? Localization.Text("等待语音识别") : Localization.Format($"第 {current + 1} 步 · {Localization.Key(SpeechUi.Stages[current])}");
        for (var i = 0; i < _stageCards.Count; i++) _stageCards[i].Update(activity?.Nodes.FirstOrDefault(n => n.Title == SpeechUi.Stages[i])?.Snapshot, i == current, _job.State, _speechLanguageDirty, i == 2 ? speech : null);
        if (!ReferenceEquals(activity, _shownSpeechActivity) || _speechLanguageDirty)
        {
            _shownSpeechActivity = activity;
            var logs = activity?.Nodes.SelectMany(n => n.Snapshot?.RecentStages ?? []).Concat(activity.RecentStages).Distinct().TakeLast(96).ToArray() ?? [];
            if (_speechLanguageDirty) _speechStages.Clear();
            Sync(_speechStages, logs);
        }
        var transcript = _job.SubtitleResult;
        if (!ReferenceEquals(transcript, _shownTranscript))
        {
            _shownTranscript = transcript;
            Sync(_speechCues, transcript?.Cues.TakeLast(300).ToArray() ?? []);
            if (_speechCues.Count > 0) Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            { if (!_closed && _speechCues.Count > 0) _transcript?.ScrollIntoView(_speechCues[^1]); }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        _transcriptEmpty.IsVisible = _speechCues.Count == 0;
        _cueCount.Text = Localization.Format($"已生成 {speech?.CueCount ?? transcript?.Cues.Length ?? 0} 条字幕");
        var segment = speech?.Segments.FirstOrDefault(s => s.Index == speech.CurrentSegment);
        _segmentCaption.Text = segment is null ? "" : Localization.Format($"第 {segment.Index + 1} 段 · {SpeechTimeline.TimeLabel(segment.StartSeconds)} – {SpeechTimeline.TimeLabel(segment.EndSeconds)} · 检测到 {segment.VoiceIntervalCount} 个语音区间");
        _segments.Update(speech, null, 0, 1);
        _wave.Update(speech, _envelope, _job.Options.Start, 1);
        _waveEmpty.IsVisible = _envelope is null || speech is null;
        _waveEmpty.Text = Localization.Text(_envelope is null ? "暂无波形" : _job.State is JobState.Running or JobState.Waiting ? "等待音轨" : "暂无分段记录");
        var queue = _queueSummary?.Invoke();
        var waiting = queue?.SubtitleJobsWaiting ?? 0;
        if (_job.State == JobState.Waiting || _job.State == JobState.Running && activity?.Stage == "等待语音识别") waiting = Math.Max(0, waiting - 1);
        _queueCaption.Text = _status.Text + (queue is null ? "" : Localization.Format($" · 另有 {waiting} 个字幕任务在等待语音识别"));
        if (!_checkingOutput && (DateTime.UtcNow - _outputChecked).TotalSeconds >= 1) _ = CheckSpeechOutputAsync();
        _speechLanguageDirty = false;
    }
    private async Task CheckSpeechOutputAsync()
    {
        _checkingOutput = true; _outputChecked = DateTime.UtcNow;
        try
        {
            var exists = await Task.Run(() => File.Exists(_job.Output));
            if (!_closed) { _speechOutputAvailable = exists; RefreshActions(); }
        }
        finally { _checkingOutput = false; }
    }
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        // Preserve realized rows where possible and bound retained data for long recordings.
        var first = 0;
        if (source.Count > 0) while (first < target.Count && !EqualityComparer<T>.Default.Equals(target[first], source[0])) first++;
        if (first < target.Count) for (var i = 0; i < first; i++) target.RemoveAt(0);
        else if (target.Count > 0) target.Clear();
        for (var i = 0; i < source.Count; i++)
            if (i >= target.Count) target.Add(source[i]); else if (!EqualityComparer<T>.Default.Equals(target[i], source[i])) target[i] = source[i];
        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }
    private void CloseSpeechProgress()
    {
        AiProgressStore.SpeechChanged -= SpeechChanged; Localization.Changed -= SpeechLanguageChanged;
        _speechLifetime.Cancel(); _speechLifetime.Dispose();
    }
}
