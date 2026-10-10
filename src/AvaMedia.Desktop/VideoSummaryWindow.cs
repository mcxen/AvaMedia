using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed class VideoSummaryWindow : Window
{
    private readonly LocalModelWarmupController _modelWarmup;
    private readonly IMediaEngine _engine;
    private readonly ObservableCollection<string> _files = [];
    private readonly MediaPreviewPanel _preview;
    private readonly ListBox _sources = new() { Name = "SummaryFiles", MinHeight = 72, MaxHeight = 130, SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _empty = Ui.Text("添加或拖入视频", "caption");
    private readonly TextBlock _notice = Ui.Status();
    private readonly CheckBox _abstract = new() { Content = "摘要" }, _summary = new() { Content = "视频内容总结" },
        _subtitles = new() { Content = "字幕提取" }, _analysis = new() { Content = "内容分析" },
        _frames = new() { Content = "分析视频画面" }, _gpu = new() { Content = "优先使用 GPU" },
        _sourceFolder = new() { Content = "输出至源文件目录" };
    private readonly ComboBox _source, _language, _speechLanguage, _speechModel, _provider;
    private readonly ComboBox _onlineProvider = new() { Name = "SummaryOnlineProvider", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _onlineProviderRow = new();
    private readonly ComboBox _localVision = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _localVisionRow = new();
    private readonly Grid _localRuntime = new() { ColumnDefinitions = new("*,*,Auto"), ColumnSpacing = 12, MinHeight = 30 };
    private readonly Button _warmAction = new() { Content = "预热模型", Classes = { "field-action" } };
    private string _runtimeVisionId = "";
    private sealed record LocalVisionChoice(string Id, string Name);
    private sealed record OnlineProviderChoice(string Id, string Name, bool UserText = true);
    private readonly TextBlock _modelNotice = Ui.Text("", "caption");
    private readonly Button? _configureOnline;
    private readonly TextBox _external = Ui.Input(), _focus = Ui.Input(), _folder;
    private readonly NumericUpDown _frameCount = new() { Minimum = 1, Maximum = 48, Increment = 1 },
        _subtitleTrack = new() { Minimum = -1, Maximum = 100, Increment = 1 },
        _audioTrack = new() { Minimum = 0, Maximum = 100, Increment = 1 },
        _chunkSize = new() { Minimum = 1000, Maximum = 4000, Increment = 100 };
    private readonly StackPanel _externalRow, _speechFields;
    private readonly Button _confirm;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _editing;
    private bool _busy;

    public VideoSummaryWindow(IMediaEngine engine, string outputFolder, IEnumerable<string>? files = null, VideoSummaryOptions? initial = null,
        string? resultFolder = null, Func<Window, Task>? manageModels = null, Func<Window, Task>? configureOnlineAi = null)
    {
        _engine = engine; _editing = initial is not null; var options = initial?.Clone() ?? new Storage().LoadToolOptions<VideoSummaryOptions>("video-summary") ?? new() { ExtractSubtitles=false, AnalyzeContent=false,Speech=new(){Model=SpeechModel.Base} };
        _modelWarmup = new(this, engine, engine.Settings);
        Title = _editing ? "编辑任务 · 视频总结" : "视频总结";
        Width = 960; Height = 800; MinWidth = 780; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, "document");
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new(20), RowSpacing = 16 };
        var imports = new StackPanel { Spacing = 8 }; root.Children.Add(imports);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var add = new Button { Content = "添加视频…", Classes = { "field-action" } };
        add.Click += async (_, _) =>
        {
            if (_busy) return;
            try { AddFiles(await Ui.Pick(this, "选择视频")); }
            catch (Exception error) { ShowError(error); }
        };
        toolbar.Children.Add(add);
        var remove = new Button { Content = "移除选中", Classes = { "field-action" } };
        remove.Click += (_, _) =>
        {
            if (_busy) return;
            foreach (var file in _sources.SelectedItems?.OfType<string>().ToArray() ?? []) _files.Remove(file);
            Refresh();
        };
        toolbar.Children.Add(remove);
        if (resultFolder is not null && File.Exists(Path.Combine(resultFolder, "report.json")))
        {
            var results = new Button { Content = "查看结果", Classes = { "field-action" } };
            results.Click += (_, _) =>
            {
                try { new VideoSummaryResultWindow(resultFolder, engine, _files.FirstOrDefault()).Show(this); }
                catch (Exception error) { ShowError(error); }
            };
            toolbar.Children.Add(results);
        }
        imports.Children.Add(toolbar);
        _sources.ItemsSource = _files;
        _sources.ItemTemplate = new FuncDataTemplate<string>((path, _) =>
        {
            var label = Ui.Text(Path.GetFileName(path!)); Localization.SetIsUserText(label, true);
            label.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis; label.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
            ToolTip.SetTip(label, path); return label;
        });
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center; _empty.IsHitTestVisible = false;
        var fileArea = new Grid(); fileArea.Children.Add(_sources); fileArea.Children.Add(_empty); imports.Children.Add(fileArea);
        var fields = new StackPanel { Spacing = 16 };
        Width = Math.Max(Width,1120); MinWidth = 980; root.ColumnDefinitions = new("*,420");
        _preview = new MediaPreviewPanel(engine); Grid.SetRow(_preview,1); Grid.SetColumn(_preview,1); root.Children.Add(_preview);
        _sources.SelectionChanged += (_,_) => _preview.SetSource(_sources.SelectedItem as string);
        Closed += (_,_) => _preview.Dispose();
        root.Children.Add(new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, [Grid.RowProperty] = 1 });
        var outputs = new WrapPanel { Orientation = Orientation.Horizontal };
        _abstract.IsChecked = options.ExtractAbstract; _summary.IsChecked = options.SummarizeContent;
        _subtitles.IsChecked = options.ExtractSubtitles; _analysis.IsChecked = options.AnalyzeContent;
        foreach (var choice in new[] { _abstract, _summary, _subtitles, _analysis })
        { choice.Margin = new(0, 0, 20, 0); choice.IsCheckedChanged += (_, _) => Refresh(); outputs.Children.Add(choice); }
        fields.Children.Add(new Expander { Header="输出内容", Content=outputs, HorizontalAlignment=HorizontalAlignment.Stretch });
        _provider = Ui.Combo(["本地模型", "线上 AI"], "本地模型");
        _provider.SelectedIndex = (int)options.Provider; _provider.Name = "SummaryProvider";
        Add(fields, "总结模型", _provider);
        var localChoices = new[] { ModelCatalog.SummaryQwen35Id, ModelCatalog.SummaryVisionId }
            .Select(id => new LocalVisionChoice(id, ModelCatalog.Find(id).Name)).ToArray();
        _localVision.ItemsSource = localChoices;
        _localVision.ItemTemplate = new FuncDataTemplate<LocalVisionChoice>((item, _) =>
        { var label = Ui.Text(item?.Name ?? ""); Localization.SetIsUserText(label, true); return label; });
        _localVision.SelectedItem = localChoices.FirstOrDefault(choice => choice.Id == options.LocalVisionModelId) ?? localChoices[0];
        Add(_localVisionRow, "本地画面模型", _localVision); fields.Children.Add(_localVisionRow);
        _onlineProvider.ItemTemplate = new FuncDataTemplate<OnlineProviderChoice>((item, _) =>
        { var label = Ui.Text(item?.Name ?? ""); Localization.SetIsUserText(label, item?.UserText == true); return label; });
        RefreshOnlineProviders(options.OnlineProviderId);
        Add(_onlineProviderRow, "供应商", _onlineProvider); fields.Children.Add(_onlineProviderRow);
        _source = Ui.Combo(["自动：优先字幕，否则识别语音", "视频字幕轨", "语音识别", "外部字幕文件"], "自动：优先字幕，否则识别语音");
        _source.SelectedIndex = (int)options.TranscriptSource; _source.Name = "SummaryTranscriptSource";
        var recognition = new StackPanel { Spacing=8 };
        Add(recognition, "字幕来源", _source);
        _external.IsReadOnly = true; _external.Text = options.SubtitleFile; Localization.SetIsUserText(_external, true);
        var pickSubtitle = new Button { Content = "浏览…", Classes = { "field-action" } };
        pickSubtitle.Click += async (_, _) =>
        {
            try
            {
                var picked = await StorageProvider.OpenFilePickerAsync(new() { Title = Localization.Text("选择字幕文件"), AllowMultiple = false,
                    FileTypeFilter = [new(Localization.Text("字幕文件")) { Patterns = ["*.srt", "*.vtt", "*.ass", "*.ssa"] }] });
                if (!_lifetime.IsCancellationRequested && picked.FirstOrDefault()?.TryGetLocalPath() is { } path) _external.Text = path;
            }
            catch (Exception error) { ShowError(error); }
        };
        _externalRow = new() { Spacing = 8 };
        Add(_externalRow, "字幕文件", WithButton(_external, pickSubtitle)); recognition.Children.Add(_externalRow);
        _speechLanguage = Ui.Combo(["自动识别", "中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语", "俄语"], "自动识别");
        _speechLanguage.SelectedIndex = Math.Max(0, Array.IndexOf(TranscriptionOptions.Languages, options.Speech.Language));
        _speechModel = Ui.Combo(["轻量 · Tiny · 32 MB", "标准 · Base · 60 MB", "Small · 190 MB"], options.Speech.Model switch
        { SpeechModel.Base => "标准 · Base · 60 MB", SpeechModel.Small => "Small · 190 MB", _ => "轻量 · Tiny · 32 MB" });
        _speechFields = new() { Spacing = 8 };
        Add(_speechFields, "识别语言", _speechLanguage); Add(_speechFields, "语音模型", _speechModel); recognition.Children.Add(_speechFields);
        _frames.IsChecked = options.AnalyzeFrames; _frames.IsCheckedChanged += (_, _) => Refresh(); fields.Children.Add(_frames);
        _language = Ui.Combo(["简体中文", "English", "日本語"], options.OutputLanguage); Add(fields, "输出语言", _language);
        _focus.Text = options.Focus; _focus.AcceptsReturn = true; _focus.TextWrapping = Avalonia.Media.TextWrapping.Wrap; _focus.MinHeight = 64;
        _focus.Watermark = "可选：关注的主题或问题"; Localization.SetIsUserText(_focus, true); Add(fields, "分析重点", _focus);
        var modelRow = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 12 };
        modelRow.Children.Add(_modelNotice);
        if (configureOnlineAi is not null)
        {
            _configureOnline = new Button { Content = "配置供应商…", Classes = { "field-action" } };
            _configureOnline.Click += async (_, _) =>
            {
                try
                {
                    var selected = (_onlineProvider.SelectedItem as OnlineProviderChoice)?.Id ?? "";
                    await configureOnlineAi(this); RefreshOnlineProviders(selected);
                }
                catch (Exception error) { ShowError(error); }
            };
            Grid.SetColumn(_configureOnline, 1); modelRow.Children.Add(_configureOnline);
        }
        if (manageModels is not null)
        {
            var manage = new Button { Content = "模型管理", Classes = { "field-action" } };
            manage.Click += async (_, _) => { try { await manageModels(this); _modelWarmup.RefreshModels(); } catch (Exception error) { ShowError(error); } };
            Grid.SetColumn(manage, 2); modelRow.Children.Add(manage);
        }
        fields.Children.Add(modelRow);
        fields.Children.Add(_localRuntime);
        var advanced = new StackPanel { Spacing = 8 };
        _frameCount.Value = options.FrameCount; _subtitleTrack.Value = options.SubtitleTrack;
        _audioTrack.Value = options.AudioTrack; _chunkSize.Value = options.ChunkCharacters; _gpu.IsChecked = options.PreferGpu;
        Add(advanced, "采样画面数", _frameCount); var subtitle=new TrackSelector(engine,files?.FirstOrDefault(),"subtitle",options.SubtitleTrack,_lifetime.Token);
        var audio=new TrackSelector(engine,files?.FirstOrDefault(),"audio",options.AudioTrack,_lifetime.Token);
        subtitle.SelectionChanged+=(_,_)=>{if(subtitle.SelectedItem is not null)_subtitleTrack.Value=subtitle.Index;};audio.SelectionChanged+=(_,_)=>{if(audio.SelectedItem is not null)_audioTrack.Value=audio.Index;};
        _sources.SelectionChanged+=(_,_)=>{var path=_sources.SelectedItem as string;subtitle.SetSource(engine,path,"subtitle",(int)(_subtitleTrack.Value??-1),_lifetime.Token);audio.SetSource(engine,path,"audio",(int)(_audioTrack.Value??0),_lifetime.Token);};
        Add(advanced,"字幕轨",subtitle);Add(advanced,"音轨",audio); Add(advanced, "分段字符数", _chunkSize); advanced.Children.Add(_gpu);
        foreach(var child in advanced.Children.ToArray()){advanced.Children.Remove(child);recognition.Children.Add(child);}
        fields.Children.Add(new Expander { Header = "识别设置", Content = recognition, HorizontalAlignment = HorizontalAlignment.Stretch });
        var saving = new StackPanel { Spacing = 8 }; _folder = Ui.Input(outputFolder); _folder.IsReadOnly = true; Localization.SetIsUserText(_folder, true);
        var browse = new Button { Content = "浏览…", Classes = { "field-action" } };
        browse.Click += async (_, _) =>
        {
            try { if (await Ui.Folder(this, "选择输出目录") is { } folder && !_lifetime.IsCancellationRequested) _folder.Text = folder; }
            catch (Exception error) { ShowError(error); }
        };
        Add(saving, "保存位置", WithButton(_folder, browse));
        _sourceFolder.IsChecked = !_editing && engine.Settings.OutputToSource; saving.Children.Add(_sourceFolder);
        Grid.SetRow(saving, 2); root.Children.Add(saving);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 }; footer.Children.Add(_notice);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(Ui.DialogButton("取消", () => Close(null)));
        _confirm = new Button { Content = _editing ? "保存修改" : "加入队列", IsDefault = true, Classes = { "primary", "dialog-action" } };
        _confirm.Click += async (_, _) =>
        {
            if (_busy) return; _busy = true; Refresh(); imports.IsEnabled = fields.IsEnabled = saving.IsEnabled = false;
            try
            {
                var request = ReadRequest();
                foreach (var file in request.Files)
                {
                    var info = await _engine.Probe(file, _lifetime.Token, audioStreamIndex: request.Options.VideoSummary!.AudioTrack);
                    if (!info.HasVideo || !double.IsFinite(info.Duration) || info.Duration <= 0) throw new ArgumentException("请选择有画面和有效时长的视频。");
                }
                var remembered=request.Options.VideoSummary!.Clone();remembered.SubtitleFile="";
                if(remembered.TranscriptSource==VideoTranscriptSource.External)remembered.TranscriptSource=VideoTranscriptSource.Automatic;
                new Storage().SaveToolOptions("video-summary",remembered);
                if (!_lifetime.IsCancellationRequested) { _modelWarmup.HandOff(); ToolExecution.Complete(this, request); }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { ShowError(error); }
            finally
            {
                if (!_lifetime.IsCancellationRequested) { _busy = false; imports.IsEnabled = fields.IsEnabled = saving.IsEnabled = true; Refresh(); }
            }
        };
        actions.Children.Add(_confirm); Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 3); root.Children.Add(footer);
        foreach (var child in root.Children.Where(child => Grid.GetRow(child) != 1)) Grid.SetColumnSpan(child,2);
        Content = root; ToolExecution.Configure(this,_confirm,"后台总结",_editing);
        StableLayout.Reserve(_warmAction, "预热模型", "停止预热", "重试预热");
        _warmAction.Click += (_, _) => { if (_modelWarmup.Running) _modelWarmup.Stop(); else _modelWarmup.Retry(); };
        _modelWarmup.Changed += UpdateWarmAction;
        _source.SelectionChanged += (_, _) => Refresh(); _provider.SelectionChanged += (_, _) => Refresh();
        _localVision.SelectionChanged += (_, _) => Refresh(); _gpu.IsCheckedChanged += (_, _) => Refresh();
        _sourceFolder.IsCheckedChanged += (_, _) => Refresh();
        DragDrop.SetAllowDrop(root, true);
        root.AddHandler(DragDrop.DragOverEvent, (_, args) => { args.DragEffects = !_busy && args.DataTransfer.TryGetFiles() is not null ? DragDropEffects.Copy : DragDropEffects.None; args.Handled = true; });
        root.AddHandler(DragDrop.DropEvent, (_, args) => { args.Handled = true; AddFiles(args.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).OfType<string>() ?? []); });
        Closed += (_, _) => _lifetime.Cancel();
        AddFiles(files ?? []); Refresh();
    }

    private void AddFiles(IEnumerable<string> files)
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        var known = _files.ToHashSet(VideoFolderScanner.PathComparer);
        foreach (var path in files.Where(File.Exists).Where(VideoFormats.IsVideo).Select(Path.GetFullPath).Where(known.Add)) _files.Add(path);
        if(_sources.SelectedItem is null&&_files.Count>0)_sources.SelectedIndex=0;
        _notice.Text = ""; Refresh();
    }
    private ConversionRequest ReadRequest()
    {
        if (_files.Count == 0) throw new ArgumentException("请添加视频。");
        var options = new VideoSummaryOptions {
            Provider = (VideoSummaryProvider)_provider.SelectedIndex,
            LocalVisionModelId = (_localVision.SelectedItem as LocalVisionChoice)?.Id ?? ModelCatalog.SummaryQwen35Id,
            OnlineProviderId = (_onlineProvider.SelectedItem as OnlineProviderChoice)?.Id ?? "",
            ExtractAbstract = _abstract.IsChecked == true, SummarizeContent = _summary.IsChecked == true,
            ExtractSubtitles = _subtitles.IsChecked == true, AnalyzeContent = _analysis.IsChecked == true,
            TranscriptSource = (VideoTranscriptSource)_source.SelectedIndex, SubtitleFile = _external.Text?.Trim() ?? "",
            SubtitleTrack = (int)(_subtitleTrack.Value ?? -1), AudioTrack = (int)(_audioTrack.Value ?? 0),
            Speech = new() { Model = _speechModel.SelectedIndex switch { 1 => SpeechModel.Base, 2 => SpeechModel.Small, _ => SpeechModel.Tiny },
                Language = TranscriptionOptions.Languages[Math.Max(0, _speechLanguage.SelectedIndex)] },
            AnalyzeFrames = _frames.IsChecked == true, FrameCount = (int)(_frameCount.Value ?? 12), PreferGpu = _gpu.IsChecked == true,
            ChunkCharacters = (int)(_chunkSize.Value ?? 2400), OutputLanguage = (string?)_language.SelectedItem ?? "简体中文", Focus = _focus.Text?.Trim() ?? ""
        };
        options.Validate();
        if (options.NeedsAi && options.Provider == VideoSummaryProvider.Online)
        {
            var selected = _engine.Settings.OnlineAi.Resolve(options.OnlineProviderId); selected.Validate(); selected.ValidateConnection();
            options.OnlineProviderId = selected.Id;
        }
        if (options.TranscriptSource == VideoTranscriptSource.External && _files.Count != 1) throw new ArgumentException("使用外部字幕时请选择一个视频。");
        var folder = _sourceFolder.IsChecked == true ? Path.GetDirectoryName(_files[0])! : _folder.Text?.Trim() ?? "";
        if (folder.Length == 0) throw new ArgumentException("请选择输出目录。");
        return new(Catalog.Find("video-summary"), _files.ToArray(), folder, new() { Format = "", VideoSummary = options }, OutputToSource: _sourceFolder.IsChecked == true, StartImmediately: ToolExecution.StartImmediately(this));
    }
    private void ShowError(Exception error) { if (!_lifetime.IsCancellationRequested) _notice.Text = Localization.Text(error.Message); }
    private void Refresh()
    {
        if (_confirm is null) return;
        _empty.IsVisible = _files.Count == 0;
        var ai = _abstract.IsChecked == true || _summary.IsChecked == true || _analysis.IsChecked == true;
        var online = _provider.SelectedIndex == (int)VideoSummaryProvider.Online;
        _onlineProviderRow.IsVisible = ai && online;
        _localVisionRow.IsVisible = ai && !online && _frames.IsChecked == true;
        _provider.IsEnabled = ai; _gpu.IsEnabled = ai && !online;
        _modelNotice.IsVisible = ai;
        _modelNotice.Text = Localization.Text(online ? "发送采样画面和转录内容到线上 AI" : "本地模型 · 首次使用自动下载");
        if (_configureOnline is not null) _configureOnline.IsVisible = ai && online;
        _frames.IsEnabled = ai; _language.IsEnabled = _focus.IsEnabled = ai; _frameCount.IsEnabled = ai && _frames.IsChecked == true;
        _externalRow.IsVisible = _source.SelectedIndex == 3; _speechFields.IsVisible = _source.SelectedIndex is 0 or 2;
        _subtitleTrack.IsEnabled = _source.SelectedIndex is 0 or 1; _audioTrack.IsEnabled = _speechFields.IsVisible;
        _folder.IsEnabled = _sourceFolder.IsChecked != true;
        _confirm.IsEnabled = !_busy && _files.Count > 0 && (ai || _subtitles.IsChecked == true);
        _confirm.Content = Localization.Text(_busy ? "检查文件…" : _editing ? "保存修改" : "加入队列");
        _modelWarmup?.Update(ai && !online ? (_frames.IsChecked == true
            ? new[] { (_localVision.SelectedItem as LocalVisionChoice)?.Id ?? ModelCatalog.SummaryQwen35Id, ModelCatalog.SummaryTextId }
            : [ModelCatalog.SummaryTextId]) : [], _gpu.IsChecked == true, _files.FirstOrDefault());
        _localRuntime.IsVisible = ai && !online;
        var visionId = (_localVision.SelectedItem as LocalVisionChoice)?.Id ?? ModelCatalog.SummaryQwen35Id;
        if (_runtimeVisionId != visionId)
        {
            _runtimeVisionId = visionId; _localRuntime.Children.Clear();
            var store = new ModelStore();
            _localRuntime.Children.Add(new ModelRuntimeView(store.Root, visionId, "画面"));
            var text = new ModelRuntimeView(store.Root, ModelCatalog.SummaryTextId, "文本"); Grid.SetColumn(text, 1); _localRuntime.Children.Add(text);
            Grid.SetColumn(_warmAction, 2); _localRuntime.Children.Add(_warmAction);
        }
        _localRuntime.Children[0].IsVisible = _frames.IsChecked == true;
        UpdateWarmAction();
    }
    private void UpdateWarmAction()
    {
        if (_modelWarmup is null || _lifetime.IsCancellationRequested) return;
        _warmAction.Content = Localization.Text(_modelWarmup.Running ? "停止预热" : _modelWarmup.Error is null ? "预热模型" : "重试预热");
        ToolTip.SetTip(_warmAction, _modelWarmup.Stopped ? Localization.Text("预热已停止，开始分析时将按需加载") : _modelWarmup.Error?.Message);
    }
    private void RefreshOnlineProviders(string selected)
    {
        var choices = new List<OnlineProviderChoice> { new("", "默认供应商", false) };
        choices.AddRange(_engine.Settings.OnlineAi.Providers.Where(p => p.Enabled).Select(p => new OnlineProviderChoice(p.Id, p.Name)));
        if (selected.Length != 0 && !choices.Any(p => p.Id == selected))
            choices.Add(new(selected, "供应商不可用", false));
        _onlineProvider.ItemsSource = choices;
        _onlineProvider.SelectedItem = choices.First(p => p.Id == selected);
    }
    private static Grid WithButton(Control control, Button button)
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(control); Grid.SetColumn(button, 1); grid.Children.Add(button); return grid;
    }
    private static void Add(Panel panel, string label, Control control)
    { control=Ui.Parameter(control,label);
        AutomationProperties.SetName(control, label);
        var row = new Grid { ColumnDefinitions = new("145,*"), ColumnSpacing = 12 };
        row.Children.Add(Ui.Text(label)); Grid.SetColumn(control, 1); row.Children.Add(control); panel.Children.Add(row);
    }
}
