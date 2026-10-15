using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop.MultiStage;

internal sealed class OrientationLifecycleWindow : MultiStageTaskWindow
{
    private readonly IMediaEngine _previewEngine;
    private readonly Job _job;
    private readonly IOrientationTask _task;
    private readonly TaskLifecycle _pages;
    private readonly TaskPageViewModel _choicePage, _exportPage;
    private readonly ComboBox _angle = new() { Classes = { "ac" }, ItemsSource = new[] { 0, 90, 180, 270 }, SelectedIndex = 0 },
        _format = new() { Classes = { "ac" }, ItemsSource = new[] { SourceVideoExport.Original, SourceVideoExport.FastRotation, "mp4", "mkv", "webm", "mov", "avi", "ts" }, SelectedIndex = 0 };
    private readonly MsPreviewCard _preview = new();
    private readonly TextBlock _note = MsUi.Caption(), _path = MsUi.Caption(), _progressText = MsUi.Caption();
    private readonly ProgressBar _progress = new() { Classes = { "ac" }, Minimum = 0, Maximum = 100 };
    private readonly StackPanel _fields = new() { Spacing = 12 };
    private readonly Func<Task> _save, _run;
    private readonly Func<IEnumerable<string>> _reserved;
    private string _folder;
    private bool _initialized, _disposed;

    internal OrientationLifecycleWindow(Job job, IOrientationTask task, IMultiStageToolContent content, IMediaEngine engine,
        Func<Task> save, Func<Task> run, Func<IEnumerable<string>> reserved) : base(task, content, "自动旋转", engine)
    {
        _previewEngine = engine; _job = job; _task = task; _save = save; _run = run; _reserved = reserved;
        var spec = job.Options.Orientation!;
        _folder = spec.OutputToSource ? Path.GetDirectoryName(job.Inputs[0])! : spec.OutputFolder;
        _format.SelectedItem = spec.Format;
        _pages = new(job, OrientationLifecycle.Pages);
        _angle.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<int>((value, _) => MsUi.Caption(BatchRotate.Direction(value)));
        _fields.Children.Add(MsUi.Heading("确认旋转方向")); _fields.Children.Add(_note);
        _fields.Children.Add(MsUi.Columns("Auto,*", MsUi.Caption("旋转方向"), _angle));
        _fields.Children.Add(MsUi.Columns("Auto,*", MsUi.Caption("输出格式"), _format));
        _path.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _fields.Children.Add(_path);
        _fields.Children.Add(Ui.Button("更改输出位置…", async () =>
        { if (await Ui.Folder(this, "输出位置") is { } folder) { _folder = folder; UpdatePages(); } }));
        var choice = MsUi.Columns("*,*", _preview, MsUi.Card(_fields));
        _choicePage = new("choice", () => new ScrollViewer { Content = choice });
        _exportPage = new("export", () => new StackPanel { Spacing = 12, Children = { MsUi.Heading("旋转导出"), _progressText, _progress } });
        _angle.SelectionChanged += (_, _) => { _preview.Rotation = _angle.SelectedItem is int angle ? angle : 0; };
        _format.SelectionChanged += (_, _) => UpdatePages();
        ContentRefreshed += (_, _) => UpdatePages();
        ViewedPageChanged += (_, _) => UpdatePages();
        Closed += (_, _) => { _disposed = true; _pages.Dispose(); _preview.Dispose(); };
        SetLifecycle(_pages, [ProcessingPage, _choicePage, _exportPage, ResultPage]);
        UpdatePages();
    }
    private void UpdatePages()
    {
        if (_disposed || _choicePage is null) return;
        var view = _task.View;
        if (!_initialized && view.HasResult)
        {
            _initialized = true; _angle.SelectedItem = view.Rotation ?? 0;
            _preview.ShowFrame(_previewEngine, view.SourcePath, 0, view.Info?.VideoStreamIndex ?? 0);
            _preview.Rotation = view.Rotation ?? 0;
            _preview.SetCaption("校正后预览", view.Decision);
        }
        _note.Text = Localization.Join(" · ", [view.Decision, view.Reason]);
        _path.Text = _folder;
        var editable = _job.State == JobState.AwaitingUser && _pages.Pages[1].State == PageState.AwaitingUser;
        _fields.IsEnabled = editable;
        _choicePage.Hint = "原视频不会被修改";
        _choicePage.PrimaryAction = new("开始导出", SubmitAsync, editable && view.HasResult);
        _choicePage.SecondaryAction = new("关闭", () => { Close(); return Task.CompletedTask; });
        _exportPage.Hint = _job.ProgressDetail;
        _exportPage.SecondaryAction = new("关闭", () => { Close(); return Task.CompletedTask; });
        _progress.Value = _job.Progress; _progressText.Text = _job.State == JobState.Failed ? _job.Error : Localization.Format($"旋转导出 · {_job.Progress:0}%");
        RefreshPageActions();
    }
    private async Task SubmitAsync()
    {
        var choice = new OrientationOutputChoice((int)(_angle.SelectedItem ?? 0), (string)(_format.SelectedItem ?? SourceVideoExport.Original));
        var format = SourceVideoExport.Format(choice.Format, _job.Inputs[0]);
        _job.Output = MediaEngine.UniqueOutput(_folder, Path.GetFileNameWithoutExtension(_job.Inputs[0]) + "_rotate" + choice.Rotation,
            format, _reserved().Concat(_job.Inputs).Append(_job.Output));
        _pages.SubmitInteraction("choice", choice);
        await _save(); await _run();
    }
}
