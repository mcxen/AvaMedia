using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>Edits the durable queue payload before processing, inside the task's own window.</summary>
internal sealed class EntryParameters : StackPanel
{
    private readonly Job _job;
    private readonly Window _window;
    private readonly Action _save;
    private readonly AppSettings _settings;
    private readonly Func<IEnumerable<string>> _reserved;
    private bool _ready;

    internal EntryParameters(Job job, Window window, AppSettings settings, Action save, Func<IEnumerable<string>> reserved)
    {
        _job = job; _window = window; _settings = settings; _save = save; _reserved = reserved;
        Bind(SpacingProperty, new DynamicResourceExtension("AcGap"));
        switch (job.FeatureId)
        {
            case "person-clip": Person(); break;
            case "auto-subtitle": Speech(job.Options.Transcription!); break;
            case "video-summary": Summary(); break;
            case "media-ai": Tags(); break;
            case "folder-classification": Classification(); break;
            case "rotate":
                Children.Add(MsUi.Caption("先检测方向，完成后对比预览并确认旋转与输出格式。"));
                break;
        }
        _ready = true;
    }
    private void Changed() { if (_ready) _save(); }
    private void Row(string label, Control control)
    {
        AutomationProperties.SetName(control, Localization.Text(label));
        var grid = new Grid { ColumnDefinitions = new("Auto,*") };
        grid.Bind(Grid.ColumnSpacingProperty, new DynamicResourceExtension("AcGap"));
        grid.Children.Add(MsUi.Caption(label)); Grid.SetColumn(control, 1); grid.Children.Add(control); Children.Add(grid);
    }
    private void Check(string label, bool value, Action<bool> set)
    {
        var input = new CheckBox { Content = Localization.Text(label), IsChecked = value, Classes = { "ac" } };
        input.IsCheckedChanged += (_, _) => { set(input.IsChecked == true); Changed(); }; Children.Add(input);
    }
    private void Number(string label, double value, double min, double max, double step, Action<double> set)
    {
        var input = new NumericUpDown { Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max,
            Increment = (decimal)step, Classes = { "ac" }, HorizontalAlignment = HorizontalAlignment.Left };
        input.ValueChanged += (_, _) => { if (input.Value is { } number) { set((double)number); Changed(); } }; Row(label, input);
    }
    private void Choice<T>(string label, T value, IEnumerable<T> values, Action<T> set, Func<T, string>? title = null) where T : notnull
    {
        var items = values.ToArray();
        var input = new ComboBox { ItemsSource = items, SelectedItem = value, Classes = { "ac" }, HorizontalAlignment = HorizontalAlignment.Left };
        if (title is not null) input.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<T>((item, _) => MsUi.Caption(item is null ? "" : title(item)));
        input.SelectionChanged += (_, _) => { if (input.SelectedItem is T next) { set(next); Changed(); } }; Row(label, input);
    }
    private void Text(string label, string value, Action<string> set)
    {
        var input = new TextBox { Text = value, Classes = { "ac" } };
        input.TextChanged += (_, _) => { set(input.Text ?? ""); Changed(); }; Row(label, input);
    }
    private void Folder(string value, Action<string> set)
    {
        var path = MsUi.Caption(value); Localization.SetIsUserText(path, true);
        path.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var button = Ui.Button("更改…", async () =>
        {
            if (await Ui.Folder(_window, "输出位置") is { } folder)
            { set(folder); path.Text = folder; Changed(); }
        });
        Row("输出位置", MsUi.Columns("*,Auto", path, button));
    }
    private void Person()
    {
        PersonClipOptions Get() => _job.Options.PersonClip!.Detection;
        void Set(PersonClipOptions next) => _job.Options.PersonClip = _job.Options.PersonClip! with { Detection = next };
        foreach (var detector in PersonDetectorCatalog.All)
            Check(detector.Name, Get().SelectedDetectors.Contains(detector.Id), selected =>
            {
                var ids = Get().SelectedDetectors.Where(id => id != detector.Id).ToList(); if (selected) ids.Add(detector.Id);
                Set(Get() with { DetectorIds = ids.ToArray() });
            });
        Choice("检测模式", Get().DetectionMode, Enum.GetValues<PersonDetectionMode>(), value => Set(Get() with { DetectionMode = value }),
            value => value switch { PersonDetectionMode.Balanced => "平衡检测", PersonDetectionMode.Recall => "减少漏检", _ => "交叉确认" });
        Number("每秒采样帧数", Get().FramesPerSecond, .25, 16, .25, value => Set(Get() with { FramesPerSecond = value }));
        Number("置信度阈值", Get().Threshold, .1, .9, .05, value => Set(Get() with { Threshold = value }));
        Number("片段前后保留秒数", Get().PaddingSeconds, 0, 30, .1, value => Set(Get() with { PaddingSeconds = value }));
        Number("合并间隔秒数", Get().MergeGapSeconds, 0, 30, .1, value => Set(Get() with { MergeGapSeconds = value }));
        Number("最短片段秒数", Get().MinimumSeconds, 0, 30, .1, value => Set(Get() with { MinimumSeconds = value }));
        Check("保留不确定片段", Get().KeepUncertain, value => Set(Get() with { KeepUncertain = value }));
        Check("语义辅助", Get().UseEmbedding, value => Set(Get() with { UseEmbedding = value }));
        Check("复用相似帧", Get().ReuseSimilarFrames, value => Set(Get() with { ReuseSimilarFrames = value }));
        Check("跳过黑灯画面", Get().SkipDarkFrames, value => Set(Get() with { SkipDarkFrames = value }));
        Check("跳过空白画面", Get().SkipBlankFrames, value => Set(Get() with { SkipBlankFrames = value }));
        Number("黑灯亮度阈值", Get().DarkLumaThreshold, 1, 32, 1, value => Set(Get() with { DarkLumaThreshold = value }));
        Check("包含手部", Get().IncludeHands, value => Set(Get() with { IncludeHands = value }));
        Children.Add(MsUi.Caption("检测后在本窗口调整片段，再选择合并格式与输出位置。"));
    }
    private void Speech(TranscriptionOptions speech)
    {
        Choice("语言", speech.Language, TranscriptionOptions.Languages, value => speech.Language = value,
            value => value switch { "auto" => "自动识别", "zh" => "中文", "en" => "英语", "ja" => "日语", "ko" => "韩语", "fr" => "法语", "de" => "德语", "es" => "西班牙语", _ => "俄语" });
        Choice("语音模型", speech.Model, Enum.GetValues<SpeechModel>(), value => speech.Model = value);
        if (_job.FeatureId == "auto-subtitle")
        {
            Folder(speech.ReviewOutputFolder ?? _settings.OutputFolder, value => speech.ReviewOutputFolder = value);
            Check("输出到源目录", speech.ReviewOutputToSource, value => speech.ReviewOutputToSource = value);
            Children.Add(MsUi.Caption("识别后在本窗口选择字幕文件或嵌入视频。"));
        }
    }
    private void Summary()
    {
        var spec = _job.Options.VideoSummary!;
        Choice("AI 来源", spec.Provider, Enum.GetValues<VideoSummaryProvider>(), value => spec.Provider = value,
            value => value == VideoSummaryProvider.Local ? "本地模型" : "在线 AI");
        var models = ModelCatalog.All.Where(model => ModelCatalog.IsSummaryVision(model.Id)).ToArray();
        Choice("画面描述模型", spec.LocalVisionModelId, models.Select(model => model.Id), value => spec.LocalVisionModelId = value,
            value => models.First(model => model.Id == value).Name);
        Choice("内容总结模型", spec.LocalSummaryModelId, models.Select(model => model.Id), value => spec.LocalSummaryModelId = value,
            value => models.First(model => model.Id == value).Name);
        var providers = _settings.OnlineAi.Providers;
        Choice("在线供应商", spec.OnlineProviderId, providers.Select(provider => provider.Id).Prepend(""), value => spec.OnlineProviderId = value,
            value => value.Length == 0 ? "默认供应商" : providers.First(provider => provider.Id == value).Name);
        Check("提取摘要", spec.ExtractAbstract, value => spec.ExtractAbstract = value);
        Check("内容总结", spec.SummarizeContent, value => spec.SummarizeContent = value);
        Check("提取字幕", spec.ExtractSubtitles, value => spec.ExtractSubtitles = value);
        Check("内容分析", spec.AnalyzeContent, value => spec.AnalyzeContent = value);
        Check("分析画面", spec.AnalyzeFrames, value => spec.AnalyzeFrames = value);
        Choice("字幕来源", spec.TranscriptSource, Enum.GetValues<VideoTranscriptSource>(), value => spec.TranscriptSource = value,
            value => value switch { VideoTranscriptSource.Automatic => "自动", VideoTranscriptSource.Embedded => "内嵌字幕", VideoTranscriptSource.Speech => "语音识别", _ => "外部字幕" });
        var subtitle = Ui.Button("选择外部字幕…", async () =>
        {
            var picked = await ToolFilePicker.OpenFilesAsync(_window, new() { Title = Localization.Text("字幕文件"),
                FileTypeFilter = [new(Localization.Text("字幕文件")) { Patterns = ["*.srt", "*.vtt", "*.ass", "*.ssa"] }] });
            if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) { spec.SubtitleFile = path; Changed(); }
        }); Children.Add(subtitle);
        Number("字幕轨道（自动为 -1）", spec.SubtitleTrack, -1, 64, 1, value => spec.SubtitleTrack = (int)value);
        Number("音轨", spec.AudioTrack, 0, 64, 1, value => spec.AudioTrack = (int)value);
        Check("自动选择音轨", spec.AutoSelectAudioTrack, value => spec.AutoSelectAudioTrack = value);
        Check("所有音轨", spec.AllAudioTracks, value => spec.AllAudioTracks = value);
        Speech(spec.Speech);
        Number("画面采样数", spec.FrameCount, 1, 48, 1, value => spec.FrameCount = (int)value);
        Choice("输出语言", spec.OutputLanguage, new[] { "简体中文", "English", "日本語" }, value => spec.OutputLanguage = value);
        Text("分析重点", spec.Focus, value => spec.Focus = value);
        Number("长视频阈值（分钟）", spec.LongVideoThresholdMinutes, 20, 1440, 1, value => spec.LongVideoThresholdMinutes = value);
        Number("长视频并发", spec.LongSummaryConcurrency, 1, 16, 1, value => spec.LongSummaryConcurrency = (int)value);
        Number("每分钟请求数", spec.LongSummaryRequestsPerMinute, 1, 10000, 1, value => spec.LongSummaryRequestsPerMinute = (int)value);
        Number("每分钟 Token 数", spec.LongSummaryTokensPerMinute, 8192, 10000000, 8192, value => spec.LongSummaryTokensPerMinute = (int)value);
        Check("严格顺序处理", spec.StrictSequential, value => spec.StrictSequential = value);
        Folder(Path.GetDirectoryName(_job.Output)!, folder => _job.Output = MediaEngine.UniqueOutput(folder,
            Path.GetFileName(_job.Output), "", _reserved().Append(_job.Output).Concat(_job.Inputs), directory: true));
    }
    private void Tags()
    {
        var spec = _job.Options.MediaTag!;
        Number("视频采样帧数", spec.Analysis.VideoFrames, 1, 32, 1, value => spec.Analysis = spec.Analysis with { VideoFrames = (int)value });
        Number("标签阈值", spec.Threshold, .05, .95, .05, value => spec.Threshold = value);
        Number("场景相似度", spec.SceneThreshold, .05, .95, .05, value => spec.SceneThreshold = value);
        Number("场景分差", spec.SceneMargin, 0, .5, .01, value => spec.SceneMargin = value);
        Check("复用相似帧", spec.Analysis.ReuseSimilarFrames, value => spec.Analysis = spec.Analysis with { ReuseSimilarFrames = value });
        Check("识别场景", spec.Analysis.RecognizeScenes, value => spec.Analysis = spec.Analysis with { RecognizeScenes = value });
        Check("仅真人", spec.Analysis.RealPeopleOnly, value => spec.Analysis = spec.Analysis with { RealPeopleOnly = value,
            RecognizeNsfw = value && _settings.EnableNsfwContent });
        Check("生成画面描述", spec.Analysis.GenerateCaptions, value => spec.Analysis = spec.Analysis with { GenerateCaptions = value });
        Text("画面描述提示词", spec.Analysis.CaptionPrompt ?? "", value => spec.Analysis = spec.Analysis with { CaptionPrompt = value });
        Check("保存标签 TXT", spec.WriteTextReport, value =>
        {
            spec.WriteTextReport = value;
            _job.Options.Format = value ? "txt" : "json";
            _job.Output = value ? MediaEngine.UniqueOutput(Path.GetDirectoryName(_job.Inputs[0])!, Path.GetFileNameWithoutExtension(_job.Inputs[0]), "txt", _reserved().Append(_job.Output).Concat(_job.Inputs))
                : AiTaskResults.InternalOutputFor(_job);
        });
    }
    private void Classification()
    {
        var spec = _job.Options.FolderClassification!;
        var rules = FolderClassificationRule.DefaultRules().Concat(spec.Rules).DistinctBy(rule => rule.Id)
            .Where(rule => _settings.EnableNsfwContent || !MediaPrivacy.IsSensitiveRule(rule)).ToArray();
        foreach (var rule in rules)
            Check(rule.Name, spec.Rules.Any(selected => selected.Id == rule.Id), selected =>
            {
                spec.Rules = spec.Rules.Where(item => item.Id != rule.Id).Concat(selected ? [rule] : Array.Empty<FolderClassificationRule>()).ToArray();
                spec.Analysis = spec.Analysis with { SemanticCandidates = spec.Rules.SelectMany(item => item.Candidates()).ToArray() };
            });
        Number("视频采样帧数", spec.Analysis.VideoFrames, 1, 32, 1, value => spec.Analysis = spec.Analysis with { VideoFrames = (int)value });
        Number("标签阈值", spec.TagThreshold, 0, 1, .05, value => spec.TagThreshold = value);
        Number("服装相似度", spec.Rules.FirstOrDefault(rule => rule.ByOutfit)?.OutfitSimilarity ?? .64, .5, .95, .01,
            value => spec.Rules = spec.Rules.Select(rule => rule.ByOutfit ? rule with { OutfitSimilarity = value } : rule).ToArray());
        Folder(spec.OutputFolder, value => spec.OutputFolder = value);
        Check("按视频 / 图片分目录", spec.SplitTypes, value => spec.SplitTypes = value);
        Check("为每个文件保存标签 TXT", spec.WriteText, value => spec.WriteText = value);
        Check("移动到分类目录", spec.Move, value => spec.Move = value);
        Children.Add(MsUi.Caption("分析完成后先核对分组，再执行整理。"));
    }
}
