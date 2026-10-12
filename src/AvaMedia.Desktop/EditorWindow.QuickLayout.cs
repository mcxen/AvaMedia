using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using AvaMedia.Desktop.Controls;
using AvaMedia.MediaUi;

namespace AvaMedia.Desktop;

/// <summary>
/// Quick-clip layout from editor/clip-editor-demo: title bar with mode switch, preview card, transport row,
/// filmstrip timeline card, segment sidebar (list, boundary frame cards, property list) and the export bar.
/// The existing named controls are moved into QuickRoot slots so every handler, shortcut and test hook keeps working;
/// the classic EditorLayout stays in the window (hidden) and still hosts the controls that are not part of the design.
/// Shortcuts are only shown in tooltips (UISPEC: no visible keycaps).
/// </summary>
public partial class EditorWindow
{
    private string _workflowStep = "";
    private Button? _nextSegmentButton;

    private static void Move(Control control, Control target, int index = -1)
    {
        switch (control.Parent)
        {
            case Panel panel: panel.Children.Remove(control); break;
            case Decorator decorator: decorator.Child = null; break;
            case ContentControl content: content.Content = null; break;
            case Avalonia.Controls.Presenters.ContentPresenter presenter: presenter.Content = null; break;
        }
        switch (target)
        {
            case Panel panel: if (index >= 0) panel.Children.Insert(Math.Min(index, panel.Children.Count), control); else panel.Children.Add(control); break;
            case Decorator decorator: decorator.Child = control; break;
            case ContentControl content: content.Content = control; break;
        }
    }

    private static void Restyle(StyledElement control, params string[] classes)
    {
        foreach (var name in new[] { "transport", "field-action", "primary", "dialog-action", "step", "time", "caption" }) control.Classes.Remove(name);
        foreach (var name in classes) control.Classes.Add(name);
        control.Classes.Add("ac");
        if (control is Button)
        {
            foreach (var role in new[] { "icon", "borderless", "large", "shell", "primary" }) control.Classes.Remove(role);
            if (classes.Any(role => role is "ib" or "nudge")) { control.Classes.Add("icon"); control.Classes.Add("borderless"); }
            if (classes.Contains("nudge")) { control.Classes.Add("small"); control.Bind(WidthProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AcSmallHeight")); }
            if (classes.Contains("pill")) control.Classes.Add("shell");
            if (classes.Contains("primary-pill")) { control.Classes.Add("primary"); control.Classes.Add("large"); }
            if (classes.Contains("link")) control.Classes.Add("borderless");
        }
    }

    private static Control QuickIcon(string kind, double size = 15) => new LucideIcon { Kind = kind, Width = size, Height = size };

    private void BuildQuickLayout()
    {
        EditorLayout.IsVisible = false; QuickRoot.IsVisible = true;
        Width = 1280; Height = 800; MinWidth = 1100; MinHeight = 700;

        // Title bar.
        Move(ModeBar, QuickModeSlot);
        // Keep the classic tab and its named controls, but omit it from the quick mode switch.
        ((TabItem)EditTabs.Items[2]!).IsVisible = false;
        var icons = new Dictionary<string, string> { ["剪辑"] = "scissors", ["画面裁剪"] = "crop", ["自动转正"] = "rotate-cw" };
        foreach (var button in ModeButtons.Items.OfType<ListBoxItem>())
            if (button.Tag is TabItem { Header: string header })
            {
                button.ClearValue(ContentControl.ContentProperty);
                button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { QuickIcon(icons.GetValueOrDefault(header, "scissors"), 13), new TextBlock { Text = Localization.Text(header), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center } } };
            }
        Move(UndoSegmentButton, QuickHistorySlot); Move(RedoSegmentButton, QuickHistorySlot);
        Restyle(UndoSegmentButton, "ib"); UndoSegmentButton.Content = QuickIcon("undo-2");
        Restyle(RedoSegmentButton, "ib"); RedoSegmentButton.Content = QuickIcon("redo-2");
        ToolTip.SetTip(UndoSegmentButton, Localization.Text("撤销") + " (Ctrl Z)"); ToolTip.SetTip(RedoSegmentButton, Localization.Text("重做") + " (Ctrl Y)");
        Avalonia.Automation.AutomationProperties.SetName(UndoSegmentButton, "撤销"); Avalonia.Automation.AutomationProperties.SetName(RedoSegmentButton, "重做");
        WindowChrome.HookDrag(this, QuickTitleBar, () => ModernFrame);

        // Preview card and transport.
        Move(PreviewArea, QuickPreviewSlot); PreviewArea.Background = null; PreviewArea.Margin = new Thickness(40, 48);
        Move(BackwardButton, QuickTransportSlot); Restyle(BackwardButton, "ib"); BackwardButton.Content = QuickIcon("chevron-left", 17);
        Move(PlayButton, QuickTransportSlot); Restyle(PlayButton, "ib", "play");
        Move(ForwardButton, QuickTransportSlot); Restyle(ForwardButton, "ib"); ForwardButton.Content = QuickIcon("chevron-right", 17);
        _nextSegmentButton = new Button { Classes = { "ac", "ib" }, Content = QuickIcon("skip-forward") };
        _nextSegmentButton.Click += NextSegmentClick;
        ToolTip.SetTip(_nextSegmentButton, Localization.Text("下一个片段")); Avalonia.Automation.AutomationProperties.SetName(_nextSegmentButton, Localization.Text("下一个片段"));
        QuickTransportSlot.Children.Add(_nextSegmentButton);
        Move(StopButton, QuickTransportSlot); Restyle(StopButton, "ib"); StopButton.Content = QuickIcon("square", 14);
        ToolTip.SetTip(BackwardButton, Localization.Text("后退") + " (←)"); ToolTip.SetTip(ForwardButton, Localization.Text("前进") + " (→)"); ToolTip.SetTip(PlayButton, Localization.Text("播放") + " (" + Localization.Text("空格") + ")");
        Move(PrecisionCombo, QuickPrecisionSlot); Restyle(PrecisionCombo, "flat");
        Move(SoundButton, QuickTransportRight); Restyle(SoundButton, "ib");

        // Timeline card.
        Move(Filmstrip, QuickFilmstripSlot);
        Move(TimelineZoom, QuickZoomSlot); Restyle(TimelineZoom, "zoom"); TimelineZoom.VerticalAlignment = VerticalAlignment.Center; TimelineZoom.ClearValue(WidthProperty);
        var zoomIn = new Button { Classes = { "ac", "ib" }, Content = QuickIcon("zoom-in") }; zoomIn.Click += ZoomInClick;
        ToolTip.SetTip(zoomIn, Localization.Text("放大时间线")); Avalonia.Automation.AutomationProperties.SetName(zoomIn, Localization.Text("放大时间线"));
        QuickZoomSlot.Children.Add(zoomIn);
        Filmstrip.CalloutBrush = this.FindResource("QcCard") as Avalonia.Media.IBrush ?? Filmstrip.CalloutBrush;
        Filmstrip.Bind(FilmstripTimeline.AccentBrushProperty, this.GetResourceObservable("QcAccent"));
        Filmstrip.Bind(FilmstripTimeline.TrackBrushProperty, this.GetResourceObservable("QcStrip"));
        Filmstrip.Bind(FilmstripTimeline.TextBrushProperty, this.GetResourceObservable("QcSecondary"));
        Filmstrip.Bind(FilmstripTimeline.CalloutBrushProperty, this.GetResourceObservable("QcCard"));
        Filmstrip.Bind(FilmstripTimeline.CalloutTextBrushProperty, this.GetResourceObservable("QcText"));
        Filmstrip.Bind(FilmstripTimeline.PlayheadBrushProperty, this.GetResourceObservable("QcPlayhead"));
        Move(EditTabs, QuickTabsCard);
        BuildQuickCropInspector();
        EditTabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, EditTabs)) SyncQuickMode(); };
        SyncQuickMode();

        // Sidebar.
        Move(SegmentList, QuickSegmentListSlot);
        Move(SegmentError, QuickSegmentErrorSlot);
        BuildBoundaryCard(QuickStartCard, StartImage, "起点", StartMinusButton, StartTime, StartPlusButton, "跳转到起点", JumpStartClick);
        BuildBoundaryCard(QuickEndCard, EndImage, "终点", EndMinusButton, EndTime, EndPlusButton, "跳转到终点", JumpEndClick);
        Move(TimeError, QuickTimeErrorSlot);
        Move(SpeedCombo, QuickSpeedSlot); Restyle(SpeedCombo, "flat");
        Move(FadeInCombo, QuickFadeSlot); Restyle(FadeInCombo, "flat");
        QuickFadeSlot.Children.Add(new TextBlock { Text = "·", Classes = { "value" }, VerticalAlignment = VerticalAlignment.Center });
        Move(FadeOutCombo, QuickFadeSlot); Restyle(FadeOutCombo, "flat");
        Move(DirectionCombo, QuickDirectionSlot); Restyle(DirectionCombo, "flat"); DirectionCombo.Width = double.NaN;
        foreach (var combo in new[] { SpeedCombo, FadeInCombo, FadeOutCombo, DirectionCombo, PrecisionCombo }) { combo.HorizontalAlignment = HorizontalAlignment.Right; combo.MinWidth = 0; }
        Move(PlaySelectionButton, QuickPlaySlot); Restyle(PlaySelectionButton, "pill");
        Move(PlayAllSegmentsButton, QuickPlaySlot); Restyle(PlayAllSegmentsButton, "pill");
        Move(InvertSelectionCheck, QuickInvertSlot);
        BuildQuickModePages();

        // Export bar: 输出配置… is in XAML; the primary action is the existing ConfirmButton.
        Move(ConfirmButton, QuickActionSlot); Restyle(ConfirmButton, "primary-pill"); ConfirmButton.MinWidth = 0;
        foreach (var control in new Control[] { ConfirmButton, PrecisionCombo, SpeedCombo, FadeInCombo, FadeOutCombo, DirectionCombo }) { control.ClearValue(HeightProperty); control.ClearValue(MinHeightProperty); }
        foreach (var error in new[] { SegmentError, TimeError })
        {
            error.Margin = new Thickness(2, 6, 2, 0); error.IsVisible = !string.IsNullOrEmpty(error.Text);
            error.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) error.IsVisible = !string.IsNullOrEmpty(error.Text); };
        }
        Filmstrip.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) ApplyTimelineZoom(); };
    }

    private void BuildBoundaryCard(StackPanel card, Image image, string tag, Button minus, TextBox time, Button plus, string link, EventHandler<RoutedEventArgs> jump)
    {
        var frame = new Button { Classes = { "ac", "frame" } };
        frame.Click += jump;
        ToolTip.SetTip(frame, Localization.Text(link));
        Avalonia.Automation.AutomationProperties.SetName(frame, Localization.Text(link));
        var panel = new Panel();
        Move(image, panel); image.Stretch = Avalonia.Media.Stretch.UniformToFill;
        panel.Children.Add(new Border { Classes = { "tag" }, Child = new TextBlock { Text = Localization.Text(tag) }, IsHitTestVisible = false });
        frame.Content = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = panel };
        card.Children.Add(frame);
        var row = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 3, Margin = new Thickness(0, 5, 0, 0) };
        Move(minus, row); Restyle(minus, "nudge"); minus.Content = QuickIcon("chevron-left", 12);
        Move(time, row); Restyle(time, "qc-time");
        time.Bind(TextBox.FontSizeProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AcSmallFont"));
        time.Bind(TextBox.FontFamilyProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("UiNumericFont"));
        time.Bind(TextBox.PaddingProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AcNoBorder"));
        Grid.SetColumn(time, 1);
        Move(plus, row); Restyle(plus, "nudge"); plus.Content = QuickIcon("chevron-right", 12); Grid.SetColumn(plus, 2);
        card.Children.Add(row);
        var jumpLink = new Button { Classes = { "ac", "link" }, Content = Localization.Text(link), Margin = new Thickness(0, 3, 0, 0) };
        jumpLink.Click += jump;
        card.Children.Add(jumpLink);
    }

    /// <summary>Design-review captures position the playhead like a user would.</summary>
    public void SeekForReview(double seconds) => Seek(seconds);

    private void SyncQuickMode()
    {
        var crop = EditTabs.SelectedIndex == 1;
        var direction = ReferenceEquals(EditTabs.SelectedItem, DirectionTab);
        QuickTimelineCard.IsVisible = true;
        QuickTabsCard.IsVisible = false;
        QuickCropInspector.IsVisible = crop;
        QuickClipInspector.IsVisible = !crop;
        QuickDirectionCard.IsVisible = direction;
        QuickOptionsCard.IsVisible = false;
    }

    private void QuickCropRowClick(object? sender, RoutedEventArgs e)
    {
        var crop = EditTabs.Items.OfType<TabItem>().ElementAtOrDefault(1);
        if (crop is { IsEnabled: true }) EditTabs.SelectedItem = crop;
    }

    private void PreviousSegmentClick(object? sender, RoutedEventArgs e) => JumpSegment(-1);
    private void NextSegmentClick(object? sender, RoutedEventArgs e) => JumpSegment(1);
    private void JumpSegment(int direction)
    {
        if (_activeSegment is null || _segments.Count == 0) return;
        var index = Math.Clamp(_segments.IndexOf(_activeSegment) + direction, 0, _segments.Count - 1);
        _segmentReady = NavigateSegment(index, _segments[index].Options.Start);
    }

    private void RefreshQuickLayout()
    {
        if (!QuickRoot.IsVisible) return;
        QuickSubtitle.Text = _workflowStep.Length > 0 ? _workflowStep : System.IO.Path.GetFileName(_path);
        var segment = _activeSegment;
        var number = segment is null ? 0 : _segments.IndexOf(segment) + 1;
        var title = segment?.Title ?? "";
        QuickSegmentBadge.IsVisible = segment is not null;
        if (segment is not null) QuickSegmentBadgeText.Text = title + " · " + FilmstripTimeline.Long(segment.Options.Start) + " – " + FilmstripTimeline.Long(segment.End);
        QuickFramesHeader.Text = title.Length > 0 ? title + " · " + Localization.Text("起点 / 终点") : Localization.Text("起点 / 终点");
        QuickCropValue.Text = _options.CropWidth > 0 && _options.CropHeight > 0 ? $"{_options.CropWidth}×{_options.CropHeight}" : Localization.Text("无");
        var total = _segments.Sum(s => _removeSelected ? Math.Max(0, s.End - s.Options.Start) : s.OutputDuration);
        QuickTimelineSummary.Text = _removeSelected
            ? Localization.Format($"{_segments.Count} 个去除片段 · 保留 {FilmstripTimeline.Compact(Math.Max(0, (_info?.Duration ?? 0) - total))}")
            : Localization.Format($"{_segments.Count} 个片段 · 总输出时长 {FilmstripTimeline.Compact(total)}");
        var parts = new List<string> { title };
        if (_options.CropWidth > 0 && _options.CropHeight > 0) parts.Add(Localization.Text("裁剪") + $" {_options.CropWidth}×{_options.CropHeight}");
        if (DirectionCombo.SelectedIndex > 0 && DirectionCombo.SelectedItem is string direction) parts.Add(Localization.Text(direction));
        if (!_removeSelected && Math.Abs(_options.Speed - 1) > 1e-9) parts.Add(Core.MediaEngine.Number(_options.Speed) + "×");
        QuickStatus.Text = string.Join(" · ", parts.Where(p => p.Length > 0));
        QuickTotalTime.Text = " / " + FilmstripTimeline.Long(_info?.Duration ?? 0);
        RefreshQuickModePages(number);
    }
}
