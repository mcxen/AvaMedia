using System.Collections.ObjectModel;
using Avalonia.Automation;
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
        public override string ToString() => System.IO.Path.GetFileName(Path);
    }
    private readonly IMediaEngine _engine;
    private readonly ConversionRequest _request;
    private readonly Source[] _sources;
    private readonly ComboBox _files = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _cues = new();
    private readonly TextBox _start = Ui.Input(), _end = Ui.Input(), _text = Ui.Input();
    private readonly TextBlock _notice = Ui.Status();
    private readonly ComboBox _format;
    private readonly TextBox _folder;
    private readonly CheckBox _sourceFolder;
    private readonly Button _export, _retry;
    private readonly StackPanel _editor = new() { Spacing = 10 };
    private readonly SubtitleStyleEditor _style;
    private readonly TimeRangePicker _timing = new();
    private readonly MediaPreviewPanel _preview;
    private double _duration = 1;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<ConversionRequest, Task>? _recognize;
    private readonly Func<SubtitleTaskResult, Task>? _saveDraft;
    private bool _busy;
    private Source? Selected => _files.SelectedItem as Source;

    public SubtitleReviewWindow(IMediaEngine engine, ConversionRequest request, bool editing, Func<ConversionRequest, Task>? recognize = null,
        Func<SubtitleTaskResult, Task>? saveDraft = null)
    {
        _engine = engine; _request = request; _recognize = recognize; _saveDraft = saveDraft; _sources = request.Files.Select((path,index) =>
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
        Title = "字幕校对"; Width = 1060; Height = 760; MinWidth = 850; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, "document");
        ToolExecution.SaveOnClose(this, FlushTaskEditsAsync);
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new(20), RowSpacing = 12 };
        root.Children.Add(_files);
        var content = new Grid { ColumnDefinitions = new("*,320"), ColumnSpacing = 16 };
        _files.ItemTemplate = new FuncDataTemplate<Source>((source, _) =>
        {
            if (source is null) return new TextBlock();
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
            var name = Ui.Text(System.IO.Path.GetFileName(source.Path)); Localization.SetIsUserText(name, true);
            name.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis; row.Children.Add(name);
            var status = Ui.Text("", "caption"); status.Bind(TextBlock.TextProperty, new Binding(nameof(Source.Status))); Grid.SetColumn(status, 1); row.Children.Add(status); return row;
        });
        _cues.ItemTemplate = new FuncDataTemplate<CueDraft>((cue, _) =>
        {
            var row = new StackPanel { Spacing = 4, Margin = new(2, 6) };
            var time = Ui.Text("", "caption"); time.Bind(TextBlock.TextProperty, new Binding(nameof(CueDraft.Time))); row.Children.Add(time);
            var text = Ui.Text(""); text.Bind(TextBlock.TextProperty, new Binding(nameof(CueDraft.Text)));
            Localization.SetIsUserText(text, true); text.TextWrapping = Avalonia.Media.TextWrapping.Wrap; row.Children.Add(text); return row;
        });
        content.Children.Add(_cues);
        Avalonia.Automation.AutomationProperties.SetName(_files,"字幕源文件");Avalonia.Automation.AutomationProperties.SetName(_cues,"字幕列表");
        Avalonia.Automation.AutomationProperties.SetName(_start,"开始时间");Avalonia.Automation.AutomationProperties.SetName(_end,"结束时间");Avalonia.Automation.AutomationProperties.SetName(_text,"字幕文字");
        _text.AcceptsReturn = true; _text.MinHeight = 100; _text.TextWrapping = Avalonia.Media.TextWrapping.Wrap; Localization.SetIsUserText(_text, true);
        _start.IsReadOnly=_end.IsReadOnly=true;
        _preview=new MediaPreviewPanel(engine){Height=180}; _editor.Children.Add(_preview);_editor.Children.Add(_timing);
        _timing.Changed+=()=>
        {
            if(_cues.SelectedItem is not CueDraft cue)return;
            cue.Start=EditorTime.Format(_timing.Start);cue.End=EditorTime.Format(_timing.End);
            _preview.SetPosition(request.Options.Start+_timing.Start*request.Options.Speed);
        };
        _editor.Children.Add(_text);
        _start.Bind(TextBox.TextProperty, new Binding(nameof(CueDraft.Start)) { Mode = BindingMode.TwoWay });
        _end.Bind(TextBox.TextProperty, new Binding(nameof(CueDraft.End)) { Mode = BindingMode.TwoWay });
        _text.Bind(TextBox.TextProperty, new Binding(nameof(CueDraft.Text)) { Mode = BindingMode.TwoWay });
        var cueActions = new WrapPanel();
        cueActions.Children.Add(Ui.Button("播放此处", async () =>
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
        cueActions.Children.Add(Ui.Button("删除此条", () =>
        {
            if (_busy || _cues.SelectedItem is not CueDraft cue || Selected?.Cues is not {} cues) return;
            var index = cues.IndexOf(cue); cues.Remove(cue);
            _cues.SelectedIndex = Math.Min(index, cues.Count - 1); Refresh();
        }));
        foreach (var button in cueActions.Children) button.Margin = new(0, 0, 8, 0);
        _editor.Children.Add(cueActions);
        var side = new StackPanel { Spacing = 12 }; side.Children.Add(_editor);
        side.Children.Add(Ui.Button("添加字幕", () =>
        {
            if (_busy || Selected?.Cues is not {} cues) return;
            var begin = TimeSpan.Zero;
            if (cues.LastOrDefault() is {} last && EditorTime.TryRead(last.End, 0, out var seconds)) begin = TimeSpan.FromSeconds(seconds);
            var cue = new CueDraft(new(begin, begin + TimeSpan.FromSeconds(1), "新字幕")); cues.Add(cue); _cues.SelectedItem = cue; Refresh();
        }));
        var output = new StackPanel { Spacing = 8 };
        var formatRow = new Grid { ColumnDefinitions = new("88,*,Auto"), ColumnSpacing = 8 };
        formatRow.Children.Add(Ui.Text("输出内容"));
        _format = Ui.Combo(["字幕文件 · SRT", "样式字幕 · ASS", "带字幕视频 · MP4", "带字幕视频 · MKV"], "字幕文件 · SRT");
        AutomationProperties.SetName(_format, "输出内容");
        _format.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "srt", "ass", "mp4", "mkv" }, request.Options.Format));
        Grid.SetColumn(_format, 1); formatRow.Children.Add(_format); output.Children.Add(formatRow);
        _folder = Ui.Input(request.OutputFolder); _folder.IsReadOnly = true; Localization.SetIsUserText(_folder, true);
        Avalonia.Automation.AutomationProperties.SetName(_folder, "字幕保存位置");
        _sourceFolder = new CheckBox { Content = "输出至源文件目录", IsChecked = request.OutputToSource };
        var folderRow = new Grid { ColumnDefinitions = new("88,*,Auto"), ColumnSpacing = 8 };
        folderRow.Children.Add(Ui.Text("保存位置")); Grid.SetColumn(_folder, 1); folderRow.Children.Add(_folder);
        var browse = Ui.Button("浏览…", async () =>
        {
            if (await Ui.Folder(this, "选择输出目录") is {} folder) { _folder.Text = folder; _sourceFolder.IsChecked = false; }
        });
        Grid.SetColumn(browse, 2); folderRow.Children.Add(browse); output.Children.Add(folderRow); output.Children.Add(_sourceFolder);
        void RefreshFolder() => _folder.IsEnabled = browse.IsEnabled = _sourceFolder.IsChecked != true;
        _sourceFolder.IsCheckedChanged += (_, _) => RefreshFolder(); RefreshFolder();
        _style = new SubtitleStyleEditor(request.Options);
        var styles=Ui.Button("字幕样式…",async()=>
        {
            var window=new Window { Title="字幕样式",Width=780,Height=650,MinWidth=700,MinHeight=480,WindowStartupLocation=WindowStartupLocation.CenterOwner };
            var scroll=new ScrollViewer { Content=_style };var layout=new Grid { RowDefinitions=new("*,Auto"),Margin=new(20),RowSpacing=12 };layout.Children.Add(scroll);
            var done=Ui.DialogButton("关闭",window.Close);done.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetRow(done,1);layout.Children.Add(done);window.Content=layout;
            window.Opened+=async(_,_)=>await _style.SetVideoAsync(_engine,Selected?.Path,_request.Options.VideoStreamIndex,_lifetime.Token);
            window.Closed+=(_,_)=>scroll.Content=null;await window.ShowDialog(this);
        }); Grid.SetColumn(styles, 2); formatRow.Children.Add(styles);
        _format.SelectionChanged+=(_,_)=>styles.IsEnabled=_format.SelectedIndex!=0;styles.IsEnabled=_format.SelectedIndex!=0;
        content.Children.Add(new ScrollViewer { Content = side, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, [Grid.ColumnProperty] = 1 });
        Grid.SetRow(content, 1); root.Children.Add(content); Grid.SetRow(output, 2); root.Children.Add(output);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 }; footer.Children.Add(_notice);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _retry = Ui.Button("后台重新识别", async () => await RecognizeAsync()); actions.Children.Add(_retry);
        var cancel = Ui.DialogButton("取消", () => Close(null)); cancel.MinWidth = 88; actions.Children.Add(cancel);
        _export = Ui.DialogButton(editing ? "保存修改" : "导出字幕", Export); _export.Classes.Add("primary");_export.IsDefault=true;actions.Children.Add(_export);
        ToolExecution.Configure(this, _export, "导出字幕", editing);
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        _files.SelectionChanged += async (_, _) => await SelectSourceAsync();
        _cues.SelectionChanged += (_, _) =>
        {
            _editor.DataContext = _cues.SelectedItem; UpdateTiming();
            Refresh();
        };
        Closed += (_, _) => { _lifetime.Cancel(); _style.Dispose(); _preview.Dispose(); };
        RefreshFiles(); _files.SelectedIndex = 0; Refresh();
    }

    private async Task SelectSourceAsync()
    {
        var source = Selected;
        _preview.SetSource(source?.Path);
        _cues.ItemsSource = source?.Cues;
        _cues.SelectedIndex = source?.Cues?.Count > 0 ? 0 : -1;
        _notice.Text = Localization.Text(source?.Status ?? ""); Refresh();
        if (source is null) return;
        try
        {
            var info = await _engine.Probe(source.Path, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || Selected != source) return;
            _duration = Math.Max(.01, (info.Duration - _request.Options.Start) / _request.Options.Speed);
            UpdateTiming();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_lifetime.IsCancellationRequested && Selected == source) _notice.Text = error.Message; }
    }

    private void UpdateTiming()
    {
        if(_cues.SelectedItem is not CueDraft cue)return;
        if(EditorTime.TryRead(cue.Start,0,out var start)&&EditorTime.TryRead(cue.End,0,out var end))
        { _timing.SetRange(start,end,_duration); _preview.SetPosition(_request.Options.Start+start*_request.Options.Speed); }
    }

    public async Task FlushTaskEditsAsync()
    {
        if (_saveDraft is null || _sources.Length != 1 || _sources[0].Cues is null) return;
        var source = _sources[0];
        await _saveDraft(new(ReadCues(source), source.Length, source.WriteUtc));
    }

    private async Task RecognizeAsync()
    {
        if (_busy || _lifetime.IsCancellationRequested || _recognize is null) return;
        _busy = true; Refresh();
        try
        {
            var options = _request.Options.Clone(); options.Format = "srt"; options.Transcription ??= new();
            options.Transcription.RecognitionOnly = true; options.Transcription.ReviewedCues = null;
            await _recognize(_request with { Options = options, InputOptions = null, StartImmediately = true });
            Close();
        }
        catch (Exception error) { _notice.Text = error.Message; }
        finally { _busy = false; if (!_lifetime.IsCancellationRequested) Refresh(); }
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
                edit.Transcription ??= new(); edit.Transcription.RecognitionOnly = false; edit.Transcription.ReviewedCues = ReadCues(source);
                edit.Transcription.ReviewedSourceLength = source.Length; edit.Transcription.ReviewedSourceWriteUtc = source.WriteUtc;
                edit.Transcription.Validate(); return edit;
            }).ToArray();
            var folder = _sourceFolder.IsChecked == true ? System.IO.Path.GetDirectoryName(sources[0].Path)! : _folder.Text?.Trim() ?? "";
            _ = ConversionBatch.CreateJobs(_request.Feature, sources.Select(source => source.Path).ToArray(), folder, options[0], options);
            ToolExecution.Complete(this, _request with { Files = sources.Select(source => source.Path).ToArray(), Options = options[0], InputOptions = options,
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
        _retry.IsVisible = _recognize is not null; _retry.IsEnabled = !_busy;
        _export.IsEnabled = !_busy && _sources.Any(source => source.Cues?.Count > 0); _editor.IsEnabled = !_busy && _cues.SelectedItem is CueDraft;
        _format.IsEnabled = !_busy;
    }
}
