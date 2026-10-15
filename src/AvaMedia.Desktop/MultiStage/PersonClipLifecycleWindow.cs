using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.PersonClipAdjust;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>The existing processing and adjustment bodies share a single durable task window.</summary>
internal sealed class PersonClipLifecycleWindow : MultiStageTaskWindow
{
    private readonly Job _job;
    private readonly TaskLifecycle _pages;
    private readonly IMediaEngine _engine;
    private readonly Func<Task> _save, _run;
    private readonly TaskPageViewModel _adjustPage, _mergePage;
    private readonly Grid _adjustSlot = new();
    private readonly StackPanel _mergeBody = new() { Spacing = 12 };
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly TextBlock _summary = MsUi.Heading(""), _expected = MsUi.Caption(), _stripNote = MsUi.Caption(), _progressText = MsUi.Caption();
    private readonly ProgressBar _progress = new() { Classes = { "ac" }, Minimum = 0, Maximum = 100 };
    private readonly SegmentedControl _transition = new() { Items = { "无", "淡入淡出" }, SelectedIndex = 0 };
    private readonly ComboBox _format = new() { Classes = { "ac" }, ItemsSource = QuickClipBatch.Presets };
    private readonly Button _folderButton;
    private readonly List<MsPreviewCard> _thumbs = [];
    private PersonClipAdjustWindow? _editor;
    private Control? _editorView;
    private bool _loading, _disposed;
    private int _stripPage;
    private string _folder;
    private string _stripKey = "";

    public PersonClipLifecycleWindow(Job job, IMultiStageTask task, IMultiStageToolContent content, IMediaEngine engine,
        string folder, Func<Task> save, Func<Task> run) : base(task, content, "保留有人片段", engine)
    {
        _job = job; _engine = engine; _folder = folder; _save = save; _run = run;
        _pages = new(job, PersonClipLifecycle.Pages);
        _adjustPage = new("adjust", () => { _ = LoadEditorAsync(); return _adjustSlot; });
        _mergePage = new("merge", BuildMerge);
        _format.SelectedItem = job.Options.PersonClip?.ExportPreset ?? SourceOutput.Original;
        _folderButton = MsUi.Action("更改输出位置…", async () =>
        {
            if (await Ui.Folder(this, "输出位置") is { } selected) { _folder = selected; UpdatePages(); }
        });
        ContentRefreshed += (_, _) => UpdatePages();
        ViewedPageChanged += (_, _) => { UpdatePages(); if (ViewedPageId != "adjust" && _editor is not null) _ = _editor.PauseEmbeddedAsync(); };
        _transition.SelectionChanged += (_, _) => UpdatePages();
        _format.SelectionChanged += (_, _) => UpdatePages();
        Closing += async (_, e) =>
        {
            if (_editor is null || _closingAfterFlush) return;
            e.Cancel = true;
            try { await _editor.FlushEmbeddedAsync(); _closingAfterFlush = true; Close(); }
            catch (Exception error) { _adjustPage.Hint = error.Message; RefreshPageActions(); }
        };
        Closed += (_, _) => { _disposed = true; _editor?.DisposeEditor(); _pages.Dispose(); ClearThumbs(); };
        SetLifecycle(_pages, [ProcessingPage, _adjustPage, _mergePage, ResultPage]);
        UpdatePages();
    }
    private bool _closingAfterFlush;

    private async Task LoadEditorAsync()
    {
        if (_loading || _editor is not null || _disposed) return;
        _loading = true;
        try
        {
            var session = await PersonClipEditing.OpenAsync(_job);
            if (_disposed) return;
            _editor = new(session, new(_engine, _folder, false, _job.Options.PersonClip?.ExportPreset ?? SourceOutput.Original,
                _ => Task.FromResult(false), _ => Task.CompletedTask));
            _editorView = _editor.Embed(this);
            _adjustSlot.Children.Clear(); _adjustSlot.Children.Add(_editorView);
            session.Changed += (_, _) => UpdatePages();
            UpdatePages();
        }
        catch (Exception error) { _adjustSlot.Children.Clear(); _adjustSlot.Children.Add(MsUi.Caption(error.Message)); }
        finally { _loading = false; }
    }

    private Control BuildMerge()
    {
        _mergeBody.Children.Add(_summary);
        var stripBody = new StackPanel { Spacing = 10 };
        var paging = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        paging.Children.Add(_stripNote);
        paging.Children.Add(MsUi.Action("上一页", () => { _stripPage = Math.Max(0, _stripPage - 1); UpdateStrip(); return Task.CompletedTask; }));
        paging.Children.Add(MsUi.Action("下一页", () => { if ((_stripPage + 1) * 8 < PersonClipLifecycle.Edit(_job).OutputSegments.Count) _stripPage++; UpdateStrip(); return Task.CompletedTask; }));
        stripBody.Children.Add(MsUi.HeadRow(MsUi.Heading("按顺序合并"), paging)); stripBody.Children.Add(_strip);
        _mergeBody.Children.Add(MsUi.Card(stripBody));
        var options = new StackPanel { Spacing = 12 };
        options.Children.Add(MsUi.Heading("合并选项"));
        options.Children.Add(MsUi.Columns("*,Auto", MsUi.Caption("转场"), _transition));
        options.Children.Add(MsUi.Columns("*,Auto", MsUi.Caption("输出格式"), _format));
        options.Children.Add(_folderButton);
        _mergeBody.Children.Add(MsUi.Card(options));
        _expected.TextWrapping = TextWrapping.Wrap;
        _mergeBody.Children.Add(MsUi.Heading("预计结果")); _mergeBody.Children.Add(_expected);
        _mergeBody.Children.Add(_progressText); _mergeBody.Children.Add(_progress);
        UpdatePages();
        return new ScrollViewer { Content = _mergeBody };
    }

    private void UpdatePages()
    {
        if (_disposed) return;
        var adjust = _pages.Pages[1]; var merge = _pages.Pages[2];
        if (_pages.Pages[0].State != PageState.Completed && _editor is not null)
        {
            _editor.DisposeEditor(); _editor = null; _editorView = null; _adjustSlot.Children.Clear();
        }
        if (ViewedPageId == "adjust" && _pages.Pages[0].State == PageState.Completed && _editor is null && !_loading)
            _ = LoadEditorAsync();
        var canEdit = _job.State == JobState.AwaitingUser;
        if (_editorView is not null) _editorView.IsEnabled = canEdit && adjust.State == PageState.AwaitingUser;
        _adjustPage.Hint = adjust.State == PageState.Completed ? "已提交，可修改后重新合并" : "调整会自动保存到任务";
        _adjustPage.SecondaryAction = adjust.State == PageState.Completed ? null : new("恢复检测结果", () => _editor?.RestoreEmbeddedAsync() ?? Task.CompletedTask, _editor is not null && canEdit);
        _adjustPage.PrimaryAction = adjust.State == PageState.Completed
            ? new("修改", async () => { _pages.Revise("adjust"); _job.State = JobState.AwaitingUser; await _save(); }, _job.State is not (JobState.Running or JobState.Waiting or JobState.Paused or JobState.Stopping))
            : new("下一步：合并确认", async () =>
            {
                var edit = await _editor!.SubmitEmbeddedAsync();
                _pages.SubmitInteraction("adjust", edit); await _save();
            }, _editor is not null && canEdit && adjust.State == PageState.AwaitingUser);
        var merging = merge.State == PageState.Running || _job.State == JobState.Waiting && _pages.CurrentPage.Page.Id == "merge";
        _transition.IsEnabled = _format.IsEnabled = _folderButton.IsEnabled = canEdit && merge.State == PageState.AwaitingUser;
        _mergePage.Hint = merging ? "正在合并片段，关闭窗口后继续运行" : "合并完成后进入“完成”";
        _mergePage.SecondaryAction = new("返回调整", async () => { _pages.Revise("adjust"); _job.State = JobState.AwaitingUser; await _save(); }, canEdit && merge.State == PageState.AwaitingUser);
        _mergePage.PrimaryAction = new("开始合并", StartMergeAsync, canEdit && merge.State == PageState.AwaitingUser);
        _progress.IsVisible = _progressText.IsVisible = merging || merge.State == PageState.Failed;
        _progress.Value = _job.Progress; _progressText.Text = merge.State == PageState.Failed ? merge.Detail : _job.ProgressDetail;
        if (_pages.Pages[1].Result is not null)
        {
            var edit = PersonClipLifecycle.Edit(_job);
            var seconds = edit.OutputSegments.Sum(s => (s.End - s.Start) / s.Speed);
            _summary.Text = Localization.Format($"保留 {edit.OutputSegments.Count} 段 · 合并后 {MediaTime.Format(seconds)} / 原片 {MediaTime.Format(edit.Info.Duration)}");
            var estimated = _transition.SelectedIndex == 0 && _format.SelectedItem as string == "Fast Copy" && File.Exists(edit.Path)
                ? " · " + MsUi.Size((long)(new FileInfo(edit.Path).Length * seconds / edit.Info.Duration)) : "";
            _expected.Text = Localization.Join("\n", [Path.Combine(_folder, Path.GetFileNameWithoutExtension(edit.Path) + " [保留片段]." + SourceOutput.VideoFormat(_format.SelectedItem as string == "Fast Copy" ? SourceOutput.Original : (_format.SelectedItem as string ?? SourceOutput.Original).ToLowerInvariant(), edit.Path)),
                _transition.SelectedIndex == 1 ? "淡入淡出 · 重新编码" : "无转场", "预计时长 " + MediaTime.Format(seconds) + estimated, "输出位置：" + _folder]);
            if (ViewedPageId == "merge") UpdateStrip();
        }
        RefreshPageActions();
    }
    private async Task StartMergeAsync()
    {
        var choice = new PersonClipMergeChoice(_format.SelectedItem as string ?? SourceOutput.Original, _transition.SelectedIndex == 1);
        var draft = PersonClipLifecycle.PrepareMerge(_job, choice, _folder, [_job.Output]);
        var detection = _job.Options.PersonClip!;
        _job.Output = draft.Output; _job.Options.Format = draft.Options.Format; _job.Options.CopyStreams = draft.Options.CopyStreams;
        _job.Options.PersonClip = detection with { AnalysisOnly = false, ExportPreset = choice.Fade && choice.Preset == "Fast Copy" ? SourceOutput.Original : choice.Preset };
        _pages.QueueInteractionWork("merge", choice);
        await _run();
    }
    private void ClearThumbs() { foreach (var preview in _thumbs) preview.Dispose(); _thumbs.Clear(); _strip.Children.Clear(); }
    private void UpdateStrip()
    {
        var edit = PersonClipLifecycle.Edit(_job); var ranges = edit.OutputSegments;
        _stripPage = Math.Clamp(_stripPage, 0, Math.Max(0, (ranges.Count - 1) / 8));
        var key = $"{_pages.Snapshot.Revision}:{_stripPage}";
        if (_stripKey == key) return; _stripKey = key;
        ClearThumbs();
        foreach (var (segment, i) in ranges.Skip(_stripPage * 8).Take(8).Select((segment, i) => (segment, i)))
        {
            var preview = new MsPreviewCard { Width = 116, FollowSourceAspect = true };
            preview.ShowFrame(_engine, edit.Path, segment.Start);
            preview.SetCaption(($"{_stripPage * 8 + i + 1} · {segment.End - segment.Start:0.#} 秒"), MediaTime.Format(segment.Start));
            _thumbs.Add(preview); _strip.Children.Add(preview);
        }
        _stripNote.Text = Localization.Format($"第 {_stripPage * 8 + 1}–{Math.Min(ranges.Count, (_stripPage + 1) * 8)} 段 / 共 {ranges.Count} 段");
    }
}
