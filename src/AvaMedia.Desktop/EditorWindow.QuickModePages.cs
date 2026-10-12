using Avalonia;
using Avalonia.Controls;
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
            var chip = new Button { Content = Localization.Text(label), Classes = { "direction-chip" }, Tag = index };
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
                Classes = { "mono" }, FontSize = 12.5, TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            };
            Localization.SetIsUserText(value, true);
            var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
            row.Children.Add(new TextBlock { Text = Localization.Text(label), Classes = { "sec" }, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(value, 1); row.Children.Add(value);
            var border = new Border { Classes = { "qc-row" }, Height = 32, Child = row };
            if (label == "音频") border.Classes.Add("last");
            QuickMediaRows.Children.Add(border); _quickMediaValues.Add(value);
        }
        Move(PreviewNote, QuickPreviewNoteSlot);
        Restyle(PreviewNote, "sec"); PreviewNote.FontSize = 12; PreviewNote.LineHeight = 18.6;
        Move(AudioStatus, QuickAudioStatusSlot);
        Restyle(AudioStatus); AudioStatus.FontSize = 12; AudioStatus.TextWrapping = TextWrapping.Wrap;
        Move(EditOptionsButton, QuickEditOptionsSlot);
        Restyle(EditOptionsButton, "pill");
    }

    private void RefreshQuickDirectionChips()
    {
        foreach (var chip in QuickDirectionChips.Children.OfType<Button>())
            chip.Classes.Set("selected", chip.Tag is int index && index == DirectionCombo.SelectedIndex);
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
