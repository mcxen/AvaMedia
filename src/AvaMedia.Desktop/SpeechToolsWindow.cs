using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed class SpeechToolsWindow : Window
{
    private readonly IMediaEngine _engine;
    private readonly Feature _feature;
    private readonly ConversionOptions _initial;
    private readonly bool _transcribe;
    private readonly ObservableCollection<string> _files = [];
    private readonly ListBox _sources = new() { Name = "SpeechFiles", MinHeight = 70, MaxHeight = 140 };
    private readonly ComboBox _output;
    private readonly ComboBox _language;
    private readonly ComboBox _model;
    private readonly SubtitleStyleEditor _style;
    private readonly VoiceEnhancementControl _voice;
    private readonly TextBox _folder;
    private readonly TextBlock _notice = Ui.Text("", "caption");
    private readonly Button _confirm;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    public Task PreviewReady => _style.PreviewReady;

    public SpeechToolsWindow(IMediaEngine engine, Feature feature, string outputFolder, IEnumerable<string>? files = null, ConversionOptions? options = null, bool editing = false)
    {
        _engine = engine; _feature = feature; _transcribe = feature.Operation == Operation.Transcribe;
        _initial = options?.Clone() ?? new() { Format = feature.Format, Transcription = _transcribe ? new() : null, SubtitleFontSize = 48, SubtitleMargin = 36, VoiceEnhancement = !_transcribe };
        if (_transcribe && options is null && files?.Any() == true && !files.Any(VideoFormats.IsVideo)) _initial.Format = "srt";
        Title = editing ? Localization.Format($"编辑任务 · {Localization.Key(feature.Label)}") : feature.Label;
        Width = 940; Height = _transcribe ? 740 : 500; MinWidth = 780; MinHeight = _transcribe ? 660 : 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, feature.Icon);
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new(20), RowSpacing = 14 };
        var source = new StackPanel { Spacing = 8 }; root.Children.Add(source);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var add = new Button { Content = "添加文件…", Classes = { "field-action" } };
        add.Click += async (_, _) => { if (!_busy) AddFiles(await Ui.Pick(this, "选择音视频文件", true)); }; toolbar.Children.Add(add);
        var remove = new Button { Content = "移除", IsEnabled = false, Classes = { "field-action" } };
        remove.Click += (_, _) => { if (!_busy && _sources.SelectedItem is string path) _files.Remove(path); if (_sources.SelectedItem is null && _files.Count > 0) _sources.SelectedIndex = 0; Refresh(); }; toolbar.Children.Add(remove);
        source.Children.Add(toolbar);
        _sources.ItemsSource = _files;
        _sources.ItemTemplate = new FuncDataTemplate<string>((path, _) =>
        {
            var text = Ui.Text(Path.GetFileName(path!)); Localization.SetIsUserText(text, true); ToolTip.SetTip(text, path); return text;
        });
        _sources.SelectionChanged += (_, _) =>
        {
            remove.IsEnabled = !_busy && _sources.SelectedItem is not null;
            if (_transcribe && _style is not null) _ = _style.SetVideoAsync(_engine, _sources.SelectedItem as string, _initial.VideoStreamIndex, _lifetime.Token);
        };
        source.Children.Add(_sources);
        var fields = new StackPanel { Spacing = 14 }; Grid.SetRow(fields, 1);
        root.Children.Add(new ScrollViewer { Content = fields, [Grid.RowProperty] = 1 });
        string[] outputs = _transcribe ? ["带字幕视频 (MP4)", "带字幕视频 (MKV)", "字幕文件 (SRT)", "样式字幕 (ASS)"]
            : feature.Category == "音频" ? ["原格式", "WAV", "M4A", "MP3", "FLAC"] : ["原格式", "MP4", "MKV", "MOV", "WAV", "M4A", "MP3"];
        _output = Ui.Combo(outputs, outputs[0]); _output.Name = "SpeechOutput";
        if (_transcribe && _initial.Format == "srt") _output.SelectedIndex = 2;
        if (options is not null) _output.SelectedIndex = _transcribe ? Array.IndexOf(new[] { "mp4", "mkv", "srt", "ass" }, options.Format)
            : Math.Max(0, Array.IndexOf(outputs, options.Format.ToUpperInvariant()));
        Add(fields, "输出内容", _output);
        _language = Ui.Combo(["自动识别", "中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语", "俄语"], "自动识别"); _language.Name = "SpeechLanguage";
        _language.SelectedIndex = Math.Max(0, Array.IndexOf(TranscriptionOptions.Languages, _initial.Transcription?.Language ?? "auto"));
        _model = Ui.Combo(["标准 · 60 MB", "轻量 · 32 MB"], _initial.Transcription?.Model == SpeechModel.Tiny ? "轻量 · 32 MB" : "标准 · 60 MB"); _model.Name = "SpeechModel";
        _style = new SubtitleStyleEditor(_initial); _voice = new VoiceEnhancementControl(_initial, !_transcribe);
        if (_transcribe)
        {
            var recognition = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 16 };
            var languages = new StackPanel(); Add(languages, "识别语言", _language); recognition.Children.Add(languages);
            var models = new StackPanel(); Add(models, "识别模型", _model); Grid.SetColumn(models, 1); recognition.Children.Add(models);
            fields.Children.Add(recognition); fields.Children.Add(_style);
        }
        fields.Children.Add(_voice);
        if (!_transcribe) fields.Children.Add(Ui.Text("适合讲话降噪；背景音乐需要人声分离。", "caption"));
        _output.SelectionChanged += (_, _) => Refresh();
        var outputRow = new Grid { ColumnDefinitions = new("72,*,Auto"), ColumnSpacing = 8 };
        outputRow.Children.Add(Ui.Text("保存位置"));
        _folder = Ui.Input(outputFolder); _folder.Name = "SpeechOutputFolder"; Grid.SetColumn(_folder, 1); outputRow.Children.Add(_folder);
        var browse = new Button { Content = "浏览…", Classes = { "field-action" } };
        browse.Click += async (_, _) => { if (!_busy && await Ui.Folder(this, "选择输出目录") is { } selected) _folder.Text = selected; };
        Grid.SetColumn(browse, 2); outputRow.Children.Add(browse); Grid.SetRow(outputRow, 2); root.Children.Add(outputRow);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 }; footer.Children.Add(_notice);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(Ui.DialogButton("取消", () => Close(null)));
        _confirm = new Button { Name = "SpeechConfirm", Content = "加入队列", IsDefault = true, Classes = { "primary", "dialog-action" } };
        _confirm.Click += async (_, _) =>
        {
            if (_busy) return; _busy = true; Refresh(); fields.IsEnabled = source.IsEnabled = outputRow.IsEnabled = false;
            try
            {
                var request = ReadRequest();
                foreach (var path in request.Files)
                {
                    var info = await engine.Probe(path, _lifetime.Token).ConfigureAwait(true);
                    if (!info.HasAudio) throw new ArgumentException("文件没有可处理的音轨。");
                    if (_transcribe && request.Options.Format is "mp4" or "mkv" && !info.HasVideo) throw new ArgumentException("音频文件请选择 SRT 或 ASS 字幕输出。");
                }
                _ = ConversionBatch.CreateJobs(feature, request.Files, request.OutputFolder, request.Options, request.InputOptions);
                Close(request);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { _notice.Text = error.Message; }
            finally { _busy = false; if (IsVisible) { fields.IsEnabled = source.IsEnabled = outputRow.IsEnabled = true; Refresh(); } }
        };
        actions.Children.Add(_confirm); Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = !_busy && e.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, (_, e) => { e.Handled = true; if (!_busy) AddFiles(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); }, RoutingStrategies.Bubble, handledEventsToo: true);
        Closed += (_, _) => { _lifetime.Cancel(); _style.Dispose(); };
        AddFiles(files ?? []); Refresh();
    }

    public void AddFiles(IEnumerable<string> files)
    {
        var known = _files.ToHashSet(VideoFolderScanner.PathComparer);
        foreach (var path in files.Where(File.Exists).Select(Path.GetFullPath).Where(known.Add)) _files.Add(path);
        if (_sources.SelectedItem is null && _files.Count > 0) _sources.SelectedIndex = 0;
        Refresh();
    }

    public ConversionRequest ReadRequest()
    {
        var options = _initial.Clone(); options.CopyStreams = false; options.SubtitleMode = SubtitleMode.None; options.Subtitle = "";
        _voice.ReadInto(options);
        if (_transcribe)
        {
            options.Format = new[] { "mp4", "mkv", "srt", "ass" }[Math.Max(0, _output.SelectedIndex)];
            options.Transcription = new() { Language = TranscriptionOptions.Languages[_language.SelectedIndex], Model = _model.SelectedIndex == 1 ? SpeechModel.Tiny : SpeechModel.Base };
            if (_style.IsVisible) _style.ReadInto(options);
            if (options.Format is "srt" or "ass") options.VoiceEnhancement = false;
            return new(_feature, _files.ToArray(), _folder.Text?.Trim() ?? "", options);
        }
        options.VideoCodec = "copy"; options.AudioCodec = "自动";
        var inputs = _files.Select(path =>
        {
            var edit = options.Clone();
            edit.Format = _output.SelectedIndex == 0 ? Path.GetExtension(path).TrimStart('.').ToLowerInvariant() : ((string)_output.SelectedItem!).ToLowerInvariant();
            if (!MediaEngine.IsAudio(edit.Format) && !VideoFormats.OriginalOutputExtensions.Contains(edit.Format)) throw new ArgumentException("此原格式不支持人声增强，请选择 MP4、MKV 或音频格式。");
            return edit;
        }).ToArray();
        return new(_feature, _files.ToArray(), _folder.Text?.Trim() ?? "", inputs.FirstOrDefault() ?? options, InputOptions: inputs);
    }

    private void Refresh()
    {
        if (_confirm is not null) _confirm.IsEnabled = !_busy && _files.Count > 0;
        if (_style is not null) _style.IsVisible = _transcribe && _output.SelectedIndex != 2;
        if (_voice is not null) _voice.IsVisible = !_transcribe || _output.SelectedIndex is 0 or 1;
    }

    private static void Add(Panel panel, string label, Control control)
    {
        var row = new Grid { ColumnDefinitions = new("80,*"), ColumnSpacing = 8 };
        row.Children.Add(Ui.Text(label)); Grid.SetColumn(control, 1); row.Children.Add(control); panel.Children.Add(row);
    }
}
