using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using static AvaMedia.Controls.ComponentResources;

namespace AvaMedia.Controls;

/// <summary>
/// Multi-stage task window layout (UISPEC 10.16, variant B): header (source, status, overall percent, clock, lifecycle actions),
/// vertical stage list, error banner, 结果 / 处理日志 sections with a tool content slot, and an output footer.
/// The host owns the task, its commands and all text; the shell never creates data.
/// </summary>
public sealed class MultiStageTaskShell : Grid
{
    private readonly TextBlock _source = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
    private readonly TextBlock _detail = new() { TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Margin = new(0, 3, 0, 0) };
    private readonly StatusPill _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _percent = new() { FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _clockPrimary = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _clockSecondary = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Border _banner = new();
    private readonly TextBlock _bannerText = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
    private readonly SegmentedControl _sections = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBoxItem _resultTab = new(), _logTab = new();
    private readonly TextBlock _sectionNote = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ContentControl _result = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly ContentControl _log = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, IsVisible = false };
    private readonly ContentControl _output = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ContentControl _sourceLeading = new() { VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private bool _syncing;

    public Control Header { get; }
    public Control ProcessingContent { get; }
    public Control FooterContent { get; }
    public StageList Stages { get; } = new();
    /// <summary>Source block (thumbnail, title, detail); hosts make it a draggable file chip.</summary>
    public Border SourceArea { get; } = new() { Classes = { "ac-ms-source" }, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
    /// <summary>Lifecycle actions (pause / continue / stop / retry / rerun); at most one primary button per window.</summary>
    public StackPanel HeaderActions { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 0, 0) };
    /// <summary>Output and result actions (show in folder / adjust / close / open).</summary>
    public StackPanel FooterActions { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    public event EventHandler? SectionChanged;

    public MultiStageTaskShell()
    {
        Classes.Add("ac-ms-shell");
        RowDefinitions = new("Auto,*,Auto");
        Header = BuildHeader(); Children.Add(Header);
        var body = new Grid { ColumnDefinitions = new("Auto,*") };
        BindResource(body, MarginProperty, "AcMsBodyMargin"); BindResource(body, ColumnSpacingProperty, "AcMsGap");
        BindResource(Stages, WidthProperty, "AcMsStageListWidth");
        body.Children.Add(Stages);
        var right = new Grid { RowDefinitions = new("Auto,Auto,*") };
        BindResource(right, RowSpacingProperty, "AcMsGap");
        Grid.SetColumn(right, 1); body.Children.Add(right);
        right.Children.Add(BuildBanner());
        var toolbar = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, MinHeight = 28 };
        _resultTab.Content = "结果"; _logTab.Content = "处理日志";
        _sections.Items.Add(_resultTab); _sections.Items.Add(_logTab); _sections.SelectedIndex = 0;
        AutomationProperties.SetName(_sections, "结果与处理日志");
        _sections.SelectionChanged += (_, _) =>
        {
            if (_syncing || _sections.SelectedIndex < 0) return;
            ApplySection(); SectionChanged?.Invoke(this, EventArgs.Empty);
        };
        toolbar.Children.Add(_sections);
        BindResource(_sectionNote, TextBlock.FontSizeProperty, "AcCaptionFont"); BindResource(_sectionNote, TextBlock.ForegroundProperty, "UiTextSecondary");
        Grid.SetColumn(_sectionNote, 1); toolbar.Children.Add(_sectionNote);
        Grid.SetRow(toolbar, 1); right.Children.Add(toolbar);
        var slot = new Panel(); slot.Children.Add(_result); slot.Children.Add(_log);
        Grid.SetRow(slot, 2); right.Children.Add(slot);
        ProcessingContent = body; Grid.SetRow(body, 1); Children.Add(body);
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        BindResource(footer, HeightProperty, "AcMsFooterHeight"); BindResource(footer, MarginProperty, "AcMsPadding");
        footer.Children.Add(_output); Grid.SetColumn(FooterActions, 1); footer.Children.Add(FooterActions);
        FooterContent = footer; Grid.SetRow(footer, 2); Children.Add(footer);
    }

    private Control BuildHeader()
    {
        var header = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 14 };
        BindResource(header, HeightProperty, "AcMsHeaderHeight"); BindResource(header, MarginProperty, "AcMsPadding");
        var source = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        BindResource(_source, TextBlock.FontSizeProperty, "AcMsTitleFont"); BindResource(_source, TextBlock.ForegroundProperty, "UiText");
        BindResource(_detail, TextBlock.FontSizeProperty, "AcCaptionFont"); BindResource(_detail, TextBlock.ForegroundProperty, "UiTextSecondary");
        source.Children.Add(_source); source.Children.Add(_detail);
        var chip = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        chip.Children.Add(_sourceLeading); Grid.SetColumn(source, 1); chip.Children.Add(source);
        SourceArea.Child = chip; header.Children.Add(SourceArea);
        var overall = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        overall.Children.Add(_status);
        BindResource(_percent, TextBlock.FontSizeProperty, "AcMsPercentFont"); BindResource(_percent, TextBlock.ForegroundProperty, "UiText");
        BindResource(_percent, MinWidthProperty, "AcMsPercentWidth"); _percent.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
        overall.Children.Add(_percent);
        var clock = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var line in new[] { _clockPrimary, _clockSecondary })
        {
            BindResource(line, TextBlock.FontSizeProperty, "AcSubStepFont"); BindResource(line, TextBlock.ForegroundProperty, "UiTextSecondary");
            line.LineHeight = 15; line.FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") };
            clock.Children.Add(line);
        }
        overall.Children.Add(clock);
        Grid.SetColumn(overall, 1); header.Children.Add(overall);
        Grid.SetColumn(HeaderActions, 2); header.Children.Add(HeaderActions);
        return header;
    }

    private Control BuildBanner()
    {
        BindResource(_banner, Border.BackgroundProperty, "AcDangerSoft"); BindResource(_banner, Border.CornerRadiusProperty, "AcMsBannerRadius");
        BindResource(_banner, Border.PaddingProperty, "AcMsBannerPadding");
        BindResource(_bannerText, TextBlock.FontSizeProperty, "AcCaptionFont"); BindResource(_bannerText, TextBlock.ForegroundProperty, "UiDanger");
        var icon = new StageGlyph { State = "failed", Width = 14, Height = 14, Margin = new(0, 2, 0, 0) };
        var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        row.Children.Add(icon); Grid.SetColumn(_bannerText, 1); row.Children.Add(_bannerText);
        _banner.Child = row; _banner.IsVisible = false;
        AutomationProperties.SetLiveSetting(_banner, AutomationLiveSetting.Assertive);
        return _banner;
    }

    public string SourceTitle { get => _source.Text ?? ""; set => _source.Text = value; }
    public string SourceDetail { get => _detail.Text ?? ""; set { _detail.Text = value; _detail.IsVisible = value.Length > 0; } }
    public TextBlock SourceTitleBlock => _source;
    public TextBlock SourceDetailBlock => _detail;
    public void SetStatus(string state, string text) { _status.State = state; _status.Text = text; }
    public string ProgressText { get => _percent.Text ?? ""; set { _percent.Text = value; _percent.IsVisible = value.Length > 0; } }

    /// <summary>Two-line clock; <paramref name="value"/> is emphasized after <paramref name="label"/>.</summary>
    public void SetClock(string label, string value, string secondary)
    {
        _clockPrimary.Inlines ??= [];
        _clockPrimary.Inlines.Clear();
        _clockPrimary.Inlines.Add(new Run(label));
        if (value.Length > 0)
        {
            var emphasis = new Run(" " + value) { FontWeight = FontWeight.Medium };
            emphasis.Bind(TextElement.ForegroundProperty, Resource("UiText"));
            _clockPrimary.Inlines.Add(emphasis);
        }
        _clockSecondary.Text = secondary; _clockSecondary.IsVisible = secondary.Length > 0;
    }

    /// <summary>Error banner: bold <paramref name="title"/>, red <paramref name="reason"/>, then a neutral recovery line.</summary>
    public void SetError(string title, string reason, string recovery)
    {
        _banner.IsVisible = title.Length + reason.Length > 0;
        _bannerText.Inlines ??= [];
        _bannerText.Inlines.Clear();
        if (!_banner.IsVisible) return;
        if (title.Length > 0) _bannerText.Inlines.Add(new Run(title) { FontWeight = FontWeight.SemiBold });
        if (reason.Length > 0) _bannerText.Inlines.Add(new Run(reason));
        if (recovery.Length > 0)
        {
            _bannerText.Inlines.Add(new LineBreak());
            var note = new Run(recovery);
            note.Bind(TextElement.ForegroundProperty, Resource("UiText"));
            _bannerText.Inlines.Add(note);
        }
    }

    public string SectionNote { get => _sectionNote.Text ?? ""; set { _sectionNote.Text = value; ToolTip.SetTip(_sectionNote, value.Length > 0 ? value : null); } }
    public bool IsLogEnabled { get => _logTab.IsEnabled; set => _logTab.IsEnabled = value; }
    public bool ShowLog
    {
        get => _sections.SelectedIndex == 1;
        set
        {
            if (ShowLog == value) return;
            _syncing = true;
            try { _sections.SelectedIndex = value ? 1 : 0; } finally { _syncing = false; }
            ApplySection();
        }
    }
    private void ApplySection() { _result.IsVisible = !ShowLog; _log.IsVisible = ShowLog; }

    public Control? ResultContent { get => _result.Content as Control; set => _result.Content = value; }
    public Control? LogContent { get => _log.Content as Control; set => _log.Content = value; }
    public Control? OutputContent { get => _output.Content as Control; set => _output.Content = value; }
    /// <summary>Leading visual of the source block (the input thumbnail).</summary>
    public Control? SourceLeading { get => _sourceLeading.Content as Control; set { _sourceLeading.Content = value; _sourceLeading.IsVisible = value is not null; } }
    /// <summary>Localized labels for the two sections (hosts pass already translated text).</summary>
    public void SetSectionLabels(string result, string log) { _resultTab.Content = result; _logTab.Content = log; }
}
