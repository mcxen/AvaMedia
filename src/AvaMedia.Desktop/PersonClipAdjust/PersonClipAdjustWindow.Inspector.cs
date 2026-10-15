using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;
using AvaMedia.Desktop.MultiStage;

namespace AvaMedia.Desktop.PersonClipAdjust;

internal sealed partial class PersonClipAdjustWindow
{
    private readonly StackPanel _inspector = new() { Spacing = 10 };
    private readonly TextBlock _segmentHeading = new() { FontSize = 13, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _segmentDetail = new() { Classes = { "ms-caption" }, TextWrapping = TextWrapping.Wrap };
    private readonly ToggleSwitch _keep = new() { Classes = { "ac", "small" }, OnContent = null, OffContent = null };
    private readonly Image[] _boundsImages = [new() { Stretch = Stretch.UniformToFill }, new() { Stretch = Stretch.UniformToFill }];
    private readonly TextBox[] _boundsTimes = [new() { Classes = { "ac" } }, new() { Classes = { "ac" } }];
    private readonly PreviewRequest[] _boundsRequests = [new(), new()];
    private readonly Bitmap?[] _boundsBitmaps = new Bitmap?[2];
    private readonly double[] _boundsLoaded = [double.NaN, double.NaN];
    private readonly EvidenceBars _evidence = new();
    private readonly StackPanel _evidenceDetails = new() { Spacing = 5 };
    private readonly TextBlock _effectSummary = new() { Classes = { "ms-caption" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private Button _effectsButton = null!, _moreButton = null!;
    private bool _updatingInspector;

    partial void BuildInspectorContent()
    {
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        header.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { _segmentHeading, _segmentDetail } });
        _segmentDetail.Margin = new(6, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(_keep, Localization.Text("保留当前片段"));
        _keep.IsCheckedChanged += (_, _) => { if (!_updatingInspector && Selected is { } s) Do(() => _session.SetKeep(s.Id, _keep.IsChecked == true)); };
        Grid.SetColumn(_keep, 1); header.Children.Add(_keep); _inspector.Children.Add(header);
        var boundaries = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 10 };
        for (var i = 0; i < 2; i++)
        {
            var index = i; var label = i == 0 ? "起点" : "终点";
            var frame = new Grid(); frame.Children.Add(_boundsImages[i]);
            var tag = SegmentTag(label, "UiAccent", "UiMediaText"); tag.HorizontalAlignment = HorizontalAlignment.Left; tag.VerticalAlignment = VerticalAlignment.Top; tag.Margin = new(5); frame.Children.Add(tag);
            var picture = new Border { CornerRadius = new(6), ClipToBounds = true, Child = frame, Height = 63 };
            MsUi.Brush(picture, Border.BackgroundProperty, "UiFill");
            picture.Tapped += (_, _) => { if (Selected is { } s) SeekTo(index == 0 ? s.Start : s.End); };
            picture.DoubleTapped += (_, e) => { ImageViewerWindow.ShowPreview(DialogOwner, _boundsImages[index].Source, Localization.Text(label)); e.Handled = true; };
            picture.SizeChanged += (_, e) => { if (e.NewSize.Width > 0) picture.Height = e.NewSize.Width * 9 / 16; };
            ToolTip.SetTip(picture, Localization.Text("点击跳转，双击查看大图"));
            var timeRow = new Grid { ColumnDefinitions = new("18,*,18"), ColumnSpacing = 2, Margin = new(0, 5, 0, 0) };
            var back = IconButton("chevron-left", i == 0 ? "起点后退" : "终点后退", () => NudgeBound(index == 0, -1), 12);
            var next = IconButton("chevron-right", i == 0 ? "起点前进" : "终点前进", () => NudgeBound(index == 0, 1), 12);
            foreach (var button in new[] { back, next }) { button.Width = 18; button.Height = 24; button.Padding = new(0); }
            var field = _boundsTimes[i]; field.Height = 24; field.MinHeight = 0; field.Padding = new(2, 0); field.FontSize = 12;
            field.HorizontalContentAlignment = HorizontalAlignment.Center; field.VerticalContentAlignment = VerticalAlignment.Center;
            MsUi.Brush(field, TextBox.BackgroundProperty, "UiFill");
            Avalonia.Automation.AutomationProperties.SetName(field, Localization.Text(label));
            field.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || Selected is not { } s) return;
                var original = index == 0 ? s.Start : s.End;
                if (EditorTime.TryRead(field.Text, original, out var at)) Do(() => _session.SetBounds(s.Id, index == 0 ? at : s.Start, index == 0 ? s.End : at));
                else Notify(Localization.Text("请输入有效时间码（mm:ss.mmm）"));
                RefreshInspector(); e.Handled = true;
            };
            timeRow.Children.Add(back); Grid.SetColumn(field, 1); timeRow.Children.Add(field); Grid.SetColumn(next, 2); timeRow.Children.Add(next);
            var column = new StackPanel { Children = { picture, timeRow } };
            Grid.SetColumn(column, i); boundaries.Children.Add(column);
        }
        _inspector.Children.Add(boundaries);
        _inspector.Children.Add(MsUi.Heading("检测依据")); _inspector.Children.Add(_evidence); _inspector.Children.Add(_evidenceDetails);
        _effectsButton = TextButton("片段效果", () => ShowEffects(_effectsButton));
        _effectsButton.HorizontalContentAlignment = HorizontalAlignment.Stretch; _effectsButton.MinHeight = 30; _effectsButton.CornerRadius = new(10);
        var effectRow = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8 };
        effectRow.Children.Add(MsUi.Caption(Localization.Text("片段效果"))); Grid.SetColumn(_effectSummary, 1); effectRow.Children.Add(_effectSummary);
        var chevron = new LucideIcon { Kind = "chevron-right", Width = 11, Height = 11 }; MsUi.Brush(chevron, LucideIcon.ForegroundProperty, "UiTextSecondary");
        Grid.SetColumn(chevron, 2); effectRow.Children.Add(chevron); _effectsButton.Content = effectRow; _inspector.Children.Add(_effectsButton);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        _moreButton = TextButton("更多", () => { if (Selected is { } s) OpenSegmentMenu(_moreButton, s.Id); }, "ellipsis");
        foreach (var b in new[] { TextButton("播放片段", () => PlaySelected()), TextButton("复制片段", DuplicateSelected), _moreButton })
        { b.Margin = new(0, 0, 6, 0); actions.Children.Add(b); }
        _inspector.Children.Add(actions);
        _inspectorSlot.Children.Add(new Border { Classes = { "ms-card" }, Padding = new(14, 12), Child = new ScrollViewer { Content = _inspector, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
    }

    partial void RefreshInspector()
    {
        var s = Selected; _inspector.IsEnabled = s is not null; _updatingInspector = true;
        _segmentHeading.Text = s is null ? Localization.Text("请选择片段") : Localization.Format($"片段 {OrderOf(s.Id)}");
        _segmentDetail.Text = s is null ? "" : Localization.Format($"{MsUi.Number(Math.Round(s.Duration, 1))} 秒 · 第 {OrderOf(s.Id)} / {_session.Segments.Count} 段");
        _keep.IsChecked = s?.Keep == true; _updatingInspector = false;
        _evidenceDetails.Children.Clear();
        if (s is null) { _evidence.Update([], 0, 0); return; }
        if (_cropping && _cropSegment != s.Id) EndCrop();
        _boundsTimes[0].Text = AdjustTime.Clock(s.Start, 3); _boundsTimes[1].Text = AdjustTime.Clock(s.End, 3);
        _ = LoadBoundAsync(0, s.Start); _ = LoadBoundAsync(1, s.End);
        var evidence = _session.Evidence(s.Id); _evidence.Update(evidence?.Around ?? [], s.Start, s.End);
        if (!_session.HasScores || evidence is null)
            _evidenceDetails.Children.Add(new TextBlock { Classes = { "ms-small" }, Text = Localization.Text("此结果未保存逐帧分数，重新检测后可查看"), TextWrapping = TextWrapping.Wrap });
        else
        {
            EvidenceRow("置信度", Localization.Format($"平均 {evidence.Average:0.00} · 峰值 {evidence.Peak:0.00}"));
            EvidenceRow("采样帧", Localization.Format($"{evidence.Samples} 帧 · 有人 {evidence.PersonSamples} · 不确定 {evidence.UncertainSamples}"));
            EvidenceRow("边界", evidence.BoundarySamples > 0 ? Localization.Format($"已细化 · 补检 {evidence.BoundarySamples} 帧") : Localization.Text("未细化"));
        }
        _effectSummary.Text = EffectsSummary(s.Options); ToolTip.SetTip(_effectSummary, _effectSummary.Text);
        RefreshPreviewEffects();
    }
    private void EvidenceRow(string key, string value)
    {
        var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        row.Children.Add(MsUi.Small(Localization.Text(key)));
        var text = MsUi.Small(value); MsUi.Brush(text, TextBlock.ForegroundProperty, "UiText"); text.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(text, 1); row.Children.Add(text); _evidenceDetails.Children.Add(row);
    }
    partial void RefreshLiveBounds()
    {
        if (LiveBounds is not { } bounds || Selected?.Id != bounds.Id) return;
        _boundsTimes[0].Text = AdjustTime.Clock(bounds.Start, 3); _boundsTimes[1].Text = AdjustTime.Clock(bounds.End, 3);
        for (var i = 0; i < 2; i++) MsUi.Brush(_boundsTimes[i], TextBox.ForegroundProperty, "UiWarning");
    }
    private async Task LoadBoundAsync(int index, double at)
    {
        MsUi.Brush(_boundsTimes[index], TextBox.ForegroundProperty, "UiText");
        if (_boundsLoaded[index] == at) return;
        _boundsLoaded[index] = at;
        var token = _boundsRequests[index].Restart(_lifetime.Token);
        var bitmap = await DecodeAsync(at, 256, 144, token, endExclusive: index == 1);
        if (bitmap is null) { if (_boundsRequests[index].IsCurrent(token)) _boundsLoaded[index] = double.NaN; return; }
        if (!_boundsRequests[index].IsCurrent(token) || _closed) { bitmap.Dispose(); return; }
        _boundsImages[index].Source = bitmap; var old = _boundsBitmaps[index]; _boundsBitmaps[index] = bitmap; old?.Dispose();
    }
    private void NudgeBound(bool start, int delta)
    {
        if (Selected is { } s) Do(() => _session.SetBounds(s.Id, start ? s.Start + delta * Precision : s.Start, start ? s.End : s.End + delta * Precision));
    }
    private static string EffectsSummary(ConversionOptions o)
    {
        var parts = new List<string>();
        if (o.Speed != 1) parts.Add(MsUi.Number(o.Speed) + "×");
        if (o.FadeIn > 0) parts.Add(Localization.Format($"淡入 {MsUi.Number(o.FadeIn)} 秒"));
        if (o.FadeOut > 0) parts.Add(Localization.Format($"淡出 {MsUi.Number(o.FadeOut)} 秒"));
        if (o.CropWidth > 0) parts.Add(Localization.Text("裁剪"));
        if (o.Rotation != 0) parts.Add(Localization.Text(BatchRotate.Direction(o.Rotation)));
        if (o.Flip) parts.Add(Localization.Text("水平镜像"));
        if (o.DelogoWidth > 0 || o.DelogoRegions?.Length > 0) parts.Add(Localization.Text("去水印"));
        return parts.Count == 0 ? Localization.Text("默认") : string.Join(" · ", parts);
    }

    private void EditEffect(int id, Action<ConversionOptions> edit)
    {
        if (_session.Segments.FirstOrDefault(s => s.Id == id) is not { } segment) return;
        var options = segment.Options.Clone(); edit(options); Do(() => _session.SetEffects(id, options));
    }
    private void ShowEffects(Control anchor)
    {
        if (Selected is not { } s) return;
        var id = s.Id; var o = s.Options;
        var panel = new StackPanel { Spacing = 10, Width = 320 };
        panel.Children.Add(MsUi.Heading("片段效果（仅此段）"));
        void Choice<T>(string title, T[] values, T selected, Func<T, string> label, Action<ConversionOptions, T> set)
        {
            var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12 };
            row.Children.Add(MsUi.Caption(Localization.Text(title)));
            var combo = new ComboBox { Classes = { "ac" }, HorizontalAlignment = HorizontalAlignment.Right, ItemsSource = values.Select(label).ToArray(), SelectedIndex = Array.IndexOf(values, selected) };
            Avalonia.Automation.AutomationProperties.SetName(combo, Localization.Text(title));
            combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) EditEffect(id, option => set(option, values[combo.SelectedIndex])); };
            Grid.SetColumn(combo, 1); row.Children.Add(combo); panel.Children.Add(row);
        }
        Choice("速度", new[] { 1d, .25, .5, .75, 1.25, 1.5, 2, 4 }.Append(o.Speed).Distinct().ToArray(), o.Speed, v => v == 1 ? Localization.Text("默认") : MsUi.Number(v) + "×", (x, v) => x.Speed = v);
        string Fade(double v) => v == 0 ? Localization.Text("关闭") : Localization.Format($"{MsUi.Number(v)} 秒");
        Choice("淡入", new[] { 0d, .5, 1, 2, 3 }.Append(o.FadeIn).Distinct().ToArray(), o.FadeIn, Fade, (x, v) => x.FadeIn = v);
        Choice("淡出", new[] { 0d, .5, 1, 2, 3 }.Append(o.FadeOut).Distinct().ToArray(), o.FadeOut, Fade, (x, v) => x.FadeOut = v);
        Choice("方向", new[] { 0, 90, 180, 270 }, o.Rotation, v => Localization.Text(v == 0 ? "保持原方向" : BatchRotate.Direction(v)), (x, v) => x.Rotation = v);
        var mirror = new CheckBox { Classes = { "ac" }, Content = Localization.Text("水平镜像"), IsChecked = o.Flip };
        mirror.IsCheckedChanged += (_, _) => EditEffect(id, x => x.Flip = mirror.IsChecked == true); panel.Children.Add(mirror);
        var flyout = new Flyout { Content = new ScrollViewer { Content = panel, MaxHeight = 560, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        panel.Children.Add(TextButton(_cropping ? "完成裁剪" : "在预览上裁剪", () => { flyout.Hide(); if (_cropping) EndCrop(); else BeginCrop(); }));
        panel.Children.Add(TextButton("清除裁剪", () => { if (_cropping) EndCrop(); EditEffect(id, x => { x.CropX = x.CropY = x.CropWidth = x.CropHeight = 0; }); }));
        var ratio = new ComboBox { Classes = { "ac" }, ItemsSource = new[] { Localization.Text("自由比例"), "16:9", "9:16", "4:3", "3:4", "1:1" }, SelectedIndex = 0 };
        Avalonia.Automation.AutomationProperties.SetName(ratio, Localization.Text("裁剪比例"));
        ratio.SelectionChanged += (_, _) => _crop.AspectRatio = ratio.SelectedIndex switch { 1 => 16d / 9 / _crop.PixelAspectRatio, 2 => 9d / 16 / _crop.PixelAspectRatio, 3 => 4d / 3 / _crop.PixelAspectRatio, 4 => 3d / 4 / _crop.PixelAspectRatio, 5 => 1 / _crop.PixelAspectRatio, _ => 0 };
        panel.Children.Add(ratio);
        panel.Children.Add(BuildRegionFields(id, o));
        panel.Children.Add(TextButton(_cropping && _delogoMode ? "完成去水印选区" : "在预览上去水印", () => { flyout.Hide(); if (_cropping) EndCrop(); else BeginCrop(delogo: true); }));
        panel.Children.Add(TextButton("清除去水印", () => EditEffect(id, x => { x.DelogoX = x.DelogoY = x.DelogoWidth = x.DelogoHeight = 0; x.DelogoRegions = null; })));
        AddOrientationControls(panel, id);
        panel.Children.Add(TextButton("输出配置…", () => { flyout.Hide(); _ = EditFullOptionsAsync(id); }));
        panel.Children.Add(new TextBlock { Classes = { "ms-small" }, Text = Localization.Text("速度与方向可预览；裁剪、淡入淡出及其他效果在输出时生效。"), TextWrapping = TextWrapping.Wrap });
        flyout.ShowAt(anchor);
    }
    private Control BuildRegionFields(int id, ConversionOptions options)
    {
        var panel = new StackPanel { Spacing = 6 }; panel.Children.Add(MsUi.Caption(Localization.Text("裁剪坐标（像素）")));
        var row = new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 6 };
        var fields = new NumericUpDown[4]; var values = new[] { options.CropX, options.CropY, options.CropWidth, options.CropHeight };
        var labels = new[] { "横坐标", "纵坐标", "宽度", "高度" };
        for (var i = 0; i < 4; i++)
        {
            var field = new NumericUpDown { Classes = { "ac" }, Minimum = 0, Maximum = i % 2 == 0 ? _session.Info.Width : _session.Info.Height, Increment = 2, Value = values[i], FormatString = "0", MinWidth = 0 };
            fields[i] = field; Avalonia.Automation.AutomationProperties.SetName(field, Localization.Text(labels[i])); ToolTip.SetTip(field, Localization.Text(labels[i]));
            Grid.SetColumn(field, i); row.Children.Add(field);
        }
        panel.Children.Add(row); panel.Children.Add(TextButton("应用坐标", () => EditEffect(id, x => { x.CropX = (int)(fields[0].Value ?? 0); x.CropY = (int)(fields[1].Value ?? 0); x.CropWidth = (int)(fields[2].Value ?? 0); x.CropHeight = (int)(fields[3].Value ?? 0); })));
        return panel;
    }
    private async Task EditFullOptionsAsync(int id)
    {
        if (_session.Segments.FirstOrDefault(s => s.Id == id) is not { } segment) return;
        try
        {
            var options = await new OptionsWindow(segment.Options.Clone(), kind: MediaOptionsKind.ClipEdit, previewEngine: _engine, previewSource: _session.SourcePath).ShowDialog<ConversionOptions?>(DialogOwner);
            if (options is not null && !_closed) Do(() => _session.SetEffects(id, options));
        }
        catch (Exception error) { Notify(error.Message); }
    }

    partial void OpenSegmentMenu(Control anchor, int id)
    {
        var menu = new ContextMenu();
        void Item(string title, Action action, bool enabled = true)
        { var item = new MenuItem { Header = Localization.Text(title), IsEnabled = enabled }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        var index = _session.Segments.ToList().FindIndex(s => s.Id == id);
        Item("与上一段合并", () => Do(() => _session.MergeWithPrevious(id)), index > 0);
        Item("与下一段合并", () => Do(() => _session.MergeWithNext(id)), index >= 0 && index < _session.Segments.Count - 1);
        Item("批量分割…", () => _ = SplitManyAsync(id)); menu.Items.Add(new Separator());
        Item("设为排除区间", () => { if (_session.Segments.FirstOrDefault(s => s.Id == id) is { } s) Do(() => { _session.AddExclusion(_selectedRange?.Start ?? s.Start, _selectedRange?.End ?? s.End); _selectedRange = null; _timeline.Range = null; }); });
        Item("按检测结果还原此段", () => Do(() => _session.RestoreSegment(id)));
        Item("反转保留 / 移除", () => Do(_session.InvertKeep)); menu.Items.Add(new Separator());
        Item("输出顺序上移", () => Do(() => _session.Move(id, -1)), index > 0);
        Item("输出顺序下移", () => Do(() => _session.Move(id, 1)), index >= 0 && index < _session.Segments.Count - 1);
        menu.Open(anchor);
    }
    private async Task SplitManyAsync(int id)
    {
        if (_session.Segments.FirstOrDefault(s => s.Id == id) is not { } segment) return;
        try
        {
            var parts = await new ClipSplitWindow(segment.Options, _session.Duration, _engine, _session.SourcePath).ShowDialog<IReadOnlyList<ConversionOptions>?>(DialogOwner);
            if (parts is not null && !_closed) Do(() => _session.SplitMany(id, parts));
        }
        catch (Exception error) { Notify(error.Message); }
    }
    private void DuplicateSelected() { if (Selected is { } s) Do(() => Select(_session.Duplicate(s.Id))); }
    private void MoveSelected(int delta) { if (Selected is { } s) Do(() => _session.Move(s.Id, delta)); }
    partial void HandleSegmentKey(KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.D when command: DuplicateSelected(); break;
            case Key.I: SetBoundFromPlayhead(true); break;
            case Key.O: SetBoundFromPlayhead(false); break;
            case Key.S when !command: SplitAtPlayhead(); break;
            case Key.Delete or Key.Back: if (Selected is { } s) Do(() => _session.SetKeep(s.Id, !s.Keep)); break;
            case Key.Up when e.KeyModifiers.HasFlag(KeyModifiers.Alt): MoveSelected(-1); break;
            case Key.Down when e.KeyModifiers.HasFlag(KeyModifiers.Alt): MoveSelected(1); break;
            default: return;
        }
        e.Handled = true;
    }
    partial void DisposeInspector()
    {
        foreach (var request in _boundsRequests) request.Dispose();
        foreach (var bitmap in _boundsBitmaps) bitmap?.Dispose();
        if (_orientationJob is { } job && _orientationChanged is { } changed) job.PropertyChanged -= changed;
    }

    private QueueService? _orientationQueue;
    private Job? _orientationJob;
    private PropertyChangedEventHandler? _orientationChanged;
    private VideoOrientationResult? _orientationResult;
    private void AddOrientationControls(StackPanel panel, int id)
    {
        var status = new TextBlock { Classes = { "ms-small" }, TextWrapping = TextWrapping.Wrap };
        var apply = TextButton("应用建议", () => { if (_orientationResult?.Rotation is { } rotation) EditEffect(id, x => x.Rotation = rotation); });
        apply.IsEnabled = _orientationResult?.IsCertain == true;
        var detect = TextButton("检测方向", () => _ = DetectOrientationAsync(id, status, apply));
        detect.IsEnabled = _orientationJob is null || _orientationJob.State is JobState.Completed or JobState.Failed or JobState.Cancelled;
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { detect, apply } });
        panel.Children.Add(TextButton("停止检测", () =>
        {
            if (_orientationJob is not { } job) return;
            var main = (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;
            if (main is not null) main.StopOrientationJob(job); else _orientationQueue?.Stop(job);
        }));
        status.Text = _orientationResult is { } result ? Localization.OrientationReason(result) : ""; panel.Children.Add(status);
    }
    private async Task DetectOrientationAsync(int id, TextBlock status, Button apply)
    {
        if (_orientationJob?.State is JobState.Running or JobState.Waiting) return;
        if (_session.Segments.FirstOrDefault(s => s.Id == id) is not { } segment) return;
        var job = new Job { FeatureId = "rotate", Inputs = [_session.SourcePath], Options = new() { Format = "json", VideoStreamIndex = segment.Options.VideoStreamIndex, Orientation = new(SourceVideoExport.Original, OutputFolder, false, false) } };
        job.Output = AiTaskResults.InternalOutputFor(job); _orientationJob = job; _orientationResult = null; apply.IsEnabled = false;
        status.Text = Localization.Text("后台检测中 · 关闭此窗口后继续运行");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _orientationChanged = (_, change) =>
        {
            if (change.PropertyName == nameof(Job.State) && job.State is JobState.Completed or JobState.Failed or JobState.Cancelled) finished.TrySetResult();
        };
        job.PropertyChanged += _orientationChanged;
        try
        {
            var main = (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow as MainWindow;
            if (main is not null) main.SubmitOrientationJob(job);
            else { _orientationQueue = new QueueService(new OrientationTaskService(_engine)); _ = _orientationQueue.Run([job]); }
            await finished.Task;
            if (_closed) return;
            _orientationResult = job.OrientationResult?.Detection; apply.IsEnabled = _orientationResult?.IsCertain == true;
            status.Text = _orientationResult is { } result ? Localization.Text(result.Description) + " · " + Localization.OrientationReason(result) : job.Error;
        }
        catch (Exception error) { if (!_closed) Notify(error.Message); }
        finally { job.PropertyChanged -= _orientationChanged; }
    }
}
