using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaMedia.Desktop;

public partial class EditorWindow
{
    private readonly List<TextBlock> _quickMediaValues = [];

    private void BuildQuickModePages()
    {
        // The sidebar combo and the original checkbox remain the edit sources, including undo/restore.
        foreach (var label in DirectionCombo.Items.OfType<string>())
        {
            var index = QuickDirectionChips.Children.Count;
            var chip = new Button { Content = Localization.Text(label), Classes = { "ac", "direction-chip" }, Tag = index };
            chip.Bind(IsEnabledProperty, DirectionCombo.GetObservable(IsEnabledProperty));
            chip.Click += (_, _) => DirectionCombo.SelectedIndex = index;
            QuickDirectionChips.Children.Add(chip);
        }
        DirectionCombo.SelectionChanged += (_, _) => RefreshQuickDirectionChips();
        RefreshQuickDirectionChips();
        Move(MirrorCheck, QuickMirrorSlot);
        Restyle(MirrorCheck, "mirror-switch");

        foreach (var button in new[] { DetectDirectionButton, CancelDetectionButton, ApplyDirectionButton })
        {
            Move(button, QuickDirectionActions);
            Restyle(button, "pill");
        }
        DetectDirectionButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { QuickIcon("rotate-cw", 13), new TextBlock { Text = Localization.Text("检测方向"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center } }
        };

        // Keep the complete status on the original named control; only its presentation is split.
        Move(DirectionStatus, QuickDirectionStatusSlot);
        DirectionStatus.IsVisible = false;
        DirectionStatus.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) RefreshQuickDirectionStatus(); };
        RefreshQuickDirectionStatus();

        foreach (var label in new[] { "文件", "时长", "画面", "视频", "音频" })
        {
            var value = new TextBlock
            {
                Classes = { "mono" }, FontFeatures = new FontFeatureCollection { FontFeature.Parse("tnum") }, FontSize = 12.5, TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            };
            Localization.SetIsUserText(value, true);
            var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
            row.Children.Add(new TextBlock { Text = Localization.Text(label), Classes = { "sec" }, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(value, 1); row.Children.Add(value);
            var border = new Border { Classes = { "ac-setting-row", "divider" }, Height = 32, Child = row };
            if (label == "音频") border.Classes.Add("last");
            QuickMediaRows.Children.Add(border); _quickMediaValues.Add(value);
        }
        Move(PreviewNote, QuickPreviewNoteSlot);
        Restyle(PreviewNote, "sec"); PreviewNote.Bind(TextBlock.FontSizeProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AcCaptionFont"));
        Move(AudioStatus, QuickAudioStatusSlot);
        Restyle(AudioStatus); AudioStatus.Bind(TextBlock.FontSizeProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AcBodyFont")); AudioStatus.TextWrapping = TextWrapping.Wrap;
        Move(EditOptionsButton, QuickOutputSlot);
        Restyle(EditOptionsButton, "pill", "tall");
        Localization.SetContent(EditOptionsButton, $"输出配置…");
        Avalonia.Automation.AutomationProperties.SetName(EditOptionsButton, Localization.Text("输出配置"));
    }

    private void QuickInfoPointerEntered(object? sender, PointerEventArgs e) => QuickInfoButton.Flyout?.ShowAt(QuickInfoButton);
    private void QuickInfoClick(object? sender, RoutedEventArgs e) => QuickInfoButton.Flyout?.ShowAt(QuickInfoButton);

    private void BuildQuickCropInspector()
    {
        QuickCropInspector.Children.Add(new TextBlock { Text = Localization.Text("选区比例"), Classes = { "head" } });
        Move(CropRatio, QuickCropInspector);
        CropRatio.HorizontalAlignment = HorizontalAlignment.Stretch;
        QuickCropInspector.Children.Add(new TextBlock { Text = Localization.Text("精确坐标"), Classes = { "head" } });
        var rows = new StackPanel();
        QuickCropInspector.Children.Add(new Border { Classes = { "ac-card" }, Child = rows });
        foreach (var (label, input) in new[] { ("X", CropX), ("Y", CropY), ("宽度", CropWidth), ("高度", CropHeight) })
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto") };
            row.Children.Add(new TextBlock { Text = Localization.Text(label), VerticalAlignment = VerticalAlignment.Center });
            Move(input, row); Grid.SetColumn(input, 1);
            input.Classes.Add("ac");
            input.TextAlignment = TextAlignment.Right;
            rows.Children.Add(new Border { Classes = { "ac-setting-row", "divider" }, Child = row });
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var apply = new Button { Content = Localization.Text("应用坐标"), Classes = { "ac", "pill" } };
        apply.Click += ApplyCropClick; actions.Children.Add(apply);
        var reset = new Button { Content = Localization.Text("重置选区"), Classes = { "ac", "pill" } };
        reset.Click += ResetCropClick; actions.Children.Add(reset);
        QuickCropInspector.Children.Add(actions);
        Move(DelogoMode, QuickCropInspector);
        Move(CropError, QuickCropInspector);
    }

    private void RefreshQuickDirectionChips()
    {
        foreach (var chip in QuickDirectionChips.Children.OfType<Button>())
            chip.Classes.Set("primary", chip.Tag is int index && index == DirectionCombo.SelectedIndex);
    }

    private void RefreshQuickDirectionStatus()
    {
        var lines = (DirectionStatus.Text ?? "").Replace("\r\n", "\n").Split('\n', 2);
        QuickDirectionSummary.Text = lines[0];
        QuickDirectionReason.Text = lines.Length > 1 ? lines[1] : "";
        QuickDirectionReason.IsVisible = !string.IsNullOrEmpty(QuickDirectionReason.Text);
    }

    private void RefreshQuickModePages(int segmentNumber)
    {
        Localization.SetText(QuickDirectionScope, $"只影响片段 {segmentNumber}，输出时生效");
        if (_quickMediaValues.Count != 5) return;
        var values = new[]
        {
            Path.GetFileName(_path),
            _info is null ? "—" : EditorTime.Format(_info.Duration),
            _info?.HasVideo == true ? $"{_info.Width} × {_info.Height}" : "—",
            string.IsNullOrEmpty(_info?.VideoCodec) ? "—" : _info.VideoCodec,
            string.IsNullOrEmpty(_info?.AudioCodec) ? "—" : _info.AudioCodec
        };
        for (var i = 0; i < values.Length; i++) _quickMediaValues[i].Text = values[i];
        ToolTip.SetTip(_quickMediaValues[0], values[0]);
        QuickAudioDot.Classes.Set("ready", _info?.HasAudio == true);
    }
}
