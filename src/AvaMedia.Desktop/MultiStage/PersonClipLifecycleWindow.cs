using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.PersonClipAdjust;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>The existing processing and adjustment bodies share a single durable task window.</summary>
internal sealed partial class PersonClipLifecycleWindow : MultiStageTaskWindow
{
    private readonly Job _job;
    private readonly TaskLifecycle _pages;
    private readonly IMediaEngine _engine;
    private readonly Func<Task> _save, _run;
    private readonly TaskPageViewModel _adjustPage, _mergePage;
    private readonly Grid _adjustSlot = new();
    private PersonClipAdjustWindow? _editor;
    private Control? _editorView;
    private bool _loading, _disposed;
    private string _folder;

    public PersonClipLifecycleWindow(Job job, IMultiStageTask task, IMultiStageToolContent content, IMediaEngine engine,
        string folder, Func<Task> save, Func<Task> run) : base(task, content, "保留有人片段", engine)
    {
        _job = job; _engine = engine; _folder = folder; _save = save; _run = run;
        _pages = new(job, PersonClipLifecycle.Pages);
        _adjustPage = new("adjust", () => { _ = LoadEditorAsync(); return _adjustSlot; });
        _mergePage = new("merge", BuildMerge);
        _format.SelectedItem = job.Options.PersonClip?.ExportPreset ?? SourceOutput.Original;
        if (_pages.Pages[2].Result?.Deserialize<PersonClipMergeChoice>() is { } choice)
        { _format.SelectedItem = choice.Preset; _transition.SelectedIndex = choice.Fade ? 1 : 0; }
        _folderButton = MsUi.Action("更改…", async () =>
        {
            if (await Ui.Folder(this, "输出位置") is { } selected) { _folder = selected; UpdatePages(); }
        });
        _folderButton.Classes.Add("link");
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
        _mergePage.Hint = merging ? "正在合并片段，关闭窗口后继续运行" : "原视频不会被修改 · 合并完成后进入“完成”";
        _mergePage.SecondaryAction = new("返回调整", async () => { _pages.Revise("adjust"); _job.State = JobState.AwaitingUser; await _save(); }, canEdit && merge.State == PageState.AwaitingUser);
        _mergePage.PrimaryAction = new("开始合并", StartMergeAsync, canEdit && merge.State == PageState.AwaitingUser);
        _progress.IsVisible = _progressText.IsVisible = merging || merge.State == PageState.Failed;
        _progress.Value = _job.Progress; _progressText.Text = merge.State == PageState.Failed ? merge.Detail : _job.ProgressDetail;
        if (_pages.Pages[1].Result is not null)
        {
            UpdateMergeSummary();
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
}
