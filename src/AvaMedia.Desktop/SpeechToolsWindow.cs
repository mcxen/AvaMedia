using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
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
    private readonly bool _transcribe, _editing;
    private readonly ObservableCollection<string> _files = [];
    private readonly ListBox _sources = new() { Name = "SpeechFiles", MinHeight = 72, MaxHeight = 130, SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _count = Ui.Text("", "caption");
    private readonly TextBlock _empty = Ui.Text("添加或拖入音视频文件", "caption");
    private readonly Button _remove = new() { Content = "移除选中", IsEnabled = false, Classes = { "field-action" } };
    private readonly ComboBox _output, _language, _model;
    private readonly SubtitleStyleEditor _style;
    private readonly Expander _styleSection, _voiceSection;
    private readonly VoiceEnhancementControl _voice;
    private readonly TextBox _folder;
    private readonly CheckBox _sourceFolder;
    private readonly Button _browse, _confirm;
    private readonly TextBlock _notice = Ui.Text("", "caption");
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy, _outputChosen;
    private string _error = "";
    public Task PreviewReady => _style.PreviewReady;

    public SpeechToolsWindow(IMediaEngine engine, Feature feature, string outputFolder, IEnumerable<string>? files = null, ConversionOptions? options = null, bool editing = false)
    {
        _engine = engine; _feature = feature; _transcribe = feature.Operation == Operation.Transcribe; _editing = editing;
        _initial = options?.Clone() ?? new() { Format = feature.Format, Transcription = _transcribe ? new() : null, SubtitleFontSize = 48, SubtitleMargin = 36, VoiceEnhancement = !_transcribe };
        _outputChosen = options is not null;
        Title = editing ? Localization.Format($"编辑任务 · {Localization.Key(feature.Label)}") : feature.Label;
        Width = 960; Height = _transcribe ? 760 : 520; MinWidth = 800; MinHeight = _transcribe ? 620 : 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, feature.Icon);
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new(20), RowSpacing = 16 };
        var source = new StackPanel { Spacing = 8 }; root.Children.Add(source);
        var toolbar = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        var imports = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var add = new Button { Content = "添加文件…", Classes = { "field-action" } };
        add.Click += async (_, _) =>
        {
            if (_busy) return;
            var picked = await StorageProvider.OpenFilePickerAsync(new() { Title = Localization.Text("选择音视频文件"), AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType(Localization.Text("音视频文件")) { Patterns = VideoFormats.InputExtensions.Select(ext => "*." + ext)
                    .Concat(new[] { "*.wav", "*.m4a", "*.mp3", "*.flac", "*.aac", "*.ogg", "*.opus", "*.wma", "*.aiff", "*.aif", "*.m4b" }).ToArray() }] });
            if (!_lifetime.IsCancellationRequested) AddFiles(picked.Select(file => file.TryGetLocalPath()).OfType<string>());
        };
        imports.Children.Add(add);
        _remove.Click += (_, _) =>
        {
            if (_busy) return;
            foreach (var path in _sources.SelectedItems?.OfType<string>().ToArray() ?? []) _files.Remove(path);
            SelectFirst(); _error = ""; Refresh();
        };
        imports.Children.Add(_remove); toolbar.Children.Add(imports); Grid.SetColumn(_count, 1); toolbar.Children.Add(_count); source.Children.Add(toolbar);
        _sources.ItemsSource = _files;
        _sources.ItemTemplate = new FuncDataTemplate<string>((path, _) =>
        {
            var text = Ui.Text(Path.GetFileName(path!)); text.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis; text.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
            Localization.SetIsUserText(text, true); ToolTip.SetTip(text, path); return text;
        });
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center; _empty.IsHitTestVisible = false;
        var fileArea = new Grid(); fileArea.Children.Add(_sources); fileArea.Children.Add(_empty); source.Children.Add(fileArea);
        var fields = new StackPanel { Spacing = 16 };
        root.Children.Add(new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, [Grid.RowProperty] = 1 });
        string[] outputs = _transcribe ? ["烧录字幕 · MP4", "烧录字幕 · MKV", "字幕文件 · SRT", "样式字幕 · ASS"]
            : feature.Category == "音频" ? ["原格式", "WAV", "M4A", "MP3", "FLAC"] : ["原格式", "MP4", "MKV", "MOV", "WAV", "M4A", "MP3"];
        _output = Ui.Combo(outputs, outputs[0]); _output.Name = "SpeechOutput";
        if (_transcribe) _output.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "mp4", "mkv", "srt", "ass" }, _initial.Format));
        else if (options is not null) _output.SelectedIndex = Math.Max(0, Array.IndexOf(outputs, options.Format.ToUpperInvariant()));
        Add(fields, "输出内容", _output);
        _language = Ui.Combo(["自动识别", "中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语", "俄语"], "自动识别"); _language.Name = "SpeechLanguage";
        _language.SelectedIndex = Math.Max(0, Array.IndexOf(TranscriptionOptions.Languages, _initial.Transcription?.Language ?? "auto"));
        _model = Ui.Combo(["标准 · Base · 60 MB", "快速 · Tiny · 32 MB", "Small · 190 MB"], _initial.Transcription?.Model switch
        { SpeechModel.Tiny => "快速 · Tiny · 32 MB", SpeechModel.Small => "Small · 190 MB", _ => "标准 · Base · 60 MB" }); _model.Name = "SpeechModel";
        _style = new SubtitleStyleEditor(_initial); _voice = new VoiceEnhancementControl(_initial, !_transcribe);
        _styleSection = new Expander { Header = "字幕样式", Content = _style, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        _voiceSection = new Expander { Header = "音频处理", Content = _voice, IsExpanded = _initial.VoiceEnhancement, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (_transcribe)
        {
            var recognition = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 20 };
            var languages = new StackPanel(); Add(languages, "识别语言", _language); recognition.Children.Add(languages);
            var models = new StackPanel(); Add(models, "识别模型", _model); Grid.SetColumn(models, 1); recognition.Children.Add(models);
            fields.Children.Add(recognition); fields.Children.Add(_styleSection); fields.Children.Add(_voiceSection);
        }
        else fields.Children.Add(_voice);
        var saving = new StackPanel { Spacing = 8 };
        var outputRow = new Grid { ColumnDefinitions = new("80,*,Auto"), ColumnSpacing = 8 };
        outputRow.Children.Add(Ui.Text("保存位置"));
        _folder = Ui.Input(outputFolder); _folder.Name = "SpeechOutputFolder"; Localization.SetIsUserText(_folder, true);
        Grid.SetColumn(_folder, 1); outputRow.Children.Add(_folder);
        _browse = new Button { Content = "浏览…", Classes = { "field-action" } };
        _browse.Click += async (_, _) => { if (!_busy && await Ui.Folder(this, "选择输出目录") is { } selected && !_lifetime.IsCancellationRequested) _folder.Text = selected; };
        Grid.SetColumn(_browse, 2); outputRow.Children.Add(_browse); saving.Children.Add(outputRow);
        _sourceFolder = new CheckBox { Name = "SpeechSourceFolder", Content = "输出至源文件目录", IsChecked = !editing && engine.Settings.OutputToSource };
        saving.Children.Add(_sourceFolder); Grid.SetRow(saving, 2); root.Children.Add(saving);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 }; footer.Children.Add(_notice);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(Ui.DialogButton("取消", () => Close(null)));
        _confirm = new Button { Name = "SpeechConfirm", Content = editing ? "保存修改" : "加入队列", IsDefault = true, Classes = { "primary", "dialog-action" } };
        _confirm.Click += async (_, _) =>
        {
            if (_busy) return; _busy = true; _error = ""; Refresh(); fields.IsEnabled = source.IsEnabled = saving.IsEnabled = false;
            try
            {
                var request = ReadRequest();
                for (var index = 0; index < request.Files.Length; index++)
                {
                    var path = request.Files[index];
                    _notice.Text = Localization.Format($"检查音轨 {index + 1}/{request.Files.Length} · {Path.GetFileName(path)}"); _notice.IsVisible = true;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    MediaInfo info;
                    try { info = await engine.Probe(path, timeout.Token).ConfigureAwait(true); }
                    catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { throw new ArgumentException(Localization.Format($"{Path.GetFileName(path)}：媒体读取超时。")); }
                    if (!info.HasAudio) { SelectInvalidFile(path); throw new ArgumentException(Localization.Format($"{Path.GetFileName(path)}：没有可处理的音轨。")); }
                    if (_transcribe && request.Options.Format is "mp4" or "mkv" && !info.HasVideo)
                    { SelectInvalidFile(path); throw new ArgumentException(Localization.Format($"{Path.GetFileName(path)}：请选择 SRT 或 ASS 字幕输出。")); }
                }
                _ = ConversionBatch.CreateJobs(feature, request.Files, request.OutputFolder, request.Options, request.InputOptions);
                Close(request);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { _error = error.Message; }
            finally { _busy = false; if (IsVisible) { fields.IsEnabled = source.IsEnabled = saving.IsEnabled = true; Refresh(); } }
        };
        actions.Children.Add(_confirm); Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        _sources.SelectionChanged += (_, _) =>
        {
            _remove.IsEnabled = !_busy && _sources.SelectedItems?.Count > 0;
            if (_transcribe) _ = _style.SetVideoAsync(_engine, _sources.SelectedItem as string, _initial.VideoStreamIndex, _lifetime.Token);
        };
        _output.SelectionChanged += (_, _) => { _outputChosen = true; _error = ""; Refresh(); };
        _folder.TextChanged += (_, _) => { _error = ""; Refresh(); };
        _sourceFolder.IsCheckedChanged += (_, _) => { _error = ""; Refresh(); };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = !_busy && e.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, (_, e) => { e.Handled = true; if (!_busy) AddFiles(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); }, RoutingStrategies.Bubble, handledEventsToo: true);
        Closed += (_, _) => { _lifetime.Cancel(); _style.Dispose(); Localization.Changed -= LanguageChanged; };
        Localization.Changed += LanguageChanged;
        AddFiles(files ?? []); Refresh();
    }

    private void LanguageChanged(object? sender, EventArgs args) => Refresh();
    private void SelectFirst() { if (_sources.SelectedItem is null && _files.Count > 0) _sources.SelectedIndex = 0; }
    private void SelectInvalidFile(string path) { _sources.SelectedItems?.Clear(); _sources.SelectedItem = path; _sources.ScrollIntoView(path); }
    public void AddFiles(IEnumerable<string> files)
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        var known = _files.ToHashSet(VideoFolderScanner.PathComparer);
        foreach (var path in files.Where(File.Exists).Select(Path.GetFullPath).Where(known.Add)) _files.Add(path);
        if (_transcribe && !_outputChosen && _files.Count > 0 && !_files.Any(VideoFormats.IsVideo))
        { _output.SelectedIndex = 2; _outputChosen = false; }
        SelectFirst(); _error = ""; Refresh();
    }

    public ConversionRequest ReadRequest()
    {
        if (_files.Count == 0) throw new ArgumentException("请添加音视频文件。");
        var folder = _sourceFolder.IsChecked == true ? Path.GetDirectoryName(_files[0])! : _folder.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("请选择输出目录。");
        var options = _initial.Clone(); options.CopyStreams = false; options.SubtitleMode = SubtitleMode.None; options.Subtitle = "";
        _voice.ReadInto(options);
        if (_transcribe)
        {
            options.Format = new[] { "mp4", "mkv", "srt", "ass" }[Math.Max(0, _output.SelectedIndex)];
            options.Transcription = new() { Language = TranscriptionOptions.Languages[_language.SelectedIndex], Model = _model.SelectedIndex switch
            { 1 => SpeechModel.Tiny, 2 => SpeechModel.Small, _ => SpeechModel.Base } };
            if (_styleSection.IsVisible) _style.ReadInto(options);
            if (options.Format is "srt" or "ass") options.VoiceEnhancement = false;
            return new(_feature, _files.ToArray(), folder, options, OutputToSource: _sourceFolder.IsChecked == true);
        }
        options.VideoCodec = "copy"; options.AudioCodec = "自动";
        var inputs = _files.Select(path =>
        {
            var edit = options.Clone();
            edit.Format = _output.SelectedIndex == 0 ? Path.GetExtension(path).TrimStart('.').ToLowerInvariant() : ((string)_output.SelectedItem!).ToLowerInvariant();
            if (!MediaEngine.IsAudio(edit.Format) && !VideoFormats.OriginalOutputExtensions.Contains(edit.Format)) throw new ArgumentException("此原格式不支持人声增强，请选择 MP4、MKV 或音频格式。");
            return edit;
        }).ToArray();
        return new(_feature, _files.ToArray(), folder, inputs[0], OutputToSource: _sourceFolder.IsChecked == true, InputOptions: inputs);
    }

    private void Refresh()
    {
        var hasFolder = _sourceFolder.IsChecked == true || !string.IsNullOrWhiteSpace(_folder.Text);
        _confirm.IsEnabled = !_busy && _files.Count > 0 && hasFolder;
        _confirm.Content = Localization.Text(_busy ? "检查文件…" : _editing ? "保存修改" : "加入队列");
        _remove.IsEnabled = !_busy && _sources.SelectedItems?.Count > 0;
        _empty.IsVisible = _files.Count == 0;
        _count.Text = _files.Count > 0 ? Localization.Format($"{_files.Count} 个文件") : "";
        _styleSection.IsVisible = _transcribe && _output.SelectedIndex != 2;
        _voiceSection.IsVisible = _transcribe && _output.SelectedIndex is 0 or 1;
        _folder.IsEnabled = _browse.IsEnabled = !_busy && _sourceFolder.IsChecked != true;
        if (!_busy)
        {
            _notice.Text = _error.Length > 0 ? _error : !hasFolder && _files.Count > 0 ? Localization.Text("请选择输出目录。") : "";
            _notice.IsVisible = _notice.Text.Length > 0;
        }
    }

    private static void Add(Panel panel, string label, Control control)
    {
        AutomationProperties.SetName(control, label);
        var row = new Grid { ColumnDefinitions = new("80,*"), ColumnSpacing = 8 };
        row.Children.Add(Ui.Text(label)); Grid.SetColumn(control, 1); row.Children.Add(control); panel.Children.Add(row);
    }
}
