using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Controls;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop.MultiStage;

internal sealed class SubtitleLifecycleWindow : MultiStageTaskWindow
{
    private readonly Job _job;
    private readonly ISubtitleTask _task;
    private readonly TaskLifecycle _pages;
    private readonly Func<Task> _save, _run;
    private readonly TaskPageViewModel _choicePage, _generatePage;
    private readonly SubtitleOutputEditor _outputOptions;
    private readonly TextBlock _note = MsUi.Caption(), _path = MsUi.Caption(), _preview = MsUi.Caption(), _generating = MsUi.Heading("生成字幕");
    private readonly ProgressBar _progress = new() { Classes = { "ac" }, Minimum = 0, Maximum = 100 };
    private readonly StackPanel _options = new() { Spacing = 12 }, _choiceBody = new() { Spacing = 12 };
    private readonly Button _folderButton;
    private bool _ready, _closed;
    private string _folder;

    public SubtitleLifecycleWindow(Job job, ISubtitleTask task, IMultiStageToolContent content, IMediaEngine engine,
        string folder, Func<Task> save, Func<Task> run) : base(task, content, "自动字幕", engine)
    {
        _job = job; _task = task; _folder = folder; _save = save; _run = run;
        _pages = new(job, SubtitleLifecycle.Pages);
        var choice = _pages.Pages[1].Result?.Deserialize<SubtitleOutputChoice>();
        _outputOptions = new(job.Options, choice is null ? null : new(choice.Mode != "file", choice.Format));
        _folderButton = MsUi.Action("更改输出位置…", async () => { if (await Ui.Folder(this, "输出位置") is { } selected) { _folder = selected; UpdatePages(); } });
        _choiceBody.Children.Add(MsUi.Heading("选择字幕输出方式")); _choiceBody.Children.Add(_note);
        _path.TextWrapping = TextWrapping.Wrap; _options.Children.Add(_path); _options.Children.Add(_folderButton);
        _preview.TextWrapping = TextWrapping.Wrap;
        var inspector = new Grid { RowDefinitions = new("*,Auto"), MinHeight = 480 };
        inspector.Children.Add(_outputOptions); Grid.SetRow(_options, 1); inspector.Children.Add(_options);
        _choiceBody.Children.Add(MsUi.Columns("*,*", MsUi.Card(new StackPanel { Spacing = 12, Children = { MsUi.Heading("识别结果预览"), _preview } }), MsUi.Card(inspector)));
        _choicePage = new("choice", () => new ScrollViewer { Content = _choiceBody });
        _generatePage = new("generate", () => new StackPanel { Spacing = 12, Children = { _generating, _progress } });
        _outputOptions.Changed += UpdatePages;
        ContentRefreshed += (_, _) => UpdatePages(); ViewedPageChanged += (_, _) => UpdatePages();
        Closed += (_, _) => { _closed = true; _pages.Dispose(); _outputOptions.Dispose(); };
        _ready = true;
        SetLifecycle(_pages, [ProcessingPage, _choicePage, _generatePage, ResultPage]);
        UpdatePages();
    }

    private SubtitleOutputChoice Choice => _outputOptions.Choice;
    private string PlannedOutput(SubtitleOutputChoice choice) => Path.Combine(_folder, Path.GetFileNameWithoutExtension(_job.Inputs[0]) + " [字幕]." + SubtitleLifecycle.OutputFormat(choice));
    private void UpdatePages()
    {
        if (!_ready || _closed) return;
        var state = _pages.Pages[1].State;
        if (state == PageState.Pending && _job.Options.Transcription is { } speech)
            _folder = speech.ReviewOutputToSource ? Path.GetDirectoryName(_job.Inputs[0])! : speech.ReviewOutputFolder ?? _folder;
        var editable = state == PageState.AwaitingUser && _job.State == JobState.AwaitingUser;
        var hasVideo = _task.View.Info?.HasVideo != false;
        _outputOptions.SetVideoAvailable(hasVideo);
        _outputOptions.IsEnabled = editable;
        _options.IsEnabled = editable;
        _path.Text = PlannedOutput(Choice);
        _note.Text = Localization.Format($"识别已完成，共 {_task.View.Lines.Count} 条字幕。选择后进入“生成”。");
        _preview.Text = string.Join("\n\n", _task.View.Lines.Take(7).Select(line => $"{line.Number}   {MediaTime.Format(line.Start)} – {MediaTime.Format(line.End)}   {line.Text}"));
        _choicePage.Hint = state == PageState.Completed ? "已提交，可修改后重新生成" : "跳过时默认导出 SRT";
        _choicePage.SecondaryAction = state == PageState.Completed ? null : new("跳过", () => SubmitAsync(new()), editable);
        _choicePage.PrimaryAction = state == PageState.Completed
            ? new("修改", async () => { _pages.Revise("choice"); _job.State = JobState.AwaitingUser; await _save(); }, _job.State is not (JobState.Running or JobState.Waiting or JobState.Paused or JobState.Stopping))
            : new(_outputOptions.ActionText, () => SubmitAsync(Choice), editable);
        _generating.Text = _pages.Pages[2].State == PageState.Failed ? _pages.Pages[2].Detail
            : Localization.Format($"生成字幕 · {_job.Progress:0}%");
        _progress.Value = _job.Progress;
        _generatePage.Hint = _job.ProgressDetail;
        _generatePage.SecondaryAction = new("关闭", () => { Close(); return Task.CompletedTask; });
        RefreshPageActions();
    }
    private async Task SubmitAsync(SubtitleOutputChoice choice)
    {
        var format = SubtitleLifecycle.OutputFormat(choice);
        if (choice == Choice) _outputOptions.ReadInto(_job.Options);
        var target = MediaEngine.UniqueOutput(_folder, Path.GetFileNameWithoutExtension(_job.Inputs[0]) + " [字幕]", format, [_job.Output, .. _job.Inputs]);
        _job.Output = target; _job.Options.Format = format;
        _job.Options.Transcription!.RecognitionOnly = false;
        _job.Options.Transcription.ReviewOutputFolder = _folder;
        _pages.SubmitInteraction("choice", choice);
        await _run();
    }
}
