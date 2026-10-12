using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;
using static AvaMedia.Desktop.Classification.ClassifyUi;

namespace AvaMedia.Desktop.Classification;

/// <summary>Compact "新建 AI 媒体整理" window: sources, what to classify by, destination; runs as a background task.</summary>
public sealed class ClassificationSubmitWindow : Window
{
    private const int MaxRules = 8;
    private readonly IMediaEngine _engine;
    private readonly AppSettings _settings;
    private readonly Action<Job> _enqueue;
    private readonly Func<Window, Task> _manageModels;
    private readonly Action _showTasks;
    private readonly Storage _storage = new();
    private readonly List<Source> _sources = [];
    private readonly List<FolderClassificationRule> _selected = [];
    private readonly FolderClassificationWindow.Preferences _preferences;
    private readonly StackPanel _sourceCards = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private readonly TextBlock _sourceSummary = T("", 11.5, brush: Muted, raw: true), _ruleSummary = T("", 11.5, brush: Muted, raw: true),
        _destName = T("分类结果", 12.5, FontWeight.SemiBold), _destPath = T("", 11, brush: Muted, raw: true);
    private readonly Grid _ruleGrid = new() { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 8, RowSpacing = 8 };
    private readonly CheckBox _recursive = new() { Content = L("包含子文件夹"), IsChecked = true }, _split = new() { Content = L("按视频 / 图片分目录") },
        _writeText = new() { Content = L("为每个文件保存标签 TXT") };
    private readonly Slider _similarity = new() { Minimum = .5, Maximum = .95, Value = .64, SmallChange = .01 },
        _frames = new() { Minimum = 1, Maximum = 32, Value = 12, IsSnapToTickEnabled = true, TickFrequency = 1 },
        _threshold = new() { Minimum = 0, Maximum = 1, Value = .5, SmallChange = .05 };
    private readonly Button _submit;
    private string _output = "";
    private bool _move, _busy;
    private sealed record Source(string Path, bool Folder) { public string[] Files = []; public int Images, Videos; }

    public ClassificationSubmitWindow(IMediaEngine engine, AppSettings settings, IEnumerable<string>? initial, Action<Job> enqueue,
        Func<Window, Task> manageModels, Action showTasks)
    {
        _engine = engine; _settings = settings; _enqueue = enqueue; _manageModels = manageModels; _showTasks = showTasks;
        _preferences = _storage.LoadToolOptions<FolderClassificationWindow.Preferences>("folder-classification") ?? new();
        Title = L("新建 AI 媒体整理"); Width = 900; Height = 520; CanResize = false; Bind(BackgroundProperty, Resource("UiClassificationPage"));
        Styles.Add(new StyleInclude(new Uri("avares://AvaMedia.Desktop/")) { Source = new Uri("avares://AvaMedia.Desktop/Classification/ClassificationSubmitStyles.axaml") });
        foreach (var check in new[] { _recursive, _split, _writeText }) check.Classes.Add("classify-check");
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowArtwork.SetKind(this, "gear");
        _output = _preferences.OutputFolder; _recursive.IsChecked = _preferences.Recursive; _split.IsChecked = _preferences.SplitTypes;
        _writeText.IsChecked = _preferences.WriteText; _frames.Value = Math.Clamp(_preferences.VideoFrames, 1, 32); _threshold.Value = (double)_preferences.TagThreshold;
        var defaults = _preferences.SceneRules.Length > 0 ? _preferences.SceneRules : FolderClassificationRule.DefaultRules();
        try { FolderClassification.ValidateRules(defaults); _selected.AddRange(defaults.Where(Visible)); } catch (ArgumentException) { }
        if (defaults.FirstOrDefault(r => r.ByOutfit) is { } outfit) _similarity.Value = outfit.OutfitSimilarity;
        _submit = Pill("后台运行分类", kind: "primary", click: () => _ = SubmitAsync(), height: 32);
        Content = Build(); RenderRules(); RenderSources(); RenderDestination();
        if (initial is not null) Opened += async (_, _) => await AddAsync(initial);
    }

    private bool Visible(FolderClassificationRule rule) => _settings.EnableNsfwContent || !MediaPrivacy.IsSensitiveRule(rule);

    private Control Build()
    {
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new(22, 14, 22, 16), RowSpacing = 12 };
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(T("新建 AI 媒体整理", 15, FontWeight.Bold)); titles.Children.Add(T("选择来源与分类方式，任务会在后台运行", 11.5, brush: Muted));
        header.Children.Add(titles);
        var tasks = Pill("查看任务列表", click: _showTasks); Grid.SetColumn(tasks, 1); header.Children.Add(tasks);
        root.Children.Add(header);

        var columns = new Grid { ColumnDefinitions = new("*,1.75*"), ColumnSpacing = 18 }; Grid.SetRow(columns, 1);
        var left = new StackPanel { Spacing = 10 };
        left.Children.Add(Heading("来源", _sourceSummary));
        left.Children.Add(new ScrollViewer { Content = _sourceCards, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(Pill("添加文件夹…", click: () => _ = AddFoldersAsync()));
        buttons.Children.Add(Pill("添加文件…", click: () => _ = AddFilesAsync()));
        buttons.Children.Add(Pill("重新扫描", click: () => _ = RescanAsync()));
        left.Children.Add(buttons); left.Children.Add(_recursive);
        _recursive.IsCheckedChanged += (_, _) => _ = RescanAsync();
        var place = Heading("放到哪里", null); place.Margin = new(0, 8, 0, 0); left.Children.Add(place);
        var dest = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10 };
        dest.Children.Add(new LucideIcon { Kind = "folder-closed", Width = 15, Height = 15, [!LucideIcon.ForegroundProperty] = Resource(Blue) });
        var destText = new StackPanel(); destText.Children.Add(_destName); destText.Children.Add(_destPath); Grid.SetColumn(destText, 1); dest.Children.Add(destText);
        var change = Pill("更改…", click: () => _ = ChangeOutputAsync()); Grid.SetColumn(change, 2); dest.Children.Add(change);
        left.Children.Add(new Border { [!Border.BackgroundProperty] = Resource(Surface), [!Border.BorderBrushProperty] = Resource(Hairline), BorderThickness = new(1), CornerRadius = new(10), Padding = new(12, 8), Child = dest, [!Border.BoxShadowProperty] = Resource("UiCardShadow") });
        left.Children.Add(Segmented(["复制到分类目录", "移动到分类目录"], _move ? 1 : 0, index => _move = index == 1));
        left.Children.Add(T("分析在后台进行；完成后先核对分组，再执行整理。", 11, brush: Muted));
        columns.Children.Add(left);

        var right = new StackPanel { Spacing = 10 };
        right.Children.Add(Heading("按什么分", _ruleSummary));
        right.Children.Add(new ScrollViewer { Content = _ruleGrid, MaxHeight = 154,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        var more = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        more.Children.Add(Pill("添加分类组…", click: () => _ = OpenAdvancedAsync()));
        var saved = Pill("已保存分类", "chevron-down", iconAfter: true);
        var menu = new MenuFlyout();
        foreach (var rule in _preferences.SavedRules.Where(Visible))
            menu.Items.Add(new MenuItem { Header = rule.Name, Command = new Command(() => Toggle(rule)) });
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = L("暂无已保存分类"), IsEnabled = false });
        saved.Flyout = menu; more.Children.Add(saved);
        right.Children.Add(more);
        var tuning = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 18 };
        tuning.Children.Add(Tuner("服装相似度", _similarity, v => v.ToString("0.00")));
        var frames = Tuner("视频采样帧数", _frames, v => $"{v:0} {L("帧")}", segmented: true); Grid.SetColumn(frames, 1); tuning.Children.Add(frames);
        var thr = Tuner("标签阈值", _threshold, v => v.ToString("0.00")); Grid.SetColumn(thr, 2); tuning.Children.Add(thr);
        right.Children.Add(new Border { [!Border.BackgroundProperty] = Resource(Surface), [!Border.BorderBrushProperty] = Resource(Hairline), BorderThickness = new(1), CornerRadius = new(12), Padding = new(12, 10), Child = tuning, [!Border.BoxShadowProperty] = Resource("UiCardShadow") });
        var options = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto"), ColumnSpacing = 14 };
        options.Children.Add(_split); Grid.SetColumn(_writeText, 1); options.Children.Add(_writeText);
        var models = Pill("模型管理…", kind: "link", click: () => _ = _manageModels(this)); models.Padding = new(0); Grid.SetColumn(models, 3); options.Children.Add(models);
        right.Children.Add(options);
        Grid.SetColumn(right, 1); columns.Children.Add(right);
        root.Children.Add(columns);

        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 }; Grid.SetRow(footer, 2);
        footer.Children.Add(T("首次运行会下载 JoyTag、MediaPipe 与 DINOv2 模型", 11, brush: Muted));
        var cancel = Pill("取消", click: Close, height: 32); Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        Grid.SetColumn(_submit, 2); footer.Children.Add(_submit);
        root.Children.Add(footer);
        return root;
    }

    private static Control Heading(string title, TextBlock? detail)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(T(title, 13, FontWeight.Bold)); if (detail is not null) row.Children.Add(detail);
        return row;
    }

    private static Control Tuner(string label, Slider slider, Func<double, string> format, bool segmented = false)
    {
        var value = T(format(slider.Value), 15, FontWeight.Bold, raw: true);
        slider.Classes.Add("classify-tuner");
        AutomationProperties.SetName(slider, L(label));
        var track = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        void RefreshTrack()
        {
            value.Text = format(slider.Value);
            track.Children.Clear(); track.ColumnDefinitions.Clear();
            if (segmented)
            {
                // Each dash represents one sampled frame; the value always comes from the real slider.
                track.ColumnSpacing = 2;
                for (var i = 0; i < (int)slider.Value; i++)
                {
                    track.ColumnDefinitions.Add(new(1, GridUnitType.Star));
                    var dash = new Border { [!Border.BackgroundProperty] = Resource(Blue), CornerRadius = new(1) };
                    Grid.SetColumn(dash, i); track.Children.Add(dash);
                }
            }
            else track.Children.Add(Bar((slider.Value - slider.Minimum) / (slider.Maximum - slider.Minimum), Blue));
        }
        slider.ValueChanged += (_, _) => RefreshTrack();
        RefreshTrack();
        var input = new Grid { Height = 16 };
        input.Children.Add(track); input.Children.Add(slider);
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(T(label, 11, brush: Muted)); panel.Children.Add(value); panel.Children.Add(input);
        return panel;
    }

    private void RenderRules()
    {
        _ruleGrid.Children.Clear(); _ruleGrid.RowDefinitions.Clear();
        string[] order = ["similar-outfits", "scenery", "people-count", "framing", "video-duration", "body-pose",
            "viewpoint", "everyday-clothes", "indoor-location", "empty-shot", "contact", "age-appearance"];
        var presets = FolderClassificationRule.Presets.Where(Visible)
            .OrderBy(rule => Array.IndexOf(order, rule.Id) is var index && index >= 0 ? index : order.Length).ToList();
        foreach (var extra in _selected.Where(rule => presets.All(p => p.Id != rule.Id))) presets.Add(extra);
        for (var i = 0; i < presets.Count; i++)
        {
            if (i % 4 == 0) _ruleGrid.RowDefinitions.Add(new(GridLength.Auto));
            var rule = presets[i]; var on = _selected.Any(r => r.Id == rule.Id);
            var text = new StackPanel { Spacing = 2 };
            var title = new Grid { ColumnDefinitions = new(on ? "11,*" : "*"), ColumnSpacing = 5 };
            if (on) title.Children.Add(new LucideIcon { Kind = "check", Width = 11, Height = 11, [!LucideIcon.ForegroundProperty] = Resource("UiClassificationRuleText") });
            var name = T(rule.Name, 12.5, FontWeight.SemiBold, on ? "UiClassificationRuleText" : Ink);
            Grid.SetColumn(name, on ? 1 : 0); title.Children.Add(name);
            text.Children.Add(title);
            var description = rule.ByOutfit ? L("按拍摄背景和服装分组") : string.Join(" / ", rule.Categories.Take(3).Select(c => L(c.Name))) + (rule.Categories.Length > 3 ? " …" : "");
            text.Children.Add(new TextBlock { Text = description, FontSize = 10.5, [!TextBlock.ForegroundProperty] = Resource(on ? "UiAccentDescription" : Muted), TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button
            {
                Content = text, [!Button.BackgroundProperty] = Resource(on ? "UiClassificationRuleFace" : Surface), [!Button.BorderBrushProperty] = Resource(on ? Blue : Hairline), BorderThickness = new(1), CornerRadius = new(10),
                Padding = new(10, 5), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = 46, MinHeight = 46
            };
            button.Classes.Add("classify-rule");
            AutomationProperties.SetName(button, L(rule.Name));
            ToolTip.SetTip(button, L(rule.Name) + " · " + description);
            button.Click += (_, _) => Toggle(rule);
            Grid.SetRow(button, i / 4); Grid.SetColumn(button, i % 4); _ruleGrid.Children.Add(button);
        }
        _ruleSummary.Text = $"{L("已选")} {_selected.Count} {L("组")} · {L("最多")} {MaxRules} {L("组")}";
        UpdateSubmit();
    }

    private void Toggle(FolderClassificationRule rule)
    {
        var index = _selected.FindIndex(r => r.Id == rule.Id);
        if (index >= 0) _selected.RemoveAt(index);
        else if (_selected.Count < MaxRules) _selected.Add(rule);
        RenderRules();
    }

    private async Task AddFoldersAsync()
    {
        var folders = await ToolFilePicker.OpenFoldersAsync(this, new() { AllowMultiple = true });
        await AddAsync(folders.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async Task AddFilesAsync() => await AddAsync(await Ui.Pick(this, "选择图片或视频"));

    public async Task AddAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths.Select(Path.GetFullPath))
        {
            if (_sources.Any(s => BatchRename.PathComparer.Equals(s.Path, path))) continue;
            if (Directory.Exists(path)) _sources.Add(new(path, true));
            else if (File.Exists(path) && MediaTagService.Supports(path)) _sources.Add(new(path, false));
        }
        if (string.IsNullOrWhiteSpace(_output) && _sources.FirstOrDefault() is { } first)
            _output = Path.Combine(first.Folder ? first.Path : Path.GetDirectoryName(first.Path)!, "分类结果");
        RenderDestination(); await RescanAsync();
    }

    private async Task RescanAsync()
    {
        var recursive = _recursive.IsChecked == true; var output = _output;
        foreach (var source in _sources)
        {
            var scan = await Task.Run(() => FolderClassification.Scan([source.Path], recursive, string.IsNullOrWhiteSpace(output) ? null : output, CancellationToken.None));
            source.Files = scan.Files.Where(MediaTagService.Supports).ToArray();
            source.Videos = source.Files.Count(VideoFormats.IsVideo); source.Images = source.Files.Length - source.Videos;
        }
        RenderSources();
    }

    private void RenderSources()
    {
        _sourceCards.Children.Clear();
        foreach (var source in _sources)
        {
            var card = new StackPanel { Width = 128, Spacing = 2 };
            var mosaic = new Grid { Width = 128, Height = 96, ColumnDefinitions = new("*,2,*"), RowDefinitions = new("*,2,*") };
            var samples = source.Files.Take(4).ToArray();
            for (var i = 0; i < 4; i++)
            {
                var thumb = new ClassifyThumb(64) { CornerRadius = new(i == 0 ? 10 : 0, i == 1 ? 10 : 0, i == 3 ? 10 : 0, i == 2 ? 10 : 0) };
                if (i < samples.Length) thumb.SetSource(samples[i], VideoFormats.IsVideo(samples[i]) ? 1 : null);
                Grid.SetColumn(thumb, i % 2 * 2); Grid.SetRow(thumb, i / 2 * 2); mosaic.Children.Add(thumb);
            }
            card.Children.Add(new Border { Width = 128, Height = 96, CornerRadius = new(10), ClipToBounds = true, Child = mosaic, Margin = new(0, 0, 0, 5) });
            card.Children.Add(T(Path.GetFileName(source.Path.TrimEnd(Path.DirectorySeparatorChar)), 12.5, FontWeight.Bold, raw: true));
            var count = source.Videos > 0 && source.Images > 0 ? Localization.Format($"{source.Files.Length} 个图片 / 视频")
                : source.Videos > 0 ? Localization.Format($"{source.Files.Length} 个视频") : Localization.Format($"{source.Files.Length} 个图片");
            card.Children.Add(T(count, 11, brush: Muted, raw: true));
            card.ContextMenu = new ContextMenu { Items = { new MenuItem { Header = L("移除"), Command = new Command(() => { _sources.Remove(source); RenderSources(); }) } } };
            _sourceCards.Children.Add(card);
        }
        if (_sources.Count == 0) _sourceCards.Children.Add(T("尚未添加来源", 12, brush: Muted));
        _sourceSummary.Text = $"{_sources.Count(s => s.Folder)} {L("个文件夹")} · {_sources.Sum(s => s.Files.Length)} {L("个文件")}";
        UpdateSubmit();
    }

    private void RenderDestination()
    {
        _destName.Text = L("分类结果");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _destPath.Text = string.IsNullOrWhiteSpace(_output) ? L("尚未选择")
            : home.Length > 0 && _output.StartsWith(home, StringComparison.Ordinal) ? "~" + _output[home.Length..] : _output;
        ToolTip.SetTip(_destPath, _output);
    }

    private async Task ChangeOutputAsync()
    {
        if (await Ui.Folder(this, "选择分类目录") is { } folder) { _output = folder; RenderDestination(); await RescanAsync(); }
    }

    private async Task OpenAdvancedAsync()
    {
        // The full rule editor stays in the advanced window; it reuses the same saved preferences.
        if (Owner is MainWindow main) main.OpenAdvancedClassification(_sources.Select(s => s.Path).ToArray());
        Close(); await Task.CompletedTask;
    }

    private void UpdateSubmit() => _submit.IsEnabled = !_busy && _selected.Count > 0 && _sources.Sum(s => s.Files.Length) > 0;

    private async Task SubmitAsync()
    {
        if (_busy) return;
        _busy = true; UpdateSubmit();
        try
        {
            if (string.IsNullOrWhiteSpace(_output)) throw new ArgumentException(L("请选择分类目录。"));
            var rules = _selected.Select(rule => rule.ByOutfit ? rule with { OutfitSimilarity = Math.Round(_similarity.Value, 2) } : rule).ToArray();
            FolderClassification.ValidateRules(rules);
            var options = new MediaTagOptions((int)_frames.Value) { SemanticCandidates = rules.SelectMany(rule => rule.Candidates()).ToArray() };
            options.Validate();
            var download = options.NeedsSemanticModel && !await SemanticModelConsent.IsInstalledAsync();
            if (download && !await SemanticModelConsent.ConfirmAsync(this, L("AI 媒体整理"))) return;
            var inputs = _sources.SelectMany(s => s.Files).Distinct(BatchRename.PathComparer).ToArray();
            var name = string.Join("、", _sources.Take(2).Select(s => Path.GetFileName(s.Path.TrimEnd(Path.DirectorySeparatorChar))));
            var job = new Job
            {
                FeatureId = "folder-classification", Inputs = inputs, DownloadTitle = name.Length > 0 ? name : Localization.Format($"分类任务 · {DateTime.Now:HH:mm:ss}"),
                Options = new() { Format = "", FolderClassification = new()
                {
                    Rules = rules, Analysis = options, TagThreshold = Math.Round(_threshold.Value, 2), IncludeNsfw = _settings.EnableNsfwContent,
                    OutputFolder = Path.GetFullPath(_output), SplitTypes = _split.IsChecked == true, WriteText = _writeText.IsChecked == true,
                    Move = _move, AllowSemanticDownload = download, LastJournal = _preferences.LastJournal
                } }
            };
            job.Output = FolderClassificationTaskStore.Folder(job);
            job.ClassificationSnapshot = new(inputs.Select(path => new FolderClassificationTaskFile(path)).ToArray());
            await Task.Run(async () => { foreach (var file in job.ClassificationSnapshot.Files) await FolderClassificationTaskStore.SaveFileAsync(job, file); });
            _preferences.SceneRules = rules; _preferences.OutputFolder = _output; _preferences.Recursive = _recursive.IsChecked == true;
            _preferences.SplitTypes = _split.IsChecked == true; _preferences.WriteText = _writeText.IsChecked == true;
            _preferences.VideoFrames = (int)_frames.Value; _preferences.TagThreshold = (decimal)Math.Round(_threshold.Value, 2);
            _storage.SaveToolOptions("folder-classification", _preferences);
            _enqueue(job); Close();
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        { await Ui.Message(this, "无法开始分类", error.Message); }
        finally { _busy = false; UpdateSubmit(); }
    }

    private sealed class Command(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
