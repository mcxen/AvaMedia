using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class VideoSummaryResultWindow
{
    private Control CreateLayout()
    {
        var root = new Grid { ColumnDefinitions = new("224,*"), RowDefinitions = new("*,Auto") };
        var sidebar = new StackPanel { Spacing = 16, Margin = new(16, 20) };
        var cover = new Grid { Height = 108, ClipToBounds = true };
        var fallback = new FeatureIcon { Kind = "document", Width = 48, Height = 48 };
        cover.Children.Add(fallback); _cover.Stretch = Stretch.UniformToFill; cover.Children.Add(_cover);
        sidebar.Children.Add(new Border { Child = cover, Classes = { "summary-inset" }, Padding = new(0) });
        _fileName.TextWrapping = TextWrapping.Wrap; sidebar.Children.Add(_fileName); sidebar.Children.Add(_metadata);
        _play = new Button { Content = "播放原视频", HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
        _play.Click += async (_, _) => await PlayAsync(0); sidebar.Children.Add(_play);
        sidebar.Children.Add(new Border { Classes = { "summary-rule" }, Margin = new(0, 4) });
        sidebar.Children.Add(_navigation);
        root.Children.Add(new Border { Child = new ScrollViewer { Content = sidebar, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, Classes = { "summary-sidebar" } });

        var reader = new Grid { RowDefinitions = new("Auto,*") };
        var toolbar = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(24, 16), ColumnSpacing = 12 };
        toolbar.Children.Add(_pageTitle);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _copy = new Button { Content = "复制当前页", Classes = { "tool" }, IsEnabled = false };
        _copy.Click += async (_, _) => await CopyAsync(); actions.Children.Add(_copy);
        _export = new Button { Content = "导出…", IsEnabled = false };
        _export.Click += (_, _) =>
        {
            if (_report is null || _navigation.SelectedIndex < 0) return;
            var page = _pages[_navigation.SelectedIndex];
            var current = new MenuItem { Header = "当前页面…" };
            current.Click += async (_, _) => await ExportAsync(page.Text, page.Extension, "notes");
            var full = new MenuItem { Header = "完整总结 (.md)…" };
            full.Click += async (_, _) => await ExportAsync(VideoSummaryService.Markdown(_report, includeImages: false), "md", "summary");
            var subtitles = new MenuItem { Header = "字幕 (.srt)…", IsEnabled = _report.Transcript.Count > 0 };
            subtitles.Click += async (_, _) => await ExportAsync(SpeechSubtitles.Srt(_report.Transcript), "srt", "subtitles");
            _export.ContextMenu = new ContextMenu { ItemsSource = new object[] { current, full, subtitles } }; _export.ContextMenu.Open(_export);
        };
        actions.Children.Add(_export); Grid.SetColumn(actions, 1); toolbar.Children.Add(actions);
        reader.Children.Add(new Border { Child = toolbar, Classes = { "summary-toolbar" } });
        Grid.SetRow(_body, 1); reader.Children.Add(_body); Grid.SetColumn(reader, 1); root.Children.Add(reader);

        var footer = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(16, 8), ColumnSpacing = 12 };
        _notice.TextTrimming = TextTrimming.CharacterEllipsis; _notice.TextWrapping = TextWrapping.NoWrap;
        footer.Children.Add(_notice);
        var bottomActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var folder = new Button { Content = "打开输出目录", Classes = { "tool" } };
        folder.Click += (_, _) => { try { PlatformServices.OpenFolder(_folder); } catch (Exception error) { ShowNotice(error.Message, true); } };
        bottomActions.Children.Add(folder); bottomActions.Children.Add(Ui.DialogButton("关闭", Close));
        Grid.SetColumn(bottomActions, 1); footer.Children.Add(bottomActions);
        Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 2); root.Children.Add(footer); return root;
    }

    private static ScrollViewer Reader(Control content) => new()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        Content = new Border { Child = content, MaxWidth = 840, Margin = new(24), HorizontalAlignment = HorizontalAlignment.Stretch }
    };

    private static Border Card(string title, Control content)
    {
        var body = new StackPanel { Spacing = 12 };
        if (title.Length > 0) body.Children.Add(Ui.Text(title, "title")); body.Children.Add(content);
        return new Border { Child = body, Classes = { "summary-card" } };
    }

    private static Control Empty(string title, string detail)
    {
        var body = new StackPanel { Spacing = 12 }; body.Children.Add(Ui.Text(title, "title")); body.Children.Add(Ui.Text(detail, "caption"));
        return Reader(body);
    }

    private Button TimeButton(double seconds)
    {
        var button = new Button { Content = EditorTime.Format(seconds), Classes = { "summary-time", "tool" },
            HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = _engine is not null && File.Exists(_source)
                && double.IsFinite(seconds) && seconds >= 0 && seconds <= (_report?.Duration ?? 0) };
        Localization.SetIsUserText(button, true);
        button.Click += async (_, _) => await PlayAsync(seconds); return button;
    }
}
