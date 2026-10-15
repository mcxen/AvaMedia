using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;
using AvaMedia.Desktop.MultiStage;

namespace AvaMedia.Desktop.PersonClipAdjust;

/// <summary>Host services of the adjust window; the window itself never touches the queue.</summary>
internal sealed record PersonClipAdjustHost(
    IMediaEngine Engine,
    string OutputFolder,
    bool OutputToSource,
    string Preset,
    Func<PersonClipRequest, Task> Export,
    Func<PersonClipOptions, Task> Redetect);

/// <summary>
/// 保留有人片段 · 调整片段 (design A: list + inspector). One independent window per task, placed beside the main window.
/// Every edit is a command on <see cref="IPersonClipEditSession"/>; the session is saved into the task result after each change.
/// </summary>
internal sealed partial class PersonClipAdjustWindow : Window
{
    private readonly IPersonClipEditSession _session;
    private readonly PersonClipAdjustHost _host;
    private readonly IMediaEngine _engine;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private int? _selected;
    private bool _closed, _saved = true, _saving;
    private int _editRevision;
    private double _position = 0;
    private MediaInfo? _info;
    private readonly Grid _listSlot = new(), _previewSlot = new(), _inspectorSlot = new(), _timelineSlot = new();
    private Task _saveTask = Task.CompletedTask;

    // Header
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _meta = new() { Classes = { "ms-caption" } };
    private readonly Border _stateDot = new() { Width = 6, Height = 6, CornerRadius = new(3), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _state = new() { Classes = { "ms-caption" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _undo, _redo, _redetect;
    private readonly Border _stale = new() { IsVisible = false, CornerRadius = new(10), Padding = new(12, 8) };
    // Footer
    private readonly TextBlock _outputPath = new() { Classes = { "ms-caption" }, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _format = new() { Classes = { "ac" }, ItemsSource = QuickClipBatch.Presets, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _mode = new() { Classes = { "ac" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _export, _restoreAll, _close;
    private string _folder;
    private bool _toSource;

    public PersonClipAdjustWindow(IPersonClipEditSession session, PersonClipAdjustHost host)
    {
        _session = session; _host = host; _engine = host.Engine;
        _folder = host.OutputFolder; _toSource = host.OutputToSource; _info = session.Info;
        Classes.Add("multistage-task-window"); Classes.Add("person-clip-adjust-window");
        Styles.Add(new StyleInclude(new Uri("avares://AvaMedia.Desktop/")) { Source = new("avares://AvaMedia.Desktop/Styles/MultiStageStyles.axaml") });
        Title = Localization.Text("调整片段 · 保留有人片段");
        Width = 1240; Height = 800; MinWidth = 1080; MinHeight = 700;
        Bind(BackgroundProperty, new DynamicResourceExtension("UiCanvas"));
        WindowArtwork.SetKind(this, "person-clip");

        _undo = IconButton("undo-2", "撤销", () => Do(_session.Undo));
        _redo = IconButton("redo-2", "重做", () => Do(_session.Redo));
        _redetect = TextButton("重新检测…", () => _ = RedetectAsync(), "rotate-ccw");
        _redetect.Height = 24; _redetect.MinHeight = 0;
        _restoreAll = MsUi.Action("还原检测结果", RestoreAllAsync);
        _close = MsUi.Action("关闭", () => { Close(); return Task.CompletedTask; });
        _export = MsUi.Action("导出", ExportAsync, primary: true);
        _mode.ItemsSource = new[] { Localization.Text("立即开始"), Localization.Text("仅加入队列") }; _mode.SelectedIndex = 0;
        _mode.Bind(MinWidthProperty, new DynamicResourceExtension("AcTaskModeWidth"));
        Avalonia.Automation.AutomationProperties.SetName(_mode, Localization.Text("任务执行方式"));
        _format.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((value, _) => new TextBlock { Text = Localization.Text(QuickClipBatch.PresetLabel(value ?? "")) });
        _format.SelectedItem = QuickClipBatch.Presets.Contains(host.Preset) ? host.Preset : PersonClipTaskOptions.DefaultExportPreset;
        Avalonia.Automation.AutomationProperties.SetName(_format, Localization.Text("输出格式"));

        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto") };
        root.Bind(Grid.RowSpacingProperty, new DynamicResourceExtension("AcMsGap"));
        root.Bind(MarginProperty, new DynamicResourceExtension("AcMsBodyMargin"));
        root.Children.Add(BuildHeader());
        Grid.SetRow(BuildStale(), 1); root.Children.Add(_stale);
        var body = MsUi.Columns("288,*,272", _listSlot, _previewSlot, _inspectorSlot);
        body.RowDefinitions = new("*,Auto");
        body.Bind(Grid.RowSpacingProperty, new DynamicResourceExtension("AcMsGap"));
        Grid.SetRowSpan(_listSlot, 2);
        Grid.SetRow(_timelineSlot, 1); Grid.SetColumn(_timelineSlot, 1); Grid.SetColumnSpan(_timelineSlot, 2); body.Children.Add(_timelineSlot);
        Grid.SetRow(body, 2); root.Children.Add(body);
        var footer = BuildFooter(); Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = WindowChrome.WrapDialog(this, root);
        BuildListContent(); BuildPreviewContent(); BuildInspectorContent(); BuildTimelineContent();

        _session.Changed += SessionChanged;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _saveTask = SaveAsync(); };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notice = ""; RefreshHeader(); };
        KeyDown += OnKeyDown;
        Opened += (_, _) => InitializeMedia();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _closed = true; _session.Changed -= SessionChanged; _saveTimer.Stop(); _noticeTimer.Stop();
            _lifetime.Cancel(); DisposeMedia(); DisposeThumbnails(); DisposeTimeline(); DisposeInspector(); _lifetime.Dispose();
        };
        _selected = _session.Segments.FirstOrDefault(s => s.Keep)?.Id ?? _session.Segments.FirstOrDefault()?.Id;
        RefreshAll();
    }

    /// <summary>The job this window edits; the host keeps one window per job.</summary>
    public Job Job => _session.Job;
    internal void ShowIndependent() => WindowChrome.ShowIndependent(this);

    // ------------------------------------------------------------------ layout helpers
    internal static Button IconButton(string icon, string tip, Action action, double size = 16)
    {
        var glyph = new LucideIcon { Kind = icon, Width = size, Height = size };
        var button = new Button { Classes = { "ac", "icon-only" }, Content = glyph, Width = 28, Height = 28, MinHeight = 0, CornerRadius = new(14), Padding = new(6), VerticalAlignment = VerticalAlignment.Center };
        glyph.Bind(LucideIcon.ForegroundProperty, new Avalonia.Data.Binding(nameof(Button.Foreground)) { Source = button });
        ToolTip.SetTip(button, Localization.Text(tip));
        Avalonia.Automation.AutomationProperties.SetName(button, Localization.Text(tip));
        button.Click += (_, _) => action();
        return button;
    }

    internal static Button TextButton(string text, Action action, string? icon = null, bool small = true)
    {
        var label = new TextBlock { Text = Localization.Text(text), VerticalAlignment = VerticalAlignment.Center };
        object content = label;
        if (icon is not null)
        {
            var glyph = new LucideIcon { Kind = icon, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { glyph, label } };
            content = panel;
            var button0 = new Button { Classes = { "ac", "text-action" }, Content = content, VerticalAlignment = VerticalAlignment.Center };
            glyph.Bind(LucideIcon.ForegroundProperty, new Avalonia.Data.Binding(nameof(Button.Foreground)) { Source = button0 });
            if (small) button0.Classes.Add("small");
            Avalonia.Automation.AutomationProperties.SetName(button0, Localization.Text(text));
            button0.Click += (_, _) => action();
            return button0;
        }
        var button = new Button { Classes = { "ac", "text-action" }, Content = content, VerticalAlignment = VerticalAlignment.Center };
        if (small) button.Classes.Add("small");
        button.Click += (_, _) => action();
        return button;
    }

    private Control BuildHeader()
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto") };
        var left = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        _title.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("AcMsTitleFont"));
        _title.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("UiText"));
        Localization.SetIsUserText(_title, true);
        left.Children.Add(_title); left.Children.Add(_meta);
        grid.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0) };
        state.Children.Add(_stateDot); state.Children.Add(_state);
        right.Children.Add(state); right.Children.Add(_undo); right.Children.Add(_redo); right.Children.Add(_redetect);
        Grid.SetColumn(right, 1); grid.Children.Add(right);
        return grid;
    }

    private Border BuildStale()
    {
        _stale.Bind(Border.BackgroundProperty, new DynamicResourceExtension("AcWarningFill"));
        var text = new TextBlock { Classes = { "ms-body" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Text = Localization.Text("源视频在检测后已更改，片段可能与画面不再对应。可以继续调整，导出前请重新检测。") };
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("AcWarningText"));
        var icon = new LucideIcon { Kind = "circle-alert", Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(LucideIcon.ForegroundProperty, new DynamicResourceExtension("UiWarning"));
        var action = TextButton("重新检测…", () => _ = RedetectAsync());
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(icon); Grid.SetColumn(text, 1); grid.Children.Add(text); Grid.SetColumn(action, 2); grid.Children.Add(action);
        _stale.Child = grid;
        return _stale;
    }

    private Control BuildFooter()
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Center };
        var output = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        var folderIcon = new LucideIcon { Kind = "folder", Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        folderIcon.Bind(LucideIcon.ForegroundProperty, new DynamicResourceExtension("UiTextSecondary"));
        output.Children.Add(folderIcon);
        output.Children.Add(MsUi.Caption(Localization.Text("输出到")));
        Localization.SetIsUserText(_outputPath, true);
        output.Children.Add(_outputPath);
        Button? change = null;
        change = TextButton("更改…", () => ShowOutputMenu(change!));
        change.Classes.Add("link");
        output.Children.Add(change);
        grid.Children.Add(output);
        var format = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        format.Children.Add(MsUi.Caption(Localization.Text("格式"))); format.Children.Add(_format);
        Grid.SetColumn(format, 1); grid.Children.Add(format);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(_restoreAll); actions.Children.Add(_close); actions.Children.Add(_mode); actions.Children.Add(_export);
        Grid.SetColumn(actions, 2); grid.Children.Add(actions);
        return grid;
    }

    private void ShowOutputMenu(Control anchor)
    {
        var source = new MenuItem { Header = Localization.Text("源视频所在文件夹"), ToggleType = MenuItemToggleType.CheckBox, IsChecked = _toSource };
        source.Click += (_, _) => { _toSource = true; RefreshOutput(); };
        var choose = new MenuItem { Header = Localization.Text("选择文件夹…") };
        choose.Click += async (_, _) =>
        {
            if (await Ui.Folder(this, "保存位置") is { } folder) { _folder = folder; _toSource = false; RefreshOutput(); }
        };
        var current = new MenuItem { Header = Localization.Text("当前文件夹"), ToggleType = MenuItemToggleType.CheckBox, IsChecked = !_toSource };
        ToolTip.SetTip(current, _folder);
        current.Click += (_, _) => { _toSource = false; RefreshOutput(); };
        var menu = new ContextMenu { Items = { current, source, choose } };
        menu.Open(anchor);
    }

    private string OutputFolder => _toSource ? Path.GetDirectoryName(_session.SourcePath) ?? _folder : _folder;
    private void RefreshOutput()
    {
        _outputPath.Text = MsUi.Display(OutputFolder) + (_toSource ? " · " + Localization.Text("源文件夹") : "");
        ToolTip.SetTip(_outputPath, OutputFolder);
    }

    // ------------------------------------------------------------------ refresh
    private string _notice = "";
    private bool _noticeError;

    private void SessionChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => SessionChanged(sender, e)); return; }
        if (_selected is { } id && _session.Segments.All(s => s.Id != id))
            _selected = _session.Segments.OrderBy(s => Math.Abs(s.Start - _position)).FirstOrDefault()?.Id;
        _editRevision++; _saved = false; _saveTimer.Stop(); _saveTimer.Start();
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshHeader(); RefreshList(); RefreshInspector(); RefreshTimeline(); RefreshFooter(); RefreshOutput();
    }

    private void RefreshHeader()
    {
        _title.Text = Path.GetFileName(_session.SourcePath);
        ToolTip.SetTip(_title, _session.SourcePath);
        var result = _session.Job.PersonDetectionResult?.Result;
        var detectors = result?.Detectors is { Count: > 0 } list ? string.Join(" + ", list.Select(d => d.Name)) : result?.Backend ?? "";
        var mode = result is null ? "" : result.DetectionMode switch
        {
            PersonDetectionMode.Recall => Localization.Text("减少漏检"), PersonDetectionMode.Consensus => Localization.Text("交叉确认"), _ => Localization.Text("平衡检测")
        };
        _meta.Text = MsUi.MediaDetail(_session.Info, detectors, mode,
            Localization.Format($"阈值 {_session.Threshold.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}"));
        ToolTip.SetTip(_meta, MediaDescription());
        _undo.IsEnabled = _session.CanUndo; _redo.IsEnabled = _session.CanRedo;
        if (_notice.Length > 0)
        {
            _state.Text = _notice;
            MsUi.Brush(_state, TextBlock.ForegroundProperty, _noticeError ? "UiDanger" : "UiTextSecondary");
            MsUi.Brush(_stateDot, Border.BackgroundProperty, _noticeError ? "UiDanger" : "UiAccent");
        }
        else
        {
            var count = _session.ModifiedCount;
            _state.Text = count == 0 ? Localization.Text("检测结果 · 未修改")
                : Localization.Format($"已修改 {count} 处") + " · " + Localization.Text(_saving || !_saved ? "正在保存…" : "已存入任务结果");
            MsUi.Brush(_state, TextBlock.ForegroundProperty, "UiTextSecondary");
            MsUi.Brush(_stateDot, Border.BackgroundProperty, count == 0 ? "UiTrack" : "UiWarning");
        }
        _stale.IsVisible = _session.SourceChanged;
    }

    private string MediaDescription()
    {
        var info = _session.Info;
        return Localization.Format($"{Path.GetFileName(_session.SourcePath)}\n时长: {EditorTime.Format(info.Duration)}    画面: {info.Width} × {info.Height}\n视频: {info.VideoCodec}    音频: {info.AudioCodec}");
    }

    private void RefreshFooter()
    {
        var kept = _session.KeptCount;
        _export.Content = Localization.Format($"导出 {kept} 段");
        _export.IsEnabled = kept > 0 && !_session.SourceChanged;
        ToolTip.SetTip(_export, _session.SourceChanged ? Localization.Text("源视频已更改，请重新检测后导出") : kept == 0 ? Localization.Text("没有保留的片段") : null);
        _restoreAll.IsEnabled = _session.ModifiedCount > 0;
    }

    private void Notify(string message, bool error = true)
    {
        _notice = message; _noticeError = error; _noticeTimer.Stop(); _noticeTimer.Start(); RefreshHeader();
    }

    /// <summary>Run one session command; invalid edits surface as a short header notice instead of a dialog.</summary>
    private void Do(Action command)
    {
        try { command(); }
        catch (ArgumentException error) { Notify(error.Message); }
        catch (InvalidOperationException error) { Notify(error.Message); }
    }

    private PersonClipEditSegment? Selected => _selected is { } id ? _session.Segments.FirstOrDefault(s => s.Id == id) : null;
    private int OrderOf(int id) => _session.Segments.ToList().FindIndex(s => s.Id == id) + 1;

    private void Select(int? id, bool seek = true, bool reveal = true)
    {
        _selected = id;
        RefreshListSelection(); RefreshInspector(); SelectTimeline(id, reveal);
        if (seek && Selected is { } segment && (_position < segment.Start || _position >= segment.End)) SeekTo(segment.Start);
    }

    partial void BuildListContent();
    partial void BuildPreviewContent();
    partial void BuildInspectorContent();
    partial void BuildTimelineContent();
    partial void RefreshList();
    partial void RefreshListSelection();
    partial void RefreshInspector();
    partial void RefreshTimeline();
    partial void SelectTimeline(int? id, bool reveal);
    partial void SeekTo(double seconds);
    partial void InitializeMedia();
    partial void DisposeMedia();
    partial void DisposeThumbnails();
    partial void DisposeTimeline();
    partial void DisposeInspector();
    partial void HandleEditorKey(KeyEventArgs e);
    partial void OpenSegmentMenu(Control anchor, int id);
    partial void PlaySelected();

    // ------------------------------------------------------------------ saving, export, re-detect
    private Task SaveAsync()
    {
        if (_saving) return _saveTask;
        return _saveTask = SavePendingAsync();
    }

    private async Task SavePendingAsync()
    {
        _saving = true; RefreshHeader();
        try
        {
            while (!_saved)
            {
                var revision = _editRevision;
                await _session.SaveAsync(CancellationToken.None);
                _saved = revision == _editRevision;
            }
        }
        catch (Exception error)
        {
            _saved = false; AppDiagnostics.Record("Person clip adjust save", error);
            if (!_closed) Notify(Localization.Format($"保存失败：{error.Message}"));
        }
        finally { _saving = false; if (!_closed) RefreshHeader(); }
    }

    private bool _closingAfterSave;
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingAfterSave) return;
        EndCrop();
        if (_saved && !_saving) return;
        e.Cancel = true; _saveTimer.Stop();
        await SaveAsync();
        if (_saved) { _closingAfterSave = true; Close(); }
    }

    private async Task RestoreAllAsync()
    {
        if (_session.ModifiedCount == 0) return;
        if (!await Ui.Confirm(this, "还原检测结果", Localization.Format($"放弃全部 {_session.ModifiedCount} 处调整，回到检测得到的片段？可以撤销。"), "还原")) return;
        Do(_session.RestoreDetection);
    }

    private async Task ExportAsync()
    {
        EndCrop();
        await StopPlaybackAsync();
        ClipEditResult edit;
        try { edit = _session.ToClipEditResult(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { Notify(error.Message); return; }
        var preset = _format.SelectedItem as string ?? PersonClipTaskOptions.DefaultExportPreset;
        try { QuickClipWorkflow.ValidateJoinedExports([edit], preset); }
        catch (ArgumentException error) { Notify(error.Message); return; }
        var folder = OutputFolder;
        if (string.IsNullOrWhiteSpace(folder)) { Notify(Localization.Text("请选择输出目录。")); return; }
        _saveTimer.Stop(); await _saveTask; await SaveAsync();
        if (!_saved) return;
        try
        {
            await _host.Export(new PersonClipRequest([edit], Path.GetFullPath(folder), _toSource, preset, _mode.SelectedIndex == 0));
            Close();
        }
        catch (Exception error) { Notify(error.Message); }
    }

    private async Task RedetectAsync()
    {
        EndCrop();
        await StopPlaybackAsync();
        _saveTimer.Stop(); await _saveTask; await SaveAsync();
        if (!_saved) return;
        try { await _host.Redetect(_session.RedetectOptions()); }
        catch (Exception error) { await Ui.Message(this, "重新检测", error.Message); }
    }

    // ------------------------------------------------------------------ keyboard
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Z when command && e.KeyModifiers.HasFlag(KeyModifiers.Shift): Do(_session.Redo); break;
            case Key.Z when command: Do(_session.Undo); break;
            case Key.Y when command: Do(_session.Redo); break;
            default: HandleEditorKey(e); return;
        }
        e.Handled = true;
    }
}
