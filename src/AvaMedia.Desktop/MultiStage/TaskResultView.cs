using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Controls;
using AvaMedia.Core;

namespace AvaMedia.Desktop.MultiStage;

/// <summary>Shared result page. Preview and metadata are read from the produced file, never the input.</summary>
internal sealed class TaskResultView : ScrollViewer, IDisposable
{
    private readonly IMediaEngine? _engine;
    private readonly MsPreviewCard _preview = new() { FollowSourceAspect = true, MaxWidth = 760 };
    private readonly StackPanel _files = new() { Spacing = 8 }, _history = new() { Spacing = 12 };
    private readonly TextBlock _detail = MsUi.Caption();
    private readonly CancellationTokenSource _lifetime = new();
    private string _path = "";
    private bool _disposed;
    public Button RevealAction { get; }

    public TaskResultView(IMediaEngine? engine)
    {
        _engine = engine;
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(_preview); body.Children.Add(_files); body.Children.Add(_detail);
        body.Children.Add(MsUi.Heading("处理记录")); body.Children.Add(MsUi.Card(_history));
        Content = body;
        RevealAction = MsUi.Action(OperatingSystem.IsMacOS() ? "在访达中显示" : "在文件夹中显示", () =>
        {
            if (File.Exists(_path)) PlatformServices.RevealFile(_path);
            else if (Directory.Exists(_path)) PlatformServices.OpenFolder(_path);
            return Task.CompletedTask;
        });
    }

    public void Update(IReadOnlyList<string> paths, IReadOnlyList<TaskPageStatus> pages, string note)
    {
        _history.Children.Clear();
        foreach (var (page, index) in pages.Select((page, index) => (page, index)))
        {
            var text = new StackPanel { Spacing = 3 };
            text.Children.Add(MsUi.Heading(page.Page.Title));
            text.Children.Add(MsUi.Caption(Localization.Join(" · ", new[] { page.Detail,
                page.Elapsed > TimeSpan.Zero ? Localization.Format($"{page.Elapsed.TotalSeconds:0.#} 秒") : "" }.Where(s => s.Length > 0))));
            _history.Children.Add(MsUi.Columns("Auto,*", new StageGlyph { Number = index + 1, Width = 18, Height = 18,
                State = page.State == PageState.Completed ? "done" : page.State == PageState.Failed ? "failed" : "pending" }, text));
        }
        if (_path == paths.FirstOrDefault() && _files.Children.OfType<MsFileChip>().Select(c => c.Path).SequenceEqual(paths)) return;
        _path = paths.FirstOrDefault() ?? "";
        _files.Children.Clear();
        foreach (var path in paths) { var chip = new MsFileChip(_engine) { HorizontalAlignment = HorizontalAlignment.Left }; chip.SetFile(path); _files.Children.Add(chip); }
        _detail.Text = note;
        _preview.IsVisible = false;
        if (_path.Length > 0) _ = LoadPreviewAsync(_path);
    }

    private async Task LoadPreviewAsync(string path)
    {
        try
        {
            if (_engine is null || !File.Exists(path)) return;
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (!QuickClipBatch.VideoExtensions.Contains(ext) && !MediaEngine.IsImage(ext) && !MediaEngine.IsAudio(ext)) return;
            var info = await _engine.Probe(path, _lifetime.Token);
            if (_disposed || _path != path) return;
            _detail.Text = MsUi.MediaDetail(info, MsFileChip.FileSize(path));
            if (info.Width <= 0 || info.Height <= 0) return;
            _preview.IsVisible = true;
            if (MediaEngine.IsImage(ext)) _preview.ShowFile(path); else _preview.ShowFrame(_engine, path, 0);
            _preview.SetCaption(Path.GetFileName(path), _detail.Text ?? "");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppDiagnostics.Record("Task result preview", error); }
    }

    public void Dispose() { _disposed = true; _lifetime.Cancel(); _preview.Dispose(); _lifetime.Dispose(); }
}
