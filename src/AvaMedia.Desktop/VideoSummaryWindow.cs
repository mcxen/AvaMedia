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
    private readonly IMediaEngine _engine;
    private readonly ObservableCollection<string> _files = [];
    private readonly ListBox _sources = new() { Name = "SummaryFiles", MinHeight = 72, MaxHeight = 130, SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _empty = Ui.Text("添加或拖入视频", "caption");
    private readonly TextBlock _notice = Ui.Text("", "caption");
    private readonly CheckBox _abstract = new() { Content = "摘要" }, _summary = new() { Content = "视频内容总结" },
        _subtitles = new() { Content = "字幕提取" }, _analysis = new() { Content = "内容分析" },
        _frames = new() { Content = "分析视频画面" }, _gpu = new() { Content = "优先使用 GPU" },
        _sourceFolder = new() { Content = "输出至源文件目录" };
    private readonly ComboBox _source, _language, _speechLanguage, _speechModel, _provider;
    private readonly ComboBox _onlineProvider = new() { Name = "SummaryOnlineProvider", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _onlineProviderRow = new();
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
        _engine = engine; _editing = initial is not null; var options = initial?.Clone() ?? new();
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
            results.Click += async (_, _) =>
            {
                try { await new VideoSummaryResultWindow(resultFolder, engine, _files.FirstOrDefault()).ShowDialog(this); }
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
        root.Children.Add(new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, [Grid.RowProperty] = 1 });
        var outputs = new WrapPanel { Orientation = Orientation.Horizontal };
        _abstract.IsChecked = options.ExtractAbstract; _summary.IsChecked = options.SummarizeContent;
        _subtitles.IsChecked = options.ExtractSubtitles; _analysis.IsChecked = options.AnalyzeContent;
        foreach (var choice in new[] { _abstract, _summary, _subtitles, _analysis })
        { choice.Margin = new(0, 0, 20, 0); choice.IsCheckedChanged += (_, _) => Refresh(); outputs.Children.Add(choice); }
        fields.Children.Add(outputs);
        _provider = Ui.Combo(["本地模型", "线上 AI"], "本地模型");
        _provider.SelectedIndex = (int)options.Provider; _provider.Name = "SummaryProvider";
        Add(fields, "总结模型", _provider);
        _onlineProvider.ItemTemplate = new FuncDataTemplate<OnlineProviderChoice>((item, _) =>
        { var label = Ui.Text(item?.Name ?? ""); Localization.SetIsUserText(label, item?.UserText == true); return label; });
        RefreshOnlineProviders(options.OnlineProviderId);
        Add(_onlineProviderRow, "供应商", _onlineProvider); fields.Children.Add(_onlineProviderRow);
        _source = Ui.Combo(["自动：优先字幕，否则识别语音", "视频字幕轨", "语音识别", "外部字幕文件"], "自动：优先字幕，否则识别语音");
        _source.SelectedIndex = (int)options.TranscriptSource; _source.Name = "SummaryTranscriptSource";
        Add(fields, "字幕来源", _source);
        _external.Text = options.SubtitleFile; Localization.SetIsUserText(_external, true);
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
        Add(_externalRow, "字幕文件", WithButton(_external, pickSubtitle)); fields.Children.Add(_externalRow);
        _speechLanguage = Ui.Combo(["自动识别", "中文", "英语", "日语", "韩语", "法语", "德语", "西班牙语", "俄语"], "自动识别");
        _speechLanguage.SelectedIndex = Math.Max(0, Array.IndexOf(TranscriptionOptions.Languages, options.Speech.Language));
        _speechModel = Ui.Combo(["轻量 · Tiny · 32 MB", "标准 · Base · 60 MB", "Small · 190 MB"], options.Speech.Model switch
        { SpeechModel.Base => "标准 · Base · 60 MB", SpeechModel.Small => "Small · 190 MB", _ => "轻量 · Tiny · 32 MB" });
        _speechFields = new() { Spacing = 8 };
        Add(_speechFields, "识别语言", _speechLanguage); Add(_speechFields, "语音模型", _speechModel); fields.Children.Add(_speechFields);
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
            manage.Click += async (_, _) => { try { await manageModels(this); } catch (Exception error) { ShowError(error); } };
            Grid.SetColumn(manage, 2); modelRow.Children.Add(manage);
        }
        fields.Children.Add(modelRow);
        var advanced = new StackPanel { Spacing = 8 };
        _frameCount.Value = options.FrameCount; _subtitleTrack.Value = options.SubtitleTrack;
        _audioTrack.Value = options.AudioTrack; _chunkSize.Value = options.ChunkCharacters; _gpu.IsChecked = options.PreferGpu;
        Add(advanced, "采样画面数", _frameCount); Add(advanced, "字幕轨（-1 自动）", _subtitleTrack);
        Add(advanced, "音轨（从 0 起）", _audioTrack); Add(advanced, "分段字符数", _chunkSize); advanced.Children.Add(_gpu);
        fields.Children.Add(new Expander { Header = "更多选项", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch });
        var saving = new StackPanel { Spacing = 8 }; _folder = Ui.Input(outputFolder); Localization.SetIsUserText(_folder, true);
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
                if (!_lifetime.IsCancellationRequested) Close(request);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { ShowError(error); }
            finally
            {
                if (!_lifetime.IsCancellationRequested) { _busy = false; imports.IsEnabled = fields.IsEnabled = saving.IsEnabled = true; Refresh(); }
            }
        };
        actions.Children.Add(_confirm); Grid.SetColumn(actions, 1); footer.Children.Add(actions); Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        _source.SelectionChanged += (_, _) => Refresh(); _provider.SelectionChanged += (_, _) => Refresh();
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
        _notice.Text = ""; Refresh();
    }
    private ConversionRequest ReadRequest()
    {
        if (_files.Count == 0) throw new ArgumentException("请添加视频。");
        var options = new VideoSummaryOptions {
            Provider = (VideoSummaryProvider)_provider.SelectedIndex,
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
        return new(Catalog.Find("video-summary"), _files.ToArray(), folder, new() { Format = "", VideoSummary = options }, OutputToSource: _sourceFolder.IsChecked == true);
    }
    private void ShowError(Exception error) { if (!_lifetime.IsCancellationRequested) _notice.Text = Localization.Text(error.Message); }
    private void Refresh()
    {
        if (_confirm is null) return;
        _empty.IsVisible = _files.Count == 0;
        var ai = _abstract.IsChecked == true || _summary.IsChecked == true || _analysis.IsChecked == true;
        var online = _provider.SelectedIndex == (int)VideoSummaryProvider.Online;
        _onlineProviderRow.IsVisible = ai && online;
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
    {
        AutomationProperties.SetName(control, label);
        var row = new Grid { ColumnDefinitions = new("145,*"), ColumnSpacing = 12 };
        row.Children.Add(Ui.Text(label)); Grid.SetColumn(control, 1); row.Children.Add(control); panel.Children.Add(row);
    }
}
