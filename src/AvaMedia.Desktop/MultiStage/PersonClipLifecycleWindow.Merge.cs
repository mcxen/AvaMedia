using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop.MultiStage;

internal sealed partial class PersonClipLifecycleWindow
{
    private const int StripPageSize = 10;
    private readonly StackPanel _mergeBody = new();
    private readonly Grid _strip = new() { ColumnSpacing = 8 };
    private readonly MsStatGrid _summary = new(horizontal: true);
    private readonly MsSettingsGroup _expected = new();
    private readonly TextBlock _stripNote = MsUi.Caption(), _progressText = MsUi.Caption(),
        _folderPath = MsUi.Caption(), _formatDetail = MsUi.Small(), _keptTime = MsUi.Heading(""), _removedTime = MsUi.Caption();
    private readonly ProgressBar _progress = new() { Classes = { "ac" }, Minimum = 0, Maximum = 100 },
        _retention = new() { Classes = { "ac" }, Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
    private readonly SegmentedControl _transition = new() { Items = { "无", "淡入淡出" }, SelectedIndex = 0 };
    private readonly ComboBox _format = new() { Classes = { "ac" }, ItemsSource = QuickClipBatch.Presets };
    private readonly Button _folderButton;
    private readonly List<MsPreviewCard> _thumbs = [];
    private Button? _previous, _next;
    private int _stripPage;
    private string _stripKey = "";

    private Control BuildMerge()
    {
        _mergeBody.Bind(StackPanel.SpacingProperty, new DynamicResourceExtension("AcMsGap"));
        _mergeBody.Children.Add(_summary);
        var stripBody = new StackPanel { Spacing = 10 };
        _previous = StripArrow("上一页", "chevron-left", -1);
        _next = StripArrow("下一页", "chevron-right", 1);
        var paging = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4,
            Children = { _stripNote, _previous, _next } };
        _stripNote.Margin = new(0, 0, 4, 0);
        stripBody.Children.Add(MsUi.HeadRow(MsUi.Heading("按顺序合并"), paging, bottom: 0));
        stripBody.Children.Add(_strip);
        stripBody.Children.Add(MsUi.Columns("Auto,Auto,*,Auto", MsUi.Caption("保留"), _keptTime, _retention, _removedTime));
        _mergeBody.Children.Add(MsUi.Card(stripBody));

        var options = new StackPanel();
        var results = new StackPanel();
        options.Bind(StackPanel.SpacingProperty, new DynamicResourceExtension("AcMsGap"));
        results.Bind(StackPanel.SpacingProperty, new DynamicResourceExtension("AcMsGap"));
        options.Children.Add(MsUi.Heading("合并选项"));
        var rows = new StackPanel();
        rows.Children.Add(MergeOptionRow("转场", _transition));
        var format = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        _formatDetail.VerticalAlignment = VerticalAlignment.Center;
        format.Children.Add(_formatDetail); Grid.SetColumn(_format, 1); format.Children.Add(_format);
        rows.Children.Add(MergeOptionRow("输出格式", format));
        var folder = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 4 };
        _folderPath.HorizontalAlignment = HorizontalAlignment.Right;
        _folderPath.VerticalAlignment = VerticalAlignment.Center;
        MsUi.Brush(_folderPath, TextBlock.ForegroundProperty, "UiText");
        Localization.SetIsUserText(_folderPath, true);
        folder.Children.Add(_folderPath); Grid.SetColumn(_folderButton, 1); folder.Children.Add(_folderButton);
        rows.Children.Add(MergeOptionRow("输出位置", folder));
        options.Children.Add(new Border { Classes = { "ms-group" }, Child = rows });
        results.Children.Add(MsUi.Heading("预计结果"));
        results.Children.Add(_expected);
        var note = MsUi.Caption("选择淡入淡出时需要重新编码。输出大小与用时取决于编码设置和设备性能。");
        note.TextWrapping = TextWrapping.Wrap;
        results.Children.Add(note);
        _mergeBody.Children.Add(MsUi.Columns("*,*", options, results));
        _mergeBody.Children.Add(_progressText); _mergeBody.Children.Add(_progress);
        UpdatePages();
        return MsUi.Scroll(_mergeBody);
    }

    private static Border MergeOptionRow(string label, Control value)
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12, MinHeight = 38, Margin = new(14, 0) };
        grid.Children.Add(new TextBlock { Classes = { "ms-body" }, Text = Localization.Text(label), VerticalAlignment = VerticalAlignment.Center });
        value.HorizontalAlignment = HorizontalAlignment.Right; value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 1); grid.Children.Add(value);
        return new Border { Classes = { "ms-row-line" }, BorderThickness = new(0, label == "转场" ? 0 : .5, 0, 0), Child = grid };
    }

    private Button StripArrow(string label, string icon, int direction)
    {
        var button = new Button { Classes = { "ac" }, Width = 24, Height = 24, Padding = new(0),
            Content = new LucideIcon { Kind = icon, Width = 14, Height = 14 } };
        button.Bind(Button.CornerRadiusProperty, new DynamicResourceExtension("AcButtonRadius"));
        ToolTip.SetTip(button, Localization.Text(label)); AutomationProperties.SetName(button, Localization.Text(label));
        button.Click += (_, _) => { _stripPage += direction; UpdateStrip(); };
        return button;
    }

    private void UpdateMergeSummary()
    {
        var edit = PersonClipLifecycle.Edit(_job);
        var ranges = edit.OutputSegments;
        var seconds = ranges.Sum(s => (s.End - s.Start) / s.Speed);
        var kept = ranges.Sum(s => s.End - s.Start);
        var percent = edit.Info.Duration > 0 ? Math.Clamp(kept / edit.Info.Duration, 0, 1) : 0;
        _summary.Set(0, "保留片段", Localization.Format($"{ranges.Count} 段"));
        _summary.Set(1, "合并后时长", MsUi.Time(seconds), Localization.Text("/ 原片 ") + MsUi.Time(edit.Info.Duration));
        _summary.Set(3, "预计用时", Localization.Text("待估算"));
        _keptTime.Text = MsUi.Time(kept); _retention.Value = percent * 100;
        _removedTime.Text = Localization.Format($"{percent:0%} · 移除 {MsUi.Time(Math.Max(0, edit.Info.Duration - kept))}");
        _folderPath.Text = MsUi.Display(_folder); ToolTip.SetTip(_folderPath, _folder);
        _formatDetail.Text = _format.SelectedItem as string == "Fast Copy" ? MsUi.MediaDetail(edit.Info with { Duration = 0 }) : "";
        _format.ItemTemplate ??= new Avalonia.Controls.Templates.FuncDataTemplate<string>((preset, _) =>
            new TextBlock { Text = Localization.Text(QuickClipBatch.PresetLabel(preset!)) });

        string size = Localization.Text("待估算"), output = "—", method;
        try
        {
            // Use the export contract for both the file name and actual stream-copy decision.
            var draft = PersonClipLifecycle.PrepareMerge(_job,
                new(_format.SelectedItem as string ?? SourceOutput.Original, _transition.SelectedIndex == 1), _folder, [_job.Output]);
            output = Path.GetFileName(_pages.Pages[2].State == PageState.Completed ? _job.Output : draft.Output);
            method = draft.Options.CopyStreams ? "直接拷贝视频流 · 不重新编码" : "重新编码";
            if (draft.Options.CopyStreams && File.Exists(edit.Path) && edit.Info.Duration > 0)
                size = Localization.Text("约 ") + MsUi.Size((long)(new FileInfo(edit.Path).Length * percent));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { method = error.Message; }
        _summary.Set(2, "预计输出大小", size);
        _expected.Update([("输出文件", output, ""), ("大小 / 用时", size, " · " + Localization.Text("待估算")), ("处理方式", method, "")], rowHeight: 38);
    }

    private void ClearThumbs() { foreach (var preview in _thumbs) preview.Dispose(); _thumbs.Clear(); _strip.Children.Clear(); }

    private void UpdateStrip()
    {
        var edit = PersonClipLifecycle.Edit(_job); var ranges = edit.OutputSegments;
        _stripPage = Math.Clamp(_stripPage, 0, Math.Max(0, (ranges.Count - 1) / StripPageSize));
        if (_previous is not null) _previous.IsEnabled = _stripPage > 0;
        if (_next is not null) _next.IsEnabled = (_stripPage + 1) * StripPageSize < ranges.Count;
        _stripNote.Text = Localization.Format($"第 {(ranges.Count == 0 ? 0 : _stripPage * StripPageSize + 1)}–{Math.Min(ranges.Count, (_stripPage + 1) * StripPageSize)} 段 / 共 {ranges.Count} 段");
        var key = $"{_pages.Snapshot.Revision}:{_stripPage}";
        if (_stripKey == key) return; _stripKey = key;
        ClearThumbs();
        var page = ranges.Skip(_stripPage * StripPageSize).Take(StripPageSize).ToArray();
        _strip.ColumnDefinitions = new(string.Join(",", Enumerable.Repeat("*", StripPageSize)));
        for (var i = 0; i < page.Length; i++)
        {
            var segment = page[i];
            var badge = new CountBadge { Text = (_stripPage * StripPageSize + i + 1).ToString(),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(4), Padding = new(4, 0) };
            MsUi.Brush(badge, Border.BackgroundProperty, "UiMediaOverlay");
            MsUi.Brush(badge.Child!, TextBlock.ForegroundProperty, "UiMediaText");
            var preview = new MsPreviewCard(badge, strip: true);
            preview.ShowFrame(_engine, edit.Path, segment.Start);
            preview.SetCaption(Localization.Format($"{(segment.End - segment.Start) / segment.Speed:0.0} 秒"), "", PersonTaskView.Time(segment.Start, 0));
            Grid.SetColumn(preview, i); _thumbs.Add(preview); _strip.Children.Add(preview);
        }
    }
}
