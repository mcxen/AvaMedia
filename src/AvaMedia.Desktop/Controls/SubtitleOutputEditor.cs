using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using AvaMedia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

/// <summary>Shared output choice and style applicability for review and automatic subtitle tasks.</summary>
public sealed class SubtitleOutputEditor : UserControl, IDisposable
{
    public sealed record Preference(bool BurnVideo = true, string Format = "srt");
    private readonly Storage _storage = new();
    private readonly SegmentedControl _mode = new() { Name = "SubtitleOutputMode", Items = { "烧录到视频", "导出字幕文件" } };
    private readonly ComboBox _format = Ui.ComponentCombo(["SRT", "ASS", "VTT"], "SRT");
    private readonly StackPanel _fileOptions = new(), _collapsedStyle = new();
    private readonly TextBlock _styleReason = Ui.ComponentText("", "caption");
    private readonly TextBlock _videoReason = Ui.ComponentText("音频源只能导出字幕文件，烧录到视频需要视频画面。", "caption");
    private readonly Button _switchToVideo;
    private readonly SubtitleStyleEditor _style;
    private bool _hasVideo = true;

    public SubtitleOutputEditor(ConversionOptions options, Preference? initial = null)
    {
        var preference = initial ?? _storage.LoadToolOptions<Preference>("subtitle-output") ?? new();
        _mode.SelectedIndex = preference.BurnVideo ? 0 : 1;
        _format.Name = "SubtitleOutputFormat";
        _format.SelectedIndex = preference.Format switch { "ass" => 1, "vtt" => 2, _ => 0 };
        AutomationProperties.SetName(_mode, "输出方式");
        AutomationProperties.SetName(_format, "字幕文件格式");
        _style = new(options) { Compact = true };
        var root = new Grid { RowDefinitions = new("Auto,*") };
        root.Bind(Grid.RowSpacingProperty, new DynamicResourceExtension("AcGap"));
        var output = new StackPanel(); Gap(output);
        output.Children.Add(Ui.ComponentText("输出方式")); output.Children.Add(_mode);
        _videoReason.IsVisible = false; output.Children.Add(_videoReason);
        Gap(_fileOptions);
        _fileOptions.Children.Add(_format);
        _fileOptions.Children.Add(Ui.ComponentText("SRT/VTT 只含文字和时间，字体与位置由播放器决定；ASS 会保留样式。", "caption"));
        output.Children.Add(_fileOptions); root.Children.Add(output);
        var styles = new StackPanel(); Gap(styles);
        styles.Children.Add(Ui.ComponentText("字幕样式"));
        Gap(_collapsedStyle); _collapsedStyle.Children.Add(_styleReason);
        _switchToVideo = Ui.Button("改为烧录到视频", () => _mode.SelectedIndex = 0);
        _switchToVideo.Classes.Add("link"); _switchToVideo.HorizontalAlignment = HorizontalAlignment.Left;
        _collapsedStyle.Children.Add(_switchToVideo); styles.Children.Add(_collapsedStyle); styles.Children.Add(_style);
        var scroll = new ScrollViewer { Content = styles, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); root.Children.Add(scroll); Content = root;
        _mode.ContainerPrepared += (_, e) => { if (e.Index == 0) e.Container.IsEnabled = _hasVideo; };
        _mode.SelectionChanged += (_, _) => SelectionChanged();
        _format.SelectionChanged += (_, _) => SelectionChanged();
        Refresh();
    }

    public event Action? Changed;
    public bool BurnVideo => _mode.SelectedIndex == 0;
    public string FileFormat => _format.SelectedIndex switch { 1 => "ass", 2 => "vtt", _ => "srt" };
    public string ActionText => BurnVideo ? "烧录并导出视频" : "导出字幕文件";
    public SubtitleOutputChoice Choice => new(BurnVideo ? "hard" : "file", FileFormat);

    public void SetVideoAvailable(bool available)
    {
        _hasVideo = available;
        if (_mode.ContainerFromIndex(0) is { } item) item.IsEnabled = available;
        // A source constraint must not overwrite the user's remembered choice.
        if (!available && BurnVideo) _mode.SelectedIndex = 1;
        Refresh();
    }

    public void ReadInto(ConversionOptions options)
    {
        options.Format = BurnVideo ? "mp4" : FileFormat;
        if (BurnVideo || FileFormat == "ass") _style.ReadInto(options);
    }

    private void SelectionChanged()
    {
        Refresh();
        if (_hasVideo) _storage.SaveToolOptions("subtitle-output", new Preference(BurnVideo, FileFormat));
        else if (!BurnVideo)
        {
            var saved = _storage.LoadToolOptions<Preference>("subtitle-output") ?? new();
            _storage.SaveToolOptions("subtitle-output", saved with { Format = FileFormat });
        }
        Changed?.Invoke();
    }

    private void Refresh()
    {
        _fileOptions.IsVisible = !BurnVideo;
        _style.IsVisible = BurnVideo || FileFormat == "ass";
        _collapsedStyle.IsVisible = !_style.IsVisible;
        _styleReason.Text = Localization.Text(FileFormat == "vtt"
            ? "VTT 不包含字体和位置，这些设置只在烧录到视频或导出 ASS 时生效"
            : "SRT 不包含字体和位置，这些设置只在烧录到视频或导出 ASS 时生效");
        _videoReason.IsVisible = !_hasVideo;
        _switchToVideo.IsVisible = _hasVideo;
    }

    private static void Gap(StackPanel panel) => panel.Bind(StackPanel.SpacingProperty, new DynamicResourceExtension("AcGap"));
    public void Dispose() => _style.Dispose();
}
