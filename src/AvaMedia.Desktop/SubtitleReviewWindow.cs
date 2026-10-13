using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
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
        public string Summary => Text.ReplaceLineEndings(" ").Trim();
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
    private readonly ComboBox _files = new() { Classes = { "ac" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _cues = new();
    private readonly AvaMedia.Controls.EmptyState _cueEmpty = new() { Text = "暂无字幕，请先识别或添加字幕" };
    private readonly TextBlock _cueCount = Ui.ComponentText("", "caption");
    private readonly TextBox _start = Ui.ComponentInput(), _end = Ui.ComponentInput(), _text = Ui.ComponentInput();
    private readonly TextBlock _notice = Ui.ComponentStatus();
    private readonly ComboBox _format;
    private readonly TextBox _folder;
    private readonly CheckBox _sourceFolder;
    private readonly Button _export, _retry;
    private readonly Grid _editor = new() { RowDefinitions = new("*,Auto,Auto,Auto"), RowSpacing = 6 };
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
        Title = "字幕校对"; Width = 1180; Height = 760; MinWidth = 980; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, "document");
        ToolExecution.SaveOnClose(this, FlushTaskEditsAsync);
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new(12), RowSpacing = 8 };
        var fileRow = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8 };
        fileRow.Children.Add(Ui.ComponentText("源文件")); Grid.SetColumn(_files, 1); fileRow.Children.Add(_files);
        _cueCount.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(_cueCount, 2); fileRow.Children.Add(_cueCount); root.Children.Add(fileRow);
        var content = new Grid { ColumnDefinitions = new("300,*,340"), ColumnSpacing = 8 };
        _files.ItemTemplate = new FuncDataTemplate<Source>((source, _) =>
        {
            if (source is null) return new TextBlock();
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
            var name = Ui.ComponentText(System.IO.Path.GetFileName(source.Path)); Localization.SetIsUserText(name, true);
            name.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis; row.Children.Add(name);
            var status = new AvaMedia.Controls.StatusPill { State = "waiting" }; status.Bind(AvaMedia.Controls.StatusPill.TextProperty, new Binding(nameof(Source.Status))); Grid.SetColumn(status, 1); row.Children.Add(status); return row;
        });
        _cues.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(6, 2)), new Setter(ListBoxItem.MinHeightProperty, 0d), new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _cues.ItemTemplate = new FuncDataTemplate<CueDraft>((cue, _) =>
        {
            var row = new Grid { ColumnDefinitions = new("148,*"), ColumnSpacing = 8 };
            var time = Ui.ComponentText("", "caption"); time.TextTrimming = TextTrimming.CharacterEllipsis; time.TextWrapping = TextWrapping.NoWrap;
            time.Bind(TextBlock.TextProperty, new Binding(nameof(CueDraft.Time))); row.Children.Add(time);
            var text = Ui.ComponentText(""); text.Bind(TextBlock.TextProperty, new Binding(nameof(CueDraft.Summary)));
            Localization.SetIsUserText(text, true); text.TextTrimming = TextTrimming.CharacterEllipsis; text.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(text, 1); row.Children.Add(text); return new AvaMedia.Controls.ListRow { Child = row };
        });
        var listHeader = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Margin = new(0, 0, 0, 4) };
        listHeader.Children.Add(Ui.ComponentText("字幕"));
        var addCue = Ui.Button("添加", () =>
        {
            if (_busy || Selected?.Cues is not {} cues) return;
            var begin = TimeSpan.Zero;
            if (cues.LastOrDefault() is {} last && EditorTime.TryRead(last.End, 0, out var seconds)) begin = TimeSpan.FromSeconds(seconds);
            var cue = new CueDraft(new(begin, begin + TimeSpan.FromSeconds(1), "新字幕")); cues.Add(cue); _cues.SelectedItem = cue; _cues.ScrollIntoView(cue); Refresh();
        });
        addCue.Classes.Add("field-action"); Grid.SetColumn(addCue, 1); listHeader.Children.Add(addCue);
        var cueColumn = new Grid { RowDefinitions = new("Auto,*") }; cueColumn.Children.Add(listHeader);
        Grid.SetRow(_cues, 1); cueColumn.Children.Add(_cues); Grid.SetRow(_cueEmpty, 1); cueColumn.Children.Add(_cueEmpty); content.Children.Add(cueColumn);
        Avalonia.Automation.AutomationProperties.SetName(_files,"字幕源文件");Avalonia.Automation.AutomationProperties.SetName(_cues,"字幕列表");
        Avalonia.Automation.AutomationProperties.SetName(_start,"开始时间");Avalonia.Automation.AutomationProperties.SetName(_end,"结束时间");Avalonia.Automation.AutomationProperties.SetName(_text,"字幕文字");
        _text.AcceptsReturn = true; _text.MinHeight = 52; _text.MaxHeight = 88; _text.TextWrapping = Avalonia.Media.TextWrapping.Wrap; Localization.SetIsUserText(_text, true);
        _start.IsReadOnly=_end.IsReadOnly=true;
        _preview=new MediaPreviewPanel(engine){Compact=true}; _editor.Children.Add(_preview);
        Grid.SetRow(_timing, 1); _editor.Children.Add(_timing);
        _timing.Changed+=()=>
        {
            if(_cues.SelectedItem is not CueDraft cue)return;
            cue.Start=EditorTime.Format(_timing.Start);cue.End=EditorTime.Format(_timing.End);
            _preview.SetPosition(request.Options.Start+_timing.Start*request.Options.Speed);
        };
        Grid.SetRow(_text, 2); _editor.Children.Add(_text);
        _start.Bind(TextBox.TextProperty, new Binding(nameof(CueDraft.Start)) { Mode = BindingMode.TwoWay });
        _end.Bind(TextBox.TextProperty, new Binding(nameof(CueDraft.End)) { Mode = BindingMode.TwoWay });
        _text.Bind(TextBox.TextProperty, new Binding(nameof(CueDraft.Text)) { Mode = BindingMode.TwoWay });
        var cueActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var play = Ui.Button("播放此处", async () =>
        {
            if (Selected is not {} source || _cues.SelectedItem is not CueDraft draft) return;
            try
            {
                var cue = draft.Read();
                var player = new PlayerWindow(engine, [source.Path]); player.ShowForPlayback(this); await player.Ready;
                await player.SeekAsync(request.Options.Start + cue.Start.TotalSeconds * request.Options.Speed, true);
            }
            catch(Exception error) { _notice.Text = error.Message; }
        });
        play.Classes.Add("field-action"); cueActions.Children.Add(play);
        var removeCue = Ui.Button("删除此条", () =>
        {
            if (_busy || _cues.SelectedItem is not CueDraft cue || Selected?.Cues is not {} cues) return;
            var index = cues.IndexOf(cue); cues.Remove(cue);
            _cues.SelectedIndex = Math.Min(index, cues.Count - 1); Refresh();
        });
        removeCue.Classes.Add("field-action"); cueActions.Children.Add(removeCue);
        Grid.SetRow(cueActions, 3); _editor.Children.Add(cueActions);
        _style = new SubtitleStyleEditor(request.Options) { Compact = true };
        var styleScroll = new ScrollViewer { Content = _style, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var output = new Grid { ColumnDefinitions = new("*,*,Auto"), ColumnSpacing = 8 };
        _format = Ui.ComponentCombo(["SRT", "ASS", "MP4", "MKV"], "SRT");
        AutomationProperties.SetName(_format, "输出内容"); ToolTip.SetTip(_format, "字幕文件 · SRT、样式字幕 · ASS、带字幕视频 · MP4、带字幕视频 · MKV");
        _format.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "srt", "ass", "mp4", "mkv" }, request.Options.Format));
        output.Children.Add(_format);
        _folder = Ui.ComponentInput(request.OutputFolder); _folder.IsReadOnly = true; Localization.SetIsUserText(_folder, true);
        Avalonia.Automation.AutomationProperties.SetName(_folder, "字幕保存位置");
        _sourceFolder = new CheckBox { Classes = { "ac" }, Content = "源目录", IsChecked = request.OutputToSource }; ToolTip.SetTip(_sourceFolder, "输出至源文件目录");
        Grid.SetColumn(_folder, 1); output.Children.Add(_folder);
        var browse = Ui.Button("浏览…", async () =>
        {
            if (await Ui.Folder(this, "选择输出目录") is {} folder) { _folder.Text = folder; _sourceFolder.IsChecked = false; }
        });
        browse.Classes.Add("field-action");
        var folderActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        folderActions.Children.Add(_sourceFolder); folderActions.Children.Add(browse);
        Grid.SetColumn(folderActions, 2); output.Children.Add(folderActions);
        void RefreshFolder() => _folder.IsEnabled = browse.IsEnabled = _sourceFolder.IsChecked != true;
        _sourceFolder.IsCheckedChanged += (_, _) => RefreshFolder(); RefreshFolder();
        _format.SelectionChanged+=(_,_)=>styleScroll.IsEnabled=_format.SelectedIndex!=0;styleScroll.IsEnabled=_format.SelectedIndex!=0;
        Grid.SetColumn(_editor, 1); content.Children.Add(_editor);
        Grid.SetColumn(styleScroll, 2); content.Children.Add(styleScroll);
        Grid.SetRow(content, 1); root.Children.Add(content); Grid.SetRow(output, 2); root.Children.Add(output);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 }; footer.Children.Add(_notice);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
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
        var count = Selected?.Cues?.Count ?? 0;
        _cueEmpty.IsVisible = count == 0;
        _cueCount.Text = count > 0 ? Localization.Format($"{count} 条") : "";
    }
}
