using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop.MultiStage;

internal sealed class SubtitleLifecycleWindow : MultiStageTaskWindow
{
    private readonly Job _job;
    private readonly ISubtitleTask _task;
    private readonly TaskLifecycle _pages;
    private readonly Func<Task> _save, _run;
    private readonly TaskPageViewModel _choicePage, _generatePage;
    private readonly Border _fileCard, _videoCard;
    private readonly SegmentedControl _embed = new() { Items = { "软字幕", "硬字幕" }, SelectedIndex = 0 };
    private readonly ComboBox _format = new() { Classes = { "ac" }, ItemsSource = new[] { "SRT", "ASS" }, SelectedIndex = 0 };
    private readonly TextBlock _note = MsUi.Caption(), _path = MsUi.Caption(), _preview = MsUi.Caption(), _generating = MsUi.Heading("生成字幕");
    private readonly ProgressBar _progress = new() { Classes = { "ac" }, Minimum = 0, Maximum = 100 };
    private readonly StackPanel _options = new() { Spacing = 12 }, _choiceBody = new() { Spacing = 12 };
    private readonly Button _folderButton;
    private bool _video, _ready, _closed;
    private string _folder;

    public SubtitleLifecycleWindow(Job job, ISubtitleTask task, IMultiStageToolContent content, IMediaEngine engine,
        string folder, Func<Task> save, Func<Task> run) : base(task, content, "自动字幕", engine)
    {
        _job = job; _task = task; _folder = folder; _save = save; _run = run;
        _pages = new(job, SubtitleLifecycle.Pages);
        if (_pages.Pages[1].Result?.Deserialize<SubtitleOutputChoice>() is { } choice)
        { _video = choice.Mode != "file"; _embed.SelectedIndex = choice.Mode == "hard" ? 1 : 0; _format.SelectedIndex = choice.Format == "ass" ? 1 : 0; }
        _fileCard = ChoiceCard("导出字幕文件", "生成 SRT 或 ASS 文件", () => { _video = false; UpdatePages(); });
        _videoCard = ChoiceCard("嵌入到视频", "生成带字幕的新视频", () => { _video = true; UpdatePages(); });
        _folderButton = MsUi.Action("更改输出位置…", async () => { if (await Ui.Folder(this, "输出位置") is { } selected) { _folder = selected; UpdatePages(); } });
        _choiceBody.Children.Add(MsUi.Heading("选择字幕输出方式")); _choiceBody.Children.Add(_note);
        _choiceBody.Children.Add(MsUi.Columns("*,*", _fileCard, _videoCard));
        _options.Children.Add(MsUi.Columns("*,Auto", MsUi.Caption("字幕类型"), _embed));
        _options.Children.Add(MsUi.Columns("*,Auto", MsUi.Caption("字幕文件格式"), _format));
        _path.TextWrapping = TextWrapping.Wrap; _options.Children.Add(_path); _options.Children.Add(_folderButton);
        _options.Children.Add(MsUi.Caption("软字幕可开关；硬字幕会重新编码画面，字幕无法关闭。"));
        _preview.TextWrapping = TextWrapping.Wrap;
        _choiceBody.Children.Add(MsUi.Columns("*,*", MsUi.Card(new StackPanel { Spacing = 12, Children = { MsUi.Heading("识别结果预览"), _preview } }), MsUi.Card(_options)));
        _choicePage = new("choice", () => new ScrollViewer { Content = _choiceBody });
        _generatePage = new("generate", () => new StackPanel { Spacing = 12, Children = { _generating, _progress } });
        _embed.SelectionChanged += (_, _) => UpdatePages(); _format.SelectionChanged += (_, _) => UpdatePages();
        ContentRefreshed += (_, _) => UpdatePages(); ViewedPageChanged += (_, _) => UpdatePages();
        Closed += (_, _) => { _closed = true; _pages.Dispose(); };
        _ready = true;
        SetLifecycle(_pages, [ProcessingPage, _choicePage, _generatePage, ResultPage]);
        UpdatePages();
    }

    private static Border ChoiceCard(string title, string detail, Action choose)
    {
        var body = new StackPanel { Spacing = 6, Children = { MsUi.Heading(title), MsUi.Caption(detail) } };
        var button = new Button { Classes = { "ac", "text-action" }, Content = body, HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => choose();
        return MsUi.Card(button);
    }
    private SubtitleOutputChoice Choice => new(_video ? _embed.SelectedIndex == 0 ? "soft" : "hard" : "file", _format.SelectedIndex == 1 ? "ass" : "srt");
    private string PlannedOutput(SubtitleOutputChoice choice) => Path.Combine(_folder, Path.GetFileNameWithoutExtension(_job.Inputs[0]) + " [字幕]." + SubtitleLifecycle.OutputFormat(choice));
    private void UpdatePages()
    {
        if (!_ready || _closed) return;
        var state = _pages.Pages[1].State;
        if (state == PageState.Pending && _job.Options.Transcription is { } speech)
            _folder = speech.ReviewOutputToSource ? Path.GetDirectoryName(_job.Inputs[0])! : speech.ReviewOutputFolder ?? _folder;
        var editable = state == PageState.AwaitingUser && _job.State == JobState.AwaitingUser;
        var hasVideo = _task.View.Info?.HasVideo == true;
        if (!hasVideo) _video = false;
        if (_job.Options.Speed != 1) _embed.SelectedIndex = 1;
        foreach (var (card, selected) in new[] { (_fileCard, !_video), (_videoCard, _video) })
        {
            card.BorderThickness = new Thickness(2);
            MsUi.Brush(card, Border.BorderBrushProperty, selected ? "UiAccent" : "AcHairline");
            card.IsEnabled = editable && (card != _videoCard || hasVideo);
        }
        _options.IsEnabled = editable;
        _embed.IsVisible = _video; _embed.IsEnabled = _job.Options.Speed == 1;
        _format.IsVisible = !_video;
        _path.Text = PlannedOutput(Choice);
        _note.Text = Localization.Format($"识别已完成，共 {_task.View.Lines.Count} 条字幕。选择后进入“生成”。");
        _preview.Text = string.Join("\n\n", _task.View.Lines.Take(7).Select(line => $"{line.Number}   {MediaTime.Format(line.Start)} – {MediaTime.Format(line.End)}   {line.Text}"));
        _choicePage.Hint = state == PageState.Completed ? "已提交，可修改后重新生成" : "跳过时默认导出 SRT";
        _choicePage.SecondaryAction = state == PageState.Completed ? null : new("跳过", () => SubmitAsync(new()), editable);
        _choicePage.PrimaryAction = state == PageState.Completed
            ? new("修改", async () => { _pages.Revise("choice"); _job.State = JobState.AwaitingUser; await _save(); }, _job.State is not (JobState.Running or JobState.Waiting or JobState.Paused or JobState.Stopping))
            : new("继续", () => SubmitAsync(Choice), editable);
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
        var target = MediaEngine.UniqueOutput(_folder, Path.GetFileNameWithoutExtension(_job.Inputs[0]) + " [字幕]", format, [_job.Output, .. _job.Inputs]);
        _job.Output = target; _job.Options.Format = format;
        _job.Options.Transcription!.RecognitionOnly = false;
        _job.Options.Transcription.ReviewOutputFolder = _folder;
        _pages.SubmitInteraction("choice", choice);
        await _run();
    }
}
