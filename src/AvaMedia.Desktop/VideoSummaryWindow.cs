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
    private readonly ListBox _sources = new() { Name = "SummaryFiles", MinHeight = 96, MaxHeight = 180, SelectionMode = SelectionMode.Multiple };
    private readonly AvaMedia.Controls.EmptyState _empty = new() { Text = "添加或拖入视频" };
    private readonly TextBlock _notice = Ui.ComponentStatus();
    private readonly CheckBox _abstract = new() { Classes = { "ac" }, Content = "摘要" }, _summary = new() { Content = "视频内容总结" },
        _subtitles = new() { Content = "字幕提取" }, _analysis = new() { Content = "内容分析" },
        _frames = new() { Content = "分析视频画面" },
        _sourceFolder = new() { Content = "输出至源文件目录" };
    private readonly ComboBox _source, _language, _speechLanguage, _speechModel, _provider;
    private readonly ComboBox _onlineProvider = new() { Classes = { "ac" }, Name = "SummaryOnlineProvider", HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140, MaxWidth = 240 };
    private readonly StackPanel _onlineProviderRow = new();
    private readonly ComboBox _localVision = new() { Classes = { "ac" }, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160, MaxWidth = 260 };
    private readonly StackPanel _localVisionRow = new();
    private readonly Grid _localRuntime = new() { ColumnDefinitions = new("*,*,Auto"), ColumnSpacing = 12, MinHeight = 30 };
    private readonly Button _warmAction = new() { Content = "预热模型", Classes = { "ac", "field-action" } };
    private string _runtimeVisionId = "";
    private sealed record LocalVisionChoice(string Id, string Name);
    private sealed record OnlineProviderChoice(string Id, string Name, bool UserText = true);
    private readonly TextBlock _modelNotice = Ui.ComponentText("", "caption");
    private readonly Button? _configureOnline;
    private readonly TextBox _external = Ui.ComponentInput(), _focus = Ui.ComponentInput(), _folder;
    private readonly NumericUpDown _frameCount = new() { Classes = { "ac" }, Minimum = 1, Maximum = 48, Increment = 1 },
        _subtitleTrack = new() { Minimum = -1, Maximum = 100, Increment = 1 },
        _audioTrack = new() { Minimum = 0, Maximum = 100, Increment = 1 },
        _chunkSize = new() { Minimum = 1000, Maximum = 4000, Increment = 100 };
    private readonly StackPanel _externalRow, _speechFields;
    private readonly Button _confirm;
    private readonly CancellationTokenSource _lifetime = new();
    public bool ResumeRequested { get; private set; }
    private readonly CheckBox _allAudioTracks = new() { Classes = { "ac" }, Content = "全部音轨" }, _autoAudioTrack = new() { Content = "按语言自动选择音轨" },
        _strictSequential = new() { Content = "严格顺序理解" };
    private readonly NumericUpDown _longConcurrency = new() { Classes = { "ac" }, Minimum = 1, Maximum = 16, Increment = 1 };
    private readonly bool _editing;
    private bool _busy;

    public VideoSummaryWindow(IMediaEngine engine, string outputFolder, IEnumerable<string>? files = null, VideoSummaryOptions? initial = null,
        string? resultFolder = null, Func<Window, Task>? manageModels = null, Func<Window, Task>? configureOnlineAi = null, bool allowResume = false)
    {
        _engine = engine; _editing = initial is not null; var options = initial?.Clone() ?? new Storage().LoadToolOptions<VideoSummaryOptions>("video-summary") ?? new() { ExtractSubtitles=false, AnalyzeContent=false,Speech=new(){Model=SpeechModel.Base} };
        _modelWarmup = new(this, engine, engine.Settings);
        Title = _editing ? "编辑任务 · 视频总结" : "视频总结";
        Width = 920; Height = 640; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, "document");
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new(12), RowSpacing = 8 };
        var imports = new AvaMedia.FileUi.FileImportPanel(); root.Children.Add(imports);
        var toolbar = imports.Actions;
        toolbar.FilesText = "添加视频…";
        toolbar.CanImport = () => !_busy;
        toolbar.AddFilesAsync = async () => AddFiles(await Ui.Pick(this, "选择视频"));
        toolbar.ReportErrorAsync = error => { ShowError(error); return Task.CompletedTask; };
        imports.ImportDroppedAsync = items => { AddFiles(items.Select(file => file.TryGetLocalPath()).OfType<string>()); return Task.CompletedTask; };
        var remove = new Button { Content = "移除选中", Classes = { "ac", "field-action" } };
        remove.Click += (_, _) =>
        {
            if (_busy) return;
            foreach (var file in _sources.SelectedItems?.OfType<string>().ToArray() ?? []) _files.Remove(file);
            Refresh();
        };
        toolbar.Children.Add(remove);
        if (resultFolder is not null && File.Exists(Path.Combine(resultFolder, "report.json")))
        {
            var results = new Button { Content = "查看结果", Classes = { "ac", "field-action" } };
            results.Click += (_, _) =>
            {
                try { Controls.WindowChrome.ShowIndependent(new VideoSummaryResultWindow(resultFolder, engine, _files.FirstOrDefault())); }
                catch (Exception error) { ShowError(error); }
            };
            toolbar.Children.Add(results);
        }
        if (allowResume && resultFolder is not null && File.Exists(Path.Combine(resultFolder, "work", "manifest.json")))
        {
            var resume = new Button { Content = "继续", Classes = { "ac", "field-action" } };
            ToolTip.SetTip(resume, "继续已有任务，复用已完成的识别、画面和分段结果。");
            resume.Click += (_, _) => { ResumeRequested = true; Close(null); }; toolbar.Children.Add(resume);
        }
        _sources.ItemsSource = _files;
        _sources.ItemTemplate = new FuncDataTemplate<string>((path, _) =>
        {
            var label = Ui.ComponentText(Path.GetFileName(path!)); Localization.SetIsUserText(label, true);
            label.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis; label.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
            ToolTip.SetTip(label, path); return new AvaMedia.Controls.ListRow { Child = label };
        });
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.VerticalAlignment = VerticalAlignment.Center; _empty.IsHitTestVisible = false;
        var fileArea = new Grid(); fileArea.Children.Add(_sources); fileArea.Children.Add(_empty); imports.FileList = fileArea;
        var fields = new StackPanel { Spacing = 6 };
        root.ColumnDefinitions = new("*,320");
        _preview = new MediaPreviewPanel(engine) { Compact = true }; Grid.SetRow(_preview,1); Grid.SetColumn(_preview,1); root.Children.Add(_preview);
        _sources.SelectionChanged += (_,_) => _preview.SetSource(_sources.SelectedItem as string);
        Closed += (_,_) => _preview.Dispose();
        root.Children.Add(new ScrollViewer { Content = Ui.ComponentCard(fields), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, [Grid.RowProperty] = 1 });
        var outputs = new WrapPanel { Orientation = Orientation.Horizontal };
        _abstract.IsChecked = options.ExtractAbstract; _summary.IsChecked = options.SummarizeContent;
        _subtitles.IsChecked = options.ExtractSubtitles; _analysis.IsChecked = options.AnalyzeContent;
        foreach (var choice in new[] { _abstract, _summary, _subtitles, _analysis, _frames })
        { choice.Margin = new(0, 0, 14, 0); choice.IsCheckedChanged += (_, _) => Refresh(); outputs.Children.Add(choice); }
        _frames.IsChecked = options.AnalyzeFrames;
        fields.Children.Add(outputs);
        _provider = Ui.ComponentCombo(["本地模型", "线上 AI"], "本地模型");
        _provider.SelectedIndex = (int)options.Provider; _provider.Name = "SummaryProvider";
        _language = Ui.ComponentCombo(["简体中文", "English", "日本語"], options.OutputLanguage);
        var choiceRow = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8 };
        Add(choiceRow, "总结模型", _provider); Add(choiceRow, "输出语言", _language, 1); fields.Children.Add(choiceRow);
        var localChoices = new[] { ModelCatalog.SummaryQwen35Id, ModelCatalog.SummaryVisionId }
            .Select(id => new LocalVisionChoice(id, ModelCatalog.Find(id).Name)).ToArray();
        _localVision.ItemsSource = localChoices;
        _localVision.ItemTemplate = new FuncDataTemplate<LocalVisionChoice>((item, _) =>
        { var label = Ui.ComponentText(item?.Name ?? ""); Localization.SetIsUserText(label, true); return label; });
        _localVision.SelectedItem = localChoices.FirstOrDefault(choice => choice.Id == options.LocalVisionModelId) ?? localChoices[0];
        Add(_localVisionRow, "本地画面模型", _localVision); fields.Children.Add(_localVisionRow);
        _onlineProvider.ItemTemplate = new FuncDataTemplate<OnlineProviderChoice>((item, _) =>
        { var label = Ui.ComponentText(item?.Name ?? ""); Localization.SetIsUserText(label, item?.UserText == true); return label; });
        RefreshOnlineProviders(options.OnlineProviderId);
        Add(_onlineProviderRow, "供应商", _onlineProvider); fields.Children.Add(_onlineProviderRow);
        _source = Ui.ComponentCombo(["自动：优先字幕，否则识别语音", "视频字幕轨", "语音识别", "外部字幕文件"], "自动：优先字幕，否则识别语音");
        _source.SelectedIndex = (int)options.TranscriptSource; _source.Name = "SummaryTranscriptSource";
        var recognition = new StackPanel { Spacing=6 };
        Add(recognition, "字幕来源", _source);
        _external.IsReadOnly = true; _external.Text = options.SubtitleFile; Localization.SetIsUserText(_external, true);
        var pickSubtitle = new Button { Content = "浏览…", Classes = { "ac", "field-action" } };
        pickSubtitle.Click += async (_, _) =>
        {
            try
            {
                var picked = await ToolFilePicker.OpenFilesAsync(this, new() { Title = Localization.Text("选择字幕文件"), AllowMultiple = false,
                    FileTypeFilter = [new(Localization.Text("字幕文件")) { Patterns = ["*.srt", "*.vtt", "*.ass", "*.ssa"] }] });
                if (!_lifetime.IsCancellationRequested && picked.FirstOrDefault()?.TryGetLocalPath() is { } path) _external.Text = path;
            }
            catch (Exception error) { ShowError(error); }
        };
        _externalRow = new() { Spacing = 8 };
        Add(_externalRow, "字幕文件", WithButton(_external, pickSubtitle)); recognition.Children.Add(_externalRow);
        _speechLanguage = Ui.ComponentCombo(["自动识别", "中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语", "俄语"], "自动识别");
        _speechLanguage.SelectedIndex = Math.Max(0, Array.IndexOf(TranscriptionOptions.Languages, options.Speech.Language));
        _speechModel = Ui.ComponentCombo(["Tiny · 32 MB", "Base · 60 MB", "Small · 190 MB"], options.Speech.Model switch
        { SpeechModel.Base => "Base · 60 MB", SpeechModel.Small => "Small · 190 MB", _ => "Tiny · 32 MB" });
        ToolTip.SetTip(_speechModel, "轻量 Tiny、标准 Base、Small");
        _speechFields = new() { Spacing = 6 };
        var speechRow = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8 };
        Add(speechRow, "识别语言", _speechLanguage); Add(speechRow, "语音模型", _speechModel, 1); _speechFields.Children.Add(speechRow); recognition.Children.Add(_speechFields);
        _focus.Text = options.Focus; _focus.AcceptsReturn = true; _focus.TextWrapping = Avalonia.Media.TextWrapping.Wrap; _focus.MinHeight = 44; _focus.MaxHeight = 72;
        _focus.Watermark = "可选：关注的主题或问题"; Localization.SetIsUserText(_focus, true); Add(recognition, "分析重点", _focus);
        var modelRow = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 };
        modelRow.Children.Add(_modelNotice);
        if (configureOnlineAi is not null)
        {
            _configureOnline = new Button { Content = "配置供应商…", Classes = { "ac", "field-action" } };
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
            var manage = new Button { Content = "模型管理", Classes = { "ac", "field-action" } };
            manage.Click += async (_, _) => { try { await manageModels(this); _modelWarmup.RefreshModels(); } catch (Exception error) { ShowError(error); } };
            Grid.SetColumn(manage, 2); modelRow.Children.Add(manage);
        }
        recognition.Children.Add(modelRow);
        fields.Children.Add(_localRuntime);
        var advanced = new StackPanel { Spacing = 6 };
        _frameCount.Value = options.FrameCount; _subtitleTrack.Value = options.SubtitleTrack;
        _audioTrack.Value = options.AudioTrack; _chunkSize.Value = options.ChunkCharacters;
        Add(advanced, "采样画面数", _frameCount); var subtitle=new TrackSelector(engine,files?.FirstOrDefault(),"subtitle",options.SubtitleTrack,_lifetime.Token);
        var audio=new TrackSelector(engine,files?.FirstOrDefault(),"audio",options.AudioTrack,_lifetime.Token);
        subtitle.SelectionChanged+=(_,_)=>{if(subtitle.SelectedItem is not null)_subtitleTrack.Value=subtitle.Index;};audio.SelectionChanged+=(_,_)=>{if(audio.SelectedItem is not null)_audioTrack.Value=audio.Index;};
        _sources.SelectionChanged+=(_,_)=>{var path=_sources.SelectedItem as string;subtitle.SetSource(engine,path,"subtitle",(int)(_subtitleTrack.Value??-1),_lifetime.Token);audio.SetSource(engine,path,"audio",(int)(_audioTrack.Value??0),_lifetime.Token);};
        Add(advanced,"字幕轨",subtitle);Add(advanced,"音轨",audio); Add(advanced, "分段字符数", _chunkSize);
        _allAudioTracks.IsChecked = options.AllAudioTracks; _autoAudioTrack.IsChecked = options.AutoSelectAudioTrack;
        _strictSequential.IsChecked = options.StrictSequential; _longConcurrency.Value = options.LongSummaryConcurrency;
        advanced.Children.Add(_autoAudioTrack); advanced.Children.Add(_allAudioTracks); advanced.Children.Add(_strictSequential);
        Add(advanced, "长视频并发", _longConcurrency);
        ToolTip.SetTip(_longConcurrency, "超过 20 分钟的视频使用；本地模型始终并发 1。");
        foreach(var child in advanced.Children.ToArray()){advanced.Children.Remove(child);recognition.Children.Add(child);}
        fields.Children.Add(new Expander { Header = "识别设置", Content = recognition, HorizontalAlignment = HorizontalAlignment.Stretch });
        var saving = new StackPanel { Spacing = 6 }; _folder = Ui.ComponentInput(outputFolder); _folder.IsReadOnly = true; Localization.SetIsUserText(_folder, true);
        var browse = new Button { Content = "浏览…", Classes = { "ac", "field-action" } };
        browse.Click += async (_, _) =>
        {
            try { if (await Ui.Folder(this, "选择输出目录") is { } folder && !_lifetime.IsCancellationRequested) _folder.Text = folder; }
            catch (Exception error) { ShowError(error); }
        };
        var saveRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        Avalonia.Automation.AutomationProperties.SetName(_folder, "保存位置"); saveRow.Children.Add(_folder); Grid.SetColumn(browse, 1); saveRow.Children.Add(browse);
        _sourceFolder.Content = "源目录"; ToolTip.SetTip(_sourceFolder, "输出至源文件目录"); _sourceFolder.VerticalAlignment = VerticalAlignment.Center;
        _sourceFolder.IsChecked = !_editing && engine.Settings.OutputToSource; Grid.SetColumn(_sourceFolder, 2); saveRow.ColumnDefinitions = new("*,Auto,Auto"); saveRow.Children.Add(_sourceFolder);
        saving.Children.Add(saveRow);
        Grid.SetRow(saving, 2); root.Children.Add(saving);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 }; footer.Children.Add(_notice);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Ui.DialogButton("取消", () => Close(null)));
        _confirm = new Button { Content = _editing ? "保存修改" : "加入队列", IsDefault = true, Classes = { "ac", "primary", "dialog-action" } };
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
        _localVision.SelectionChanged += (_, _) => Refresh();
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
            AllAudioTracks = _allAudioTracks.IsChecked == true, AutoSelectAudioTrack = _autoAudioTrack.IsChecked == true,
            StrictSequential = _strictSequential.IsChecked == true, LongSummaryConcurrency = (int)(_longConcurrency.Value ?? 4),
            Speech = new() { Model = _speechModel.SelectedIndex switch { 1 => SpeechModel.Base, 2 => SpeechModel.Small, _ => SpeechModel.Tiny },
                Language = TranscriptionOptions.Languages[Math.Max(0, _speechLanguage.SelectedIndex)] },
            AnalyzeFrames = _frames.IsChecked == true, FrameCount = (int)(_frameCount.Value ?? 12),
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
        _preview.IsVisible = _files.Count > 0;
        var ai = _abstract.IsChecked == true || _summary.IsChecked == true || _analysis.IsChecked == true;
        var online = _provider.SelectedIndex == (int)VideoSummaryProvider.Online;
        _onlineProviderRow.IsVisible = ai && online;
        _localVisionRow.IsVisible = ai && !online && _frames.IsChecked == true;
        _provider.IsEnabled = ai;
        _modelNotice.IsVisible = ai;
        _modelNotice.Text = Localization.Text(online ? "发送采样画面和转录内容到线上 AI" : "本地模型 · 首次使用自动下载");
        if (_configureOnline is not null) _configureOnline.IsVisible = ai && online;
        _frames.IsEnabled = ai; _language.IsEnabled = _focus.IsEnabled = ai; _frameCount.IsEnabled = ai && _frames.IsChecked == true;
        _externalRow.IsVisible = _source.SelectedIndex == 3; _speechFields.IsVisible = _source.SelectedIndex is 0 or 2;
        _subtitleTrack.IsEnabled = _source.SelectedIndex is 0 or 1; _audioTrack.IsEnabled = _speechFields.IsVisible;
        _folder.IsEnabled = _sourceFolder.IsChecked != true;
        _confirm.IsEnabled = !_busy && _files.Count > 0 && (ai || _subtitles.IsChecked == true);
        _confirm.Content = Localization.Text(_busy ? "检查文件…" : _editing ? "保存修改" : ToolExecution.StartImmediately(this) ? "后台总结" : "仅加入队列");
        _modelWarmup?.Update(ai && !online ? (_frames.IsChecked == true
            ? new[] { (_localVision.SelectedItem as LocalVisionChoice)?.Id ?? ModelCatalog.SummaryQwen35Id, ModelCatalog.SummaryTextId }
            : [ModelCatalog.SummaryTextId]) : [], _files.FirstOrDefault());
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
        ToolTip.SetTip(_warmAction, _modelWarmup.Error?.Message);
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
    private static void Add(Panel panel, string label, Control control, int column = 0)
    { control=Ui.ComponentParameter(control,label);
        AutomationProperties.SetName(control, label);
        var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 6 };
        row.Children.Add(Ui.ComponentText(label)); Grid.SetColumn(control, 1); row.Children.Add(control);
        if (column > 0) Grid.SetColumn(row, column);
        panel.Children.Add(row);
    }
}
