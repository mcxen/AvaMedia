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
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto") };

        var header = new Grid { ColumnDefinitions = new("112,*,Auto"), ColumnSpacing = 12, Margin = new(12, 10, 12, 8) };
        var cover = new Grid { Height = 63, ClipToBounds = true };
        cover.Children.Add(new FeatureIcon { Kind = "document", Width = 28, Height = 28 });
        _cover.Stretch = Stretch.UniformToFill; cover.Children.Add(_cover);
        header.Children.Add(new Border { Child = cover, Classes = { "summary-inset" }, Padding = new(0) });
        var identity = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        _fileName.TextWrapping = TextWrapping.NoWrap; _fileName.TextTrimming = TextTrimming.CharacterEllipsis;
        _metadata.TextWrapping = TextWrapping.NoWrap; _metadata.TextTrimming = TextTrimming.CharacterEllipsis;
        identity.Children.Add(_fileName); identity.Children.Add(_metadata);
        Grid.SetColumn(identity, 1); header.Children.Add(identity);
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        _play = new Button { Content = "播放", Classes = { "tool" }, IsEnabled = false };
        _play.Click += async (_, _) => await PlayAsync(0); headerActions.Children.Add(_play);
        Grid.SetColumn(headerActions, 2); header.Children.Add(headerActions);
        root.Children.Add(new Border { Child = header, Classes = { "summary-toolbar" } });

        var navBar = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12, Margin = new(12, 6) };
        _navigation.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() =>
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 });
        ScrollViewer.SetHorizontalScrollBarVisibility(_navigation, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_navigation, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        navBar.Children.Add(_navigation);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        _copy = new Button { Content = "复制", Classes = { "tool" }, IsEnabled = false };
        _copy.Click += async (_, _) => await CopyAsync(); actions.Children.Add(_copy);
        _export = new Button { Content = "导出…", Classes = { "tool" }, IsEnabled = false };
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
        actions.Children.Add(_export); Grid.SetColumn(actions, 1); navBar.Children.Add(actions);
        Grid.SetRow(navBar, 1); root.Children.Add(navBar);

        Grid.SetRow(_body, 2); root.Children.Add(_body);

        var footer = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(12, 6), ColumnSpacing = 12 };
        _notice.TextTrimming = TextTrimming.CharacterEllipsis; _notice.TextWrapping = TextWrapping.NoWrap;
        footer.Children.Add(_notice);
        var bottomActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var folder = new Button { Content = "输出目录", Classes = { "tool" } };
        folder.Click += (_, _) => { try { PlatformServices.OpenFolder(_folder); } catch (Exception error) { ShowNotice(error.Message, true); } };
        bottomActions.Children.Add(folder); bottomActions.Children.Add(Ui.DialogButton("关闭", Close));
        Grid.SetColumn(bottomActions, 1); footer.Children.Add(bottomActions);
        Grid.SetRow(footer, 3); root.Children.Add(footer); return root;
    }

    private static ScrollViewer Reader(Control content) => new()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        Content = new Border { Child = content, Margin = new(12, 4, 12, 12), HorizontalAlignment = HorizontalAlignment.Stretch }
    };

    private static Control Split(Control primary, Control secondary)
    {
        var grid = new Grid { ColumnDefinitions = new("*,12,*"), Margin = new(12, 4, 12, 12) };
        grid.Children.Add(Scroll(primary));
        var right = Scroll(secondary); Grid.SetColumn(right, 2); grid.Children.Add(right);
        return grid;
    }

    private static ScrollViewer Scroll(Control content) => new()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        Content = content
    };

    private static Border Card(string title, Control content)
    {
        var body = new StackPanel { Spacing = 6 };
        if (title.Length > 0) body.Children.Add(Ui.Text(title, "summary-section")); body.Children.Add(content);
        return new Border { Child = body, Classes = { "summary-card" } };
    }

    private static Control Empty(string title, string detail)
    {
        var body = new StackPanel { Spacing = 6 }; body.Children.Add(Ui.Text(title, "title")); body.Children.Add(Ui.Text(detail, "caption"));
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
