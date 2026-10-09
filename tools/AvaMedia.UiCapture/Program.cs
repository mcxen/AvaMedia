using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;

internal static class Program
{
    private static readonly string[] Themes = ["Light", "Dark", "MacOS9", "WindowsXP"];
    private static readonly string[] Names = ["light", "dark", "macos9", "winxp"];
    private static readonly CancellationTokenSource Lifetime = new();
    private static string _output = "";
    private static string _samples = "";
    private static bool _hold;
    private static bool _tracePointer;
    private static bool _reviewOnly;
    private static int _reviewTheme = Themes.Length - 1;
    private static readonly List<object> Captures = [];

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: AvaMedia.UiCapture <output> <sample-directory> [--hold] [--review-only] [--theme=MacOS9] [--trace-pointer]"); return 2; }
        _output = Path.GetFullPath(args[0]); _samples = Path.GetFullPath(args[1]); _hold = args.Contains("--hold");
        _tracePointer = args.Contains("--trace-pointer");
        _reviewOnly = args.Contains("--review-only");
        var themeArgument = args.FirstOrDefault(arg => arg.StartsWith("--theme=", StringComparison.Ordinal));
        if (themeArgument is not null)
        {
            _reviewTheme = Array.IndexOf(Themes, themeArgument[8..]);
            if (_reviewTheme < 0) { Console.Error.WriteLine("Theme must be Light, Dark, MacOS9 or WindowsXP."); return 2; }
        }
        Directory.CreateDirectory(_output);
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().SetupWithoutStarting();
        Localization.Apply("zh-CN"); Motion.SetReducedMotion(true);
        Dispatcher.UIThread.Post(async () =>
        {
            try { await Run(); }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; Lifetime.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(Lifetime.Token);
        return Environment.ExitCode;
    }

    private static async Task Settle(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(350);
        window.UpdateLayout();
    }

    private static async Task Capture(Window window, string name)
    {
        await Settle(window);
        var size = window.ClientSize;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(_output, name + ".png"));
        Captures.Add(new { file = name + ".png", theme = window.ActualThemeVariant.Key?.ToString(), width = size.Width, height = size.Height });
        Console.WriteLine("Captured " + name);
    }

    private static async Task Run()
    {
        var video = Path.Combine(_samples, "航天员.mp4");
        var audio = Path.Combine(_samples, "示例音轨.wav");
        var image = Path.Combine(_samples, "航天员.png");
        var document = Path.Combine(_samples, "使用说明.txt");
        foreach (var file in new[] { video, audio, image, document }) if (!File.Exists(file)) throw new FileNotFoundException("Missing screenshot sample", file);
        for (var index = _reviewOnly ? _reviewTheme : 0; index < Themes.Length; index++)
        {
            var theme = Themes[index]; var name = Names[index];
            var state = new Storage(Path.Combine(_output, "state", name));
            var settings = new AppSettings { Theme = theme, Language = "zh-CN", OutputFolder = Path.Combine(_output, "output"), CloseToTray = false,
                CheckForUpdates = false, AutoUpdate = false, AutoDownloadRepairModel = false, ReduceMotion = true, PlayerNativeHighResolution = false, EnableBetaFeatures = true };
            state.SaveSettings(settings);
            state.SaveJobs(new[]
            {
                new Job { Inputs = [video], FeatureId = "mp4", Output = Path.Combine(settings.OutputFolder, "航天员.mp4"), Options = new() { Format = "mp4" }, State = JobState.Waiting },
                new Job { Inputs = [audio], FeatureId = "audio-mp3", Output = Path.Combine(settings.OutputFolder, "示例音轨.mp3"), Options = new() { Format = "mp3" }, State = JobState.Waiting },
                new Job { Inputs = [image], FeatureId = "image-webp", Output = Path.Combine(settings.OutputFolder, "航天员.webp"), Options = new() { Format = "webp" }, State = JobState.Waiting }
            });
            var main = new MainWindow(state) { Width = 1440, Height = 860 };
            main.Show(); await Settle(main);
            await Task.WhenAll(main.GetVisualDescendants().OfType<JobRowView>().Select(row => row.Ready));
            if (_reviewOnly)
            {
                var review = Components(theme, main, state, video, audio, image, document);
                review.Closed += (_, _) => { CloseMain(main); Lifetime.Cancel(); };
                review.Show(main); Console.WriteLine("Native review ready; close the Components window to exit."); return;
            }
            await Capture(main, "main-" + name);
            var engine = main.Engine;
            var routes = new MediaRouteWindow(engine, [video, audio, image, document], new MediaFileRouter(true));
            routes.Show(main); await Task.Delay(700); await Capture(routes, "route-" + name); routes.Close();
            var preferences = new SettingsWindow(settings); preferences.Show(main);
            await Capture(preferences, "settings-" + name); preferences.Close();
            var options = new OptionsWindow(new() { Format = "mp4" }, presetStorage: state);
            options.Show(main); await Capture(options, "options-" + name); options.Close();
            var convert = new ConvertWindow(engine, Catalog.Find("mp4"), settings.OutputFolder, [video]);
            convert.Show(main); await Task.Delay(300); await Capture(convert, "convert-" + name); convert.Close();
            var download = new DownloadWindow(settings, settings.OutputFolder); download.Show(main);
            await Capture(download, "download-" + name); download.Close();
            var editor = new EditorWindow(engine, video, new() { Start = .5, End = 3.5 }, "quick-workflow");
            editor.SetWorkflowStep(1, 1); editor.Show(main); await editor.Ready;
            await editor.PreviewReady; await editor.ThumbnailsReady; await Capture(editor, "editor-" + name);
            var clip = editor.ReadClipEdit(); editor.Close();
            var export = new ClipExportWindow([clip], settings.OutputFolder); export.Show(main);
            await Capture(export, "export-" + name); export.Close();
            var player = new PlayerWindow(engine, [video], preferences: state); player.Show(main);
            await player.Ready; await player.FirstFrameReady.WaitAsync(TimeSpan.FromSeconds(15));
            if (player.IsPlaying) await player.TogglePlaybackAsync();
            await Capture(player, "player-" + name); player.Close();
            var widgets = MediaComponents(image);
            widgets.Show(main); await Capture(widgets, "media-components-" + name); widgets.Close();
            var gallery = Components(theme, main, state, video, audio, image, document);
            gallery.Show(main); await Capture(gallery, "components-" + name);
            if (_hold && index == Themes.Length - 1)
            {
                gallery.Closed += (_, _) => { CloseMain(main); Lifetime.Cancel(); };
                var route = new MediaRouteWindow(engine, [video, audio, image, document], new MediaFileRouter(true));
                route.Show(main); gallery.Activate();
            }
            else { gallery.Close(); CloseMain(main); }
        }
        await File.WriteAllTextAsync(Path.Combine(_output, "captures.json"), JsonSerializer.Serialize(new
        {
            capturedUtc = DateTimeOffset.UtcNow, platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            version = typeof(MainWindow).Assembly.GetName().Version?.ToString(), captures = Captures,
            sampleOnly = true, mediaExportsExecuted = false, scale = 1
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!_hold) Lifetime.Cancel();
        else Console.WriteLine("Native review ready; close the Components window to exit.");
    }

    private static void CloseMain(MainWindow main) => typeof(MainWindow).GetMethod("CloseSetupPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);

    private static StackPanel Row(params Control[] controls) => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 }.With(controls);
    private static StackPanel With(this StackPanel panel, IEnumerable<Control> controls) { foreach (var control in controls) panel.Children.Add(control); return panel; }
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold };

    private static Window MediaComponents(string image)
    {
        var window = new Window { Title = "AvaMedia · 媒体组件检查", Width = 1160, Height = 940, MinWidth = 980, MinHeight = 820 };
        var root = new StackPanel { Margin = new(16), Spacing = 12 };
        root.Children.Add(Label("字幕样式与画面预览"));
        var subtitles = new SubtitleStyleEditor(new() { SubtitleFontSize = 48, SubtitleAlignment = 2, SubtitleColor = "#FFFFFF" });
        subtitles.SetFrame(File.ReadAllBytes(image), 512, 512);
        root.Children.Add(subtitles);
        root.Children.Add(Label("AI 标签图表 · 示例数据"));
        var charts = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 16 };
        var bars = new AiTagChart { Height = 190 };
        TagChartBar[] observations = [new("portrait", "人物", .88, .94, .78, .88), new("uniform", "服装", .72, .83, .68, .72), new("indoor", "室内", .57, .66, .54, .57)];
        bars.Update(observations, [], .4, 4, 1.5, "portrait", false);
        charts.Children.Add(bars);
        var timeline = new AiTagChart { Timeline = true, Height = 190 };
        timeline.Update([], [new("portrait", [new(0, .64), new(1, .88), new(2, .94), new(3, .76), new(4, .82)], ModelCatalog.JoyTagId, false, 0),
            new("portrait", [new(0, .12), new(1, .42), new(2, .56), new(3, .30), new(4, .48)], ModelCatalog.EmbeddingId, true, 0)],
            .4, 4, 1.5, "portrait", false, [new(ModelCatalog.JoyTagId, .4, false), new(ModelCatalog.EmbeddingId, .25, true)]);
        Grid.SetColumn(timeline, 1); charts.Children.Add(timeline); root.Children.Add(charts);
        var previews = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 16 };
        var bitmap = new Bitmap(image);
        var compare = new StackPanel { Spacing = 8 };
        compare.Children.Add(Label("图片对比"));
        var comparison = new ImageCompareView { Source = bitmap, Result = bitmap, Height = 180 };
        comparison.Bind(ImageCompareView.SurfaceProperty, new DynamicResourceExtension("UiSurface"));
        comparison.Bind(ImageCompareView.CheckerProperty, new DynamicResourceExtension("UiSurfaceRaised"));
        comparison.Bind(ImageCompareView.AccentProperty, new DynamicResourceExtension("UiAccent"));
        compare.Children.Add(comparison);
        previews.Children.Add(compare);
        var information = new StackPanel { Spacing = 8 };
        information.Children.Add(Label("下载监控与摘要 · 示例数据"));
        var speed = new DownloadSpeedMonitor();
        speed.Update(new(2.4 * 1024 * 1024, 3.2 * 1024 * 1024, 1.8 * 1024 * 1024),
            Enumerable.Range(0, 60).Select(i => (1.8 + Math.Sin(i / 5d) * .8) * 1024 * 1024).ToArray());
        information.Children.Add(speed);
        var summaryType = typeof(MainWindow).Assembly.GetType("AvaMedia.Desktop.Controls.SummaryDocumentView")!;
        information.Children.Add((Control)Activator.CreateInstance(summaryType,
            "## 画面摘要\n\n人物站在室内，背景可见旗帜与模型。\n\n- 00:00:00 · 人物与服装\n- 00:00:02 · 背景细节")!);
        Grid.SetColumn(information, 1); previews.Children.Add(information); root.Children.Add(previews);
        window.Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        window.Closed += (_, _) => { subtitles.Dispose(); bitmap.Dispose(); };
        return window;
    }

    private static Window Components(string theme, MainWindow owner, Storage state, params string[] files)
    {
        var window = new Window { Title = "AvaMedia · 外观组件检查", Width = 1040, Height = 760, MinWidth = 820, MinHeight = 580, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var themePicker = new ComboBox { ItemsSource = Themes, SelectedItem = theme, Width = 160 };
        themePicker.SelectionChanged += (_, _) => { if (themePicker.SelectedItem is string value) Skin.Apply(value); };
        var route = new Button { Content = "文件路由" }; route.Click += (_, _) => new MediaRouteWindow(owner.Engine, files, new MediaFileRouter(true)).Show(window);
        var settings = new Button { Content = "偏好设置" }; settings.Click += (_, _) => new SettingsWindow(state.LoadSettings()).Show(window);
        var save = new Button { Content = "保存组件截图" };
        save.Click += async (_, _) => await Capture(window, "review-components-" + Names[Array.IndexOf(Themes, themePicker.SelectedItem as string ?? theme)]);
        var view = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new(20), RowSpacing = 16 };
        var menu = new Menu { Items = { new MenuItem { Header = "文件", Items = { new MenuItem { Header = "打开…", InputGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.O, Avalonia.Input.KeyModifiers.Control) },
            new MenuItem { Header = "最近文件", Items = { new MenuItem { Header = "航天员.mp4" } } }, new Separator(), new MenuItem { Header = "不可用操作", IsEnabled = false } } },
            new MenuItem { Header = "显示", Items = { new MenuItem { Header = "显示预览", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true }, new MenuItem { Header = "工具栏", ToggleType = MenuItemToggleType.CheckBox } } } } };
        view.Children.Add(menu);
        var toolbar = Row(Label("外观"), themePicker, route, settings, save); Grid.SetRow(toolbar, 1); view.Children.Add(toolbar);
        var columns = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 24 };
        var left = new StackPanel { Spacing = 14 };
        left.Children.Add(Label("按钮与输入"));
        var primary = new Button { Content = "确认", IsDefault = true, Classes = { "primary", "dialog-action" } };
        var normal = new Button { Content = "普通按钮" }; ToolTip.SetTip(normal, "按钮提示");
        left.Children.Add(Row(primary, normal, new Button { Content = "禁用", IsEnabled = false }));
        left.Children.Add(new TextBox { Text = "中文文件名与 English 123", Watermark = "输入文字" });
        left.Children.Add(new TextBox { Text = "禁用输入", IsEnabled = false });
        left.Children.Add(Row(new NumericUpDown { Value = 1280, Minimum = 1, Maximum = 4096, Width = 140 },
            new ComboBox { ItemsSource = new[] { "MP4", "MKV", "WebM" }, SelectedIndex = 0, Width = 160 }));
        left.Children.Add(Label("选择与进度"));
        left.Children.Add(Row(new CheckBox { Content = "已勾选", IsChecked = true }, new CheckBox { Content = "未勾选" }, new CheckBox { Content = "混合", IsThreeState = true, IsChecked = null }));
        left.Children.Add(Row(new RadioButton { Content = "自动", GroupName = "mode", IsChecked = true }, new RadioButton { Content = "手动", GroupName = "mode" }, new ToggleButton { Content = "切换按钮", IsChecked = true }));
        left.Children.Add(new Slider { Minimum = 0, Maximum = 100, Value = 42 });
        left.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = 62 });
        left.Children.Add(new ProgressBar { IsIndeterminate = true });
        left.Children.Add(Row(new FeatureIcon { Kind = Catalog.Find("mp4").Icon, Width = 48, Height = 48 }, new FeatureIcon { Kind = Catalog.Find("audio-mp3").Icon, Width = 48, Height = 48 },
            new FeatureIcon { Kind = Catalog.Find("image-webp").Icon, Width = 48, Height = 48 }, new FeatureIcon { Kind = Catalog.Find("zip").Icon, Width = 48, Height = 48 }));
        columns.Children.Add(left);
        var right = new StackPanel { Spacing = 14 };
        right.Children.Add(Label("列表、标签与滚动"));
        var list = new ListBox { ItemsSource = Enumerable.Range(1, 18).Select(i => $"{i:00} · 媒体文件与选中状态"), SelectedIndex = 1, Height = 180 };
        list.ContextMenu = new ContextMenu { Items = { new MenuItem { Header = "打开" }, new Separator(), new MenuItem { Header = "属性" } } };
        right.Children.Add(list);
        right.Children.Add(new TabControl { Items = { new TabItem { Header = "视频", Content = new TextBlock { Text = "格式 · 分辨率 · 画质", Margin = new(8) } }, new TabItem { Header = "音频", Content = new TextBlock { Text = "采样率 · 声道", Margin = new(8) } } } });
        right.Children.Add(new Expander { Header = "展开选项", IsExpanded = true, Content = new TextBox { Text = "保留已填写参数", Margin = new(8) } });
        right.Children.Add(new TreeView { Items = { new TreeViewItem { Header = "输入文件", IsExpanded = true, Items = { new TreeViewItem { Header = "航天员.mp4" }, new TreeViewItem { Header = "示例音轨.wav" } } } }, Height = 105 });
        Grid.SetColumn(right, 1); columns.Children.Add(right);
        var scroll = new ScrollViewer { Content = columns, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); view.Children.Add(scroll);
        var footer = new TextBlock { Text = "本地组件预览 · 未执行转换或下载", Classes = { "caption" } }; Grid.SetRow(footer, 3); view.Children.Add(footer);
        window.Content = view;
        if (_tracePointer)
        {
            void Trace(string kind, PointerEventArgs args) => File.AppendAllText(Path.Combine(_output, "pointer.log"),
                $"{kind} point={args.GetPosition(window)} screen={window.PointToScreen(args.GetPosition(window))} size={window.ClientSize} position={window.Position} left={args.GetCurrentPoint(window).Properties.IsLeftButtonPressed} source={args.Source?.GetType().Name} captured={args.Pointer.Captured?.GetType().Name} handled={args.Handled} canResize={window.CanResize}\n");
            window.AddHandler(InputElement.PointerPressedEvent, (_, args) => Trace("pressed", args), RoutingStrategies.Tunnel, true);
            window.AddHandler(InputElement.PointerMovedEvent, (_, args) => { if (args.Pointer.Captured is not null) Trace("moved", args); }, RoutingStrategies.Tunnel, true);
            window.AddHandler(InputElement.PointerReleasedEvent, (_, args) => Trace("released", args), RoutingStrategies.Tunnel, true);
            window.Deactivated += (_, _) => File.AppendAllText(Path.Combine(_output, "pointer.log"), "deactivated\n");
            window.AddHandler(InputElement.PointerCaptureLostEvent, (_, args) => File.AppendAllText(Path.Combine(_output, "pointer.log"),
                $"capture-lost captured={args.Pointer.Captured?.GetType().Name}\n"));
        }
        return window;
    }
}
