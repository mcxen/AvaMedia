using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed class SubtitleReviewWindow : Window
{
    private sealed class Source(string path)
    {
        public string Path { get; } = path;
        public ObservableCollection<SubtitleCue>? Cues { get; set; }
        public long Length { get; set; }
        public DateTime WriteUtc { get; set; }
        public string Status { get; set; } = "待识别";
    }
    private readonly IMediaEngine _engine;
    private readonly ConversionRequest _request;
    private readonly Source[] _sources;
    private readonly ListBox _files = new(), _cues = new();
    private readonly TextBox _start = Ui.Input(), _end = Ui.Input(), _text = Ui.Input();
    private readonly TextBlock _notice = Ui.Text("", "caption");
    private readonly ComboBox _format;
    private readonly Button _export, _retry, _stop;
    private readonly StackPanel _editor = new() { Spacing = 10 };
    private readonly AiActivityView _activity = new() { Compact = true };
    private readonly SubtitleStyleEditor _style;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _recognition;
    private bool _busy;
    private Source? Selected => _files.SelectedItem as Source;

    public SubtitleReviewWindow(IMediaEngine engine, ConversionRequest request, bool editing)
    {
        _engine = engine; _request = request; _sources = request.Files.Select((path,index) =>
        {
            var source=new Source(path);var options=request.InputOptions?.ElementAtOrDefault(index)??request.Options;
            if(options.Transcription is { ReviewedCues: {} cues } speech)
            {
                var file=new FileInfo(path);
                if(file.Exists&&file.Length==speech.ReviewedSourceLength&&file.LastWriteTimeUtc==speech.ReviewedSourceWriteUtc)
                {source.Cues=new(cues);source.Length=file.Length;source.WriteUtc=file.LastWriteTimeUtc;source.Status="待校对";}
            }
            return source;
        }).ToArray();
        Title = "字幕校对"; Width = 1100; Height = 780; MinWidth = 850; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, "document");
        var root = new Grid { RowDefinitions = new("*,Auto,Auto"), Margin = new(20), RowSpacing = 12 };
        var content = new Grid { ColumnDefinitions = new("230,*,310"), ColumnSpacing = 16 };
        _files.ItemTemplate = new FuncDataTemplate<Source>((source, _) =>
        {
            var row = new StackPanel { Spacing = 4, Margin = new(0, 4) };
            var name = Ui.Text(System.IO.Path.GetFileName(source!.Path)); Localization.SetIsUserText(name, true);
            name.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis; row.Children.Add(name); row.Children.Add(Ui.Text(source.Status, "caption")); return row;
        });
        _cues.ItemTemplate = new FuncDataTemplate<SubtitleCue>((cue, _) =>
        {
            var row = new StackPanel { Spacing = 4, Margin = new(2, 6) };
            row.Children.Add(Ui.Text(MediaTime.Format(cue!.Start.TotalSeconds) + " – " + MediaTime.Format(cue.End.TotalSeconds), "caption"));
            var text = Ui.Text(cue.Text); Localization.SetIsUserText(text, true); text.TextWrapping = Avalonia.Media.TextWrapping.Wrap; row.Children.Add(text); return row;
        });
        content.Children.Add(_files); Grid.SetColumn(_cues, 1); content.Children.Add(_cues);
        Avalonia.Automation.AutomationProperties.SetName(_files,"字幕源文件");Avalonia.Automation.AutomationProperties.SetName(_cues,"字幕列表");
        Avalonia.Automation.AutomationProperties.SetName(_start,"开始时间");Avalonia.Automation.AutomationProperties.SetName(_end,"结束时间");Avalonia.Automation.AutomationProperties.SetName(_text,"字幕文字");
        _text.AcceptsReturn = true; _text.MinHeight = 100; _text.TextWrapping = Avalonia.Media.TextWrapping.Wrap; Localization.SetIsUserText(_text, true);
        _editor.Children.Add(Ui.Text("开始时间")); _editor.Children.Add(_start); _editor.Children.Add(Ui.Text("结束时间")); _editor.Children.Add(_end);
        _editor.Children.Add(_text);
        _editor.Children.Add(Ui.Button("应用修改", ApplyCue));
        _editor.Children.Add(Ui.Button("播放此处", async () =>
        {
            if (Selected is not {} source || _cues.SelectedItem is not SubtitleCue cue) return;
            try
            {
                var player = new PlayerWindow(engine, [source.Path]); player.Show(this); await player.Ready;
                await player.SeekAsync(request.Options.Start + cue.Start.TotalSeconds * request.Options.Speed, true);
            }
            catch(Exception error) { _notice.Text = error.Message; }
        }));
        _editor.Children.Add(Ui.Button("删除此条", () => { if (_cues.SelectedItem is SubtitleCue cue) Selected?.Cues?.Remove(cue); Refresh(); }));
        var side = new StackPanel { Spacing = 12 }; side.Children.Add(_editor);
        side.Children.Add(Ui.Button("添加字幕", () =>
        {
            if (_busy || Selected?.Cues is not {} cues) return;
            var begin = cues.LastOrDefault()?.End ?? TimeSpan.Zero;
            var cue = new SubtitleCue(begin, begin + TimeSpan.FromSeconds(1), "新字幕"); cues.Add(cue); _cues.SelectedItem = cue; Refresh();
        }));
        _format = Ui.Combo(["字幕文件 · SRT", "样式字幕 · ASS", "带字幕视频 · MP4", "带字幕视频 · MKV"], "字幕文件 · SRT");
        _format.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "srt", "ass", "mp4", "mkv" }, request.Options.Format));
        side.Children.Add(Ui.Text("输出内容")); side.Children.Add(_format);
        _style = new SubtitleStyleEditor(request.Options);
        var styles=Ui.Button("字幕样式…",async()=>
        {
            var window=new Window { Title="字幕样式",Width=780,Height=650,MinWidth=700,MinHeight=480,WindowStartupLocation=WindowStartupLocation.CenterOwner };
            var scroll=new ScrollViewer { Content=_style };var layout=new Grid { RowDefinitions=new("*,Auto"),Margin=new(20),RowSpacing=12 };layout.Children.Add(scroll);
            var done=Ui.DialogButton("关闭",()=>
            {
                try{var draft=_request.Options.Clone();_style.ReadInto(draft);window.Close();}
                catch(Exception error){_=Ui.Message(window,"参数错误",error.Message);}
            });done.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetRow(done,1);layout.Children.Add(done);window.Content=layout;
            window.Opened+=async(_,_)=>await _style.SetVideoAsync(_engine,Selected?.Path,_request.Options.VideoStreamIndex,_lifetime.Token);
            window.Closed+=(_,_)=>scroll.Content=null;await window.ShowDialog(this);
        });side.Children.Add(styles);
        _format.SelectionChanged+=(_,_)=>styles.IsVisible=_format.SelectedIndex!=0;styles.IsVisible=_format.SelectedIndex!=0;
        Grid.SetColumn(side, 2); content.Children.Add(new ScrollViewer { Content = side, [Grid.ColumnProperty] = 2 }); root.Children.Add(content);
        Grid.SetRow(_activity, 1); root.Children.Add(_activity);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 }; footer.Children.Add(_notice);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _retry = Ui.Button("重试失败文件", async () => await RecognizeAsync()); actions.Children.Add(_retry);
        _stop = Ui.Button("停止识别", () => _recognition?.Cancel()); actions.Children.Add(_stop);
        actions.Children.Add(Ui.DialogButton("取消", () => Close(null)));
        _export = Ui.DialogButton(editing ? "保存修改" : "导出字幕", Export); _export.Classes.Add("primary");_export.IsDefault=true;actions.Children.Add(_export);
        ToolExecution.Configure(this, _export, "导出字幕", editing);
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        _files.SelectionChanged += (_, _) => { _cues.ItemsSource = Selected?.Cues; _notice.Text = Localization.Text(Selected?.Status ?? ""); Refresh(); };
        _cues.SelectionChanged += (_, _) =>
        {
            if (_cues.SelectedItem is {} item && item is SubtitleCue cue)
            { _start.Text = MediaTime.Format(cue.Start.TotalSeconds); _end.Text = MediaTime.Format(cue.End.TotalSeconds); _text.Text = cue.Text; }
            Refresh();
        };
        Opened += async (_, _) => await RecognizeAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _recognition?.Cancel(); _style.Dispose(); };
        RefreshFiles(); _files.SelectedIndex = 0; Refresh();
    }

    private async Task RecognizeAsync()
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _recognition = operation; _busy = true; Refresh();
        try
        {
            foreach (var source in _sources.Where(source => source.Cues is null))
            {
                operation.Token.ThrowIfCancellationRequested(); source.Status = "识别中"; RefreshFiles();
                try
                {
                    var file = new FileInfo(source.Path); source.Length = file.Length; source.WriteUtc = file.LastWriteTimeUtc;
                    var options = (_request.InputOptions?.ElementAtOrDefault(Array.IndexOf(_sources,source))??_request.Options).Clone(); var speech = options.Transcription?.Clone() ?? new(); speech.ReviewedCues = null;
                    var job = new Job { Inputs = [source.Path], Options = options };
                    var cues = await new SpeechSubtitleService(_engine).TranscribeAsync(job, speech, options.AudioStreamIndex,
                        percent => Dispatcher.UIThread.Post(() => _notice.Text = System.IO.Path.GetFileName(source.Path) + $" · {percent:0}%"), operation.Token,
                        activity => Dispatcher.UIThread.Post(() => { if (!_lifetime.IsCancellationRequested) _activity.Update(activity); }));
                    CheckSource(source); source.Cues = new(cues); source.Status = cues.Count == 0 ? "未识别到语音" : "待校对";
                }
                catch (OperationCanceledException) { source.Status = "待识别"; throw; }
                catch (Exception error) { source.Status = error.Message; }
                RefreshFiles(); if (Selected == source) _cues.ItemsSource = source.Cues;
            }
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) _notice.Text = Localization.Text("识别已停止，已完成结果保留"); }
        finally { _busy = false; _recognition = null; if (!_lifetime.IsCancellationRequested) Refresh(); }
    }
    private void SaveCurrentCue()
    {
        if (_busy || Selected?.Cues is not {} cues || _cues.SelectedItem is not SubtitleCue cue) return;
        if(!EditorTime.TryRead(_start.Text,cue.Start.TotalSeconds,out var begin)||!EditorTime.TryRead(_end.Text,cue.End.TotalSeconds,out var end))throw new ArgumentException("时间格式为 时:分:秒.毫秒");
        var revised = new SubtitleCue(TimeSpan.FromSeconds(begin), TimeSpan.FromSeconds(end), _text.Text?.Trim() ?? "");
        var index = cues.IndexOf(cue); if(index<0)return;var draft = cues.ToArray(); draft[index] = revised;
        new TranscriptionOptions { ReviewedCues = draft }.Validate(); cues[index] = revised; _cues.SelectedItem = revised;
    }
    private void ApplyCue()
    {
        try { SaveCurrentCue(); _notice.Text=""; }
        catch (Exception error) { _notice.Text = error.Message; }
    }
    private void Export()
    {
        if (_busy) return;
        try
        {
            SaveCurrentCue();
            var sources = _sources.Where(source => source.Cues?.Count > 0).ToArray();
            if (sources.Length == 0) throw new ArgumentException("请先识别或添加字幕。");
            var options = sources.Select(source =>
            {
                CheckSource(source); var edit = (_request.InputOptions?.ElementAtOrDefault(Array.IndexOf(_sources,source))??_request.Options).Clone(); edit.Format = new[] { "srt", "ass", "mp4", "mkv" }[_format.SelectedIndex];
                if (edit.Format != "srt") _style.ReadInto(edit);
                edit.Transcription ??= new(); edit.Transcription.ReviewedCues = source.Cues!.ToArray();
                edit.Transcription.ReviewedSourceLength = source.Length; edit.Transcription.ReviewedSourceWriteUtc = source.WriteUtc;
                edit.Transcription.Validate(); return edit;
            }).ToArray();
            _ = ConversionBatch.CreateJobs(_request.Feature, sources.Select(source => source.Path).ToArray(), _request.OutputFolder, options[0], options);
            Close(_request with { Files = sources.Select(source => source.Path).ToArray(), Options = options[0], InputOptions = options, StartImmediately = ToolExecution.StartImmediately(this) });
        }
        catch(Exception error) { _notice.Text = error.Message; }
    }
    private static void CheckSource(Source source)
    {
        var file = new FileInfo(source.Path);
        if (!file.Exists || file.Length != source.Length || file.LastWriteTimeUtc != source.WriteUtc) throw new IOException("源文件已改变，请重新打开并识别字幕。");
    }
    private void RefreshFiles() { var selected = Selected; _files.ItemsSource = null; _files.ItemsSource = _sources; _files.SelectedItem = selected ?? _sources.FirstOrDefault(); }
    private void Refresh()
    {
        _stop.IsVisible = _busy; _retry.IsVisible = !_busy && _sources.Any(source => source.Cues is null);
        _export.IsEnabled = !_busy && _sources.Any(source => source.Cues?.Count > 0); _editor.IsEnabled = !_busy && _cues.SelectedItem is SubtitleCue;
        _format.IsEnabled = !_busy;
    }
}
