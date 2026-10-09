using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed class SubtitleReviewWindow : Window
{
    private sealed class CueDraft(SubtitleCue original) : Observable
    {
        private string _start = EditorTime.Format(original.Start.TotalSeconds);
        private string _end = EditorTime.Format(original.End.TotalSeconds);
        private string _text = original.Text;
        public string Start { get => _start; set { if (Set(ref _start, value)) Raise(nameof(Time)); } }
        public string End { get => _end; set { if (Set(ref _end, value)) Raise(nameof(Time)); } }
        public string Text { get => _text; set => Set(ref _text, value); }
        public string Time => Start + " – " + End;
        public SubtitleCue Read()
        {
            if (!EditorTime.TryRead(Start, original.Start.TotalSeconds, out var begin)
                || !EditorTime.TryRead(End, original.End.TotalSeconds, out var end))
                throw new ArgumentException("时间格式为 时:分:秒.毫秒");
            var cue = new SubtitleCue(TimeSpan.FromSeconds(begin), TimeSpan.FromSeconds(end), Text.Trim());
            new TranscriptionOptions { ReviewedCues = [cue] }.Validate();
            return cue;
        }
    }
    private sealed class Source(string path) : Observable
    {
        private string _status = "待识别";
        public string Path { get; } = path;
        public ObservableCollection<CueDraft>? Cues { get; set; }
        public long Length { get; set; }
        public DateTime WriteUtc { get; set; }
        public string Status { get => _status; set => Set(ref _status, value); }
    }
    private readonly IMediaEngine _engine;
    private readonly ConversionRequest _request;
    private readonly Source[] _sources;
    private readonly ListBox _files = new(), _cues = new();
    private readonly TextBox _start = Ui.Input(), _end = Ui.Input(), _text = Ui.Input();
    private readonly TextBlock _notice = Ui.Text("", "caption");
    private readonly ComboBox _format;
    private readonly TextBox _folder;
    private readonly CheckBox _sourceFolder;
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
                {source.Cues=new(cues.Select(cue => new CueDraft(cue)));source.Length=file.Length;source.WriteUtc=file.LastWriteTimeUtc;source.Status="待校对";}
            }
            return source;
        }).ToArray();
        Title = "字幕校对"; Width = 1100; Height = 780; MinWidth = 850; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, "document");
        var root = new Grid { RowDefinitions = new("*,Auto,Auto"), Margin = new(20), RowSpacing = 12 };
        var content = new Grid { ColumnDefinitions = new("230,*,310"), ColumnSpacing = 16 };
        _files.ItemTemplate = new FuncDataTemplate<Source>((source, _) =>
        {
            if (source is null) return new TextBlock();
            var row = new StackPanel { Spacing = 4, Margin = new(0, 4) };
            var name = Ui.Text(System.IO.Path.GetFileName(source.Path)); Localization.SetIsUserText(name, true);
            name.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis; row.Children.Add(name);
            var status = Ui.Text("", "caption"); status.Bind(TextBlock.TextProperty, new Binding(nameof(Source.Status))); row.Children.Add(status); return row;
        });
        _cues.ItemTemplate = new FuncDataTemplate<CueDraft>((cue, _) =>
        {
            var row = new StackPanel { Spacing = 4, Margin = new(2, 6) };
            var time = Ui.Text("", "caption"); time.Bind(TextBlock.TextProperty, new Binding(nameof(CueDraft.Time))); row.Children.Add(time);
            var text = Ui.Text(""); text.Bind(TextBlock.TextProperty, new Binding(nameof(CueDraft.Text)));
            Localization.SetIsUserText(text, true); text.TextWrapping = Avalonia.Media.TextWrapping.Wrap; row.Children.Add(text); return row;
        });
        content.Children.Add(_files); Grid.SetColumn(_cues, 1); content.Children.Add(_cues);
        Avalonia.Automation.AutomationProperties.SetName(_files,"字幕源文件");Avalonia.Automation.AutomationProperties.SetName(_cues,"字幕列表");
        Avalonia.Automation.AutomationProperties.SetName(_start,"开始时间");Avalonia.Automation.AutomationProperties.SetName(_end,"结束时间");Avalonia.Automation.AutomationProperties.SetName(_text,"字幕文字");
        _text.AcceptsReturn = true; _text.MinHeight = 100; _text.TextWrapping = Avalonia.Media.TextWrapping.Wrap; Localization.SetIsUserText(_text, true);
        _editor.Children.Add(Ui.Text("开始时间")); _editor.Children.Add(_start); _editor.Children.Add(Ui.Text("结束时间")); _editor.Children.Add(_end);
        _editor.Children.Add(_text);
        _start.Bind(TextBox.TextProperty, new Binding(nameof(CueDraft.Start)) { Mode = BindingMode.TwoWay });
        _end.Bind(TextBox.TextProperty, new Binding(nameof(CueDraft.End)) { Mode = BindingMode.TwoWay });
        _text.Bind(TextBox.TextProperty, new Binding(nameof(CueDraft.Text)) { Mode = BindingMode.TwoWay });
        _editor.Children.Add(Ui.Button("播放此处", async () =>
        {
            if (Selected is not {} source || _cues.SelectedItem is not CueDraft draft) return;
            try
            {
                var cue = draft.Read();
                var player = new PlayerWindow(engine, [source.Path]); player.ShowForPlayback(this); await player.Ready;
                await player.SeekAsync(request.Options.Start + cue.Start.TotalSeconds * request.Options.Speed, true);
            }
            catch(Exception error) { _notice.Text = error.Message; }
        }));
        _editor.Children.Add(Ui.Button("删除此条", () => { if (_cues.SelectedItem is CueDraft cue) Selected?.Cues?.Remove(cue); Refresh(); }));
        var side = new StackPanel { Spacing = 12 }; side.Children.Add(_editor);
        side.Children.Add(Ui.Button("添加字幕", () =>
        {
            if (_busy || Selected?.Cues is not {} cues) return;
            var begin = TimeSpan.Zero;
            if (cues.LastOrDefault() is {} last && EditorTime.TryRead(last.End, 0, out var seconds)) begin = TimeSpan.FromSeconds(seconds);
            var cue = new CueDraft(new(begin, begin + TimeSpan.FromSeconds(1), "新字幕")); cues.Add(cue); _cues.SelectedItem = cue; Refresh();
        }));
        _format = Ui.Combo(["字幕文件 · SRT", "样式字幕 · ASS", "带字幕视频 · MP4", "带字幕视频 · MKV"], "字幕文件 · SRT");
        _format.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "srt", "ass", "mp4", "mkv" }, request.Options.Format));
        side.Children.Add(Ui.Text("输出内容")); side.Children.Add(_format);
        _folder = Ui.Input(request.OutputFolder); Localization.SetIsUserText(_folder, true);
        Avalonia.Automation.AutomationProperties.SetName(_folder, "字幕保存位置");
        _sourceFolder = new CheckBox { Content = "输出至源文件目录", IsChecked = request.OutputToSource };
        side.Children.Add(Ui.Text("保存位置")); side.Children.Add(_folder);
        side.Children.Add(Ui.Button("浏览…", async () =>
        {
            if (await Ui.Folder(this, "选择输出目录") is {} folder) { _folder.Text = folder; _sourceFolder.IsChecked = false; }
        }));
        side.Children.Add(_sourceFolder);
        _sourceFolder.IsCheckedChanged += (_, _) => _folder.IsEnabled = _sourceFolder.IsChecked != true;
        _folder.IsEnabled = _sourceFolder.IsChecked != true;
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
        _files.SelectionChanged += (_, _) => { _cues.ItemsSource = Selected?.Cues; _cues.SelectedIndex = Selected?.Cues?.Count > 0 ? 0 : -1; _notice.Text = Localization.Text(Selected?.Status ?? ""); Refresh(); };
        _cues.SelectionChanged += (_, _) =>
        {
            _editor.DataContext = _cues.SelectedItem;
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
                    CheckSource(source); source.Cues = new(cues.Select(cue => new CueDraft(cue))); source.Status = cues.Count == 0 ? "未识别到语音" : "待校对";
                }
                catch (OperationCanceledException) { source.Status = "待识别"; throw; }
                catch (Exception error) { source.Status = error.Message; }
                RefreshFiles(); if (Selected == source) { _cues.ItemsSource = source.Cues; _cues.SelectedIndex = source.Cues?.Count > 0 ? 0 : -1; }
            }
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) _notice.Text = Localization.Text("识别已停止，已完成结果保留"); }
        finally { _busy = false; _recognition = null; if (!_lifetime.IsCancellationRequested) Refresh(); }
    }
    private void Export()
    {
        if (_busy) return;
        try
        {
            var sources = _sources.Where(source => source.Cues?.Count > 0).ToArray();
            if (sources.Length == 0) throw new ArgumentException("请先识别或添加字幕。");
            var options = sources.Select(source =>
            {
                CheckSource(source); var edit = (_request.InputOptions?.ElementAtOrDefault(Array.IndexOf(_sources,source))??_request.Options).Clone(); edit.Format = new[] { "srt", "ass", "mp4", "mkv" }[_format.SelectedIndex];
                if (edit.Format != "srt") _style.ReadInto(edit);
                edit.Transcription ??= new(); edit.Transcription.ReviewedCues = ReadCues(source);
                edit.Transcription.ReviewedSourceLength = source.Length; edit.Transcription.ReviewedSourceWriteUtc = source.WriteUtc;
                edit.Transcription.Validate(); return edit;
            }).ToArray();
            var folder = _sourceFolder.IsChecked == true ? System.IO.Path.GetDirectoryName(sources[0].Path)! : _folder.Text?.Trim() ?? "";
            _ = ConversionBatch.CreateJobs(_request.Feature, sources.Select(source => source.Path).ToArray(), folder, options[0], options);
            Close(_request with { Files = sources.Select(source => source.Path).ToArray(), Options = options[0], InputOptions = options,
                OutputFolder = folder, OutputToSource = _sourceFolder.IsChecked == true, StartImmediately = ToolExecution.StartImmediately(this) });
        }
        catch(Exception error) { _notice.Text = error.Message; }
    }
    private SubtitleCue[] ReadCues(Source source)
    {
        var result = new List<SubtitleCue>();
        foreach (var draft in source.Cues!)
        {
            try { result.Add(draft.Read()); }
            catch (ArgumentException)
            {
                _files.SelectedItem = source; _cues.SelectedItem = draft; _cues.ScrollIntoView(draft);
                throw;
            }
        }
        return result.OrderBy(cue => cue.Start).ToArray();
    }
    private static void CheckSource(Source source)
    {
        var file = new FileInfo(source.Path);
        if (!file.Exists || file.Length != source.Length || file.LastWriteTimeUtc != source.WriteUtc) throw new IOException("源文件已改变，请重新打开并识别字幕。");
    }
    private void RefreshFiles() { _files.ItemsSource ??= _sources; }
    private void Refresh()
    {
        _stop.IsVisible = _busy; _retry.IsVisible = !_busy && _sources.Any(source => source.Cues is null);
        _export.IsEnabled = !_busy && _sources.Any(source => source.Cues?.Count > 0); _editor.IsEnabled = !_busy && _cues.SelectedItem is CueDraft;
        _format.IsEnabled = !_busy;
    }
}
