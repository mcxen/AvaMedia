using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public partial class JobRowView : UserControl
{
    // Limit background FFmpeg processes even when many rows become visible at once.
    private static readonly SemaphoreSlim PreviewSlots = new(2);
    private MainWindow? _owner;
    private JobRowDetails? _details;
    private CancellationTokenSource? _load;
    private PreviewKey? _key;
    public Task Ready { get; private set; } = Task.CompletedTask;
    public JobRowDetails? Details => _details;
    public JobRowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindJob();
        AttachedToVisualTree += (_, _) => BindJob();
        DetachedFromVisualTree += (_, _) => Release();
    }
    private void BindJob()
    {
        Release();
        _owner = this.GetVisualAncestors().OfType<MainWindow>().FirstOrDefault();
        if (_owner is null || DataContext is not Job job) return;
        _details = new(job);
        RowRoot.DataContext = _details;
        job.PropertyChanged += JobChanged;
        _owner.JobDisplayChanged += Refresh;
        Refresh();
    }
    private void JobChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (ReferenceEquals(sender, _details?.Job)) Refresh();
    });
    private void Refresh()
    {
        if (_details is null || _owner is null) return;
        _details.Refresh();
        foreach (var state in Enum.GetValues<JobState>()) StateText.Classes.Set(state.ToString().ToLowerInvariant(), state == _details.Job.State);
        var job = _details.Job;
        var path = job.FeatureId is "download" or "record" ? job.State == JobState.Completed ? job.Output : "" : job.Inputs.FirstOrDefault() ?? "";
        var o = job.InputOptions?.FirstOrDefault() ?? job.Options;
        long modified = 0;
        try { if (File.Exists(path)) modified = File.GetLastWriteTimeUtc(path).Ticks; } catch (IOException) { }
        var key = new PreviewKey(path, modified, o.VideoStreamIndex, o.AudioStreamIndex, o.Start, o.End, _owner.Engine.Settings.FFmpegPath, _owner.Engine.Settings.FFprobePath);
        if (key == _key) return;
        _key = key;
        _load?.Cancel(); _load = null;
        _details.SetMedia(null, "");
        var feature = Catalog.Find(job.FeatureId);
        if (path.Length == 0) { _details.SetMedia(null, job.FeatureId == "download" ? "下载完成后读取媒体信息" : "等待生成媒体"); return; }
        if (!File.Exists(path)) { _details.SetMedia(null, "源文件缺失或不可访问"); return; }
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (feature.Category is "文档" or "光驱设备\\DVD\\CD\\ISO" || !(QuickClipBatch.VideoExtensions.Contains(extension) || MediaEngine.IsAudio(extension) || MediaEngine.IsImage(extension) || extension is "jpeg" or "tif" or "gif"))
        { _details.SetMedia(null, "文件任务"); return; }
        _details.SetMedia(null, "正在读取媒体信息…");
        _load = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Ready = LoadAsync(_details, _owner.Engine, key, _load);
    }
    private async Task LoadAsync(JobRowDetails details, IMediaEngine engine, PreviewKey key, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        bool acquired = false;
        MediaInfo? media = null;
        try
        {
            await PreviewSlots.WaitAsync(token); acquired = true;
            media = await engine.Probe(key.Path, token, key.Video, key.Audio);
            byte[]? cover = null;
            if (media.HasVideo)
            {
                var end = key.End > key.Start ? Math.Min(key.End, media.Duration) : media.Duration;
                var position = media.Duration > 0 ? Math.Clamp(key.Start + Math.Min(1, Math.Max(0, end - key.Start) * .1), 0, Math.Max(0, media.Duration - .05)) : 0;
                cover = await engine.Thumbnail(key.Path, position, 228, 144, token, pad: false, videoStreamIndex: key.Video);
            }
            if (!token.IsCancellationRequested && ReferenceEquals(details, _details)) details.SetMedia(media, "", cover);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(details, _details) && ReferenceEquals(cancellation, _load)) details.SetMedia(media, "媒体信息读取超时");
        }
        catch (Exception)
        {
            // Inspection failures never change the job state or prevent conversion.
            if (!token.IsCancellationRequested && ReferenceEquals(details, _details)) details.SetMedia(media, "媒体信息不可用，请检查外部工具或源文件");
        }
        finally
        {
            if (acquired) PreviewSlots.Release();
            if (ReferenceEquals(_load, cancellation)) _load = null;
            cancellation.Dispose();
        }
    }
    private async void CoverClick(object? sender, RoutedEventArgs e)
    { e.Handled = true; if (_owner is not null && _details is not null) await _owner.EditJob(_details.Job); }
    private void Release()
    {
        _load?.Cancel(); _load = null; _key = null;
        if (_owner is not null) _owner.JobDisplayChanged -= Refresh;
        if (_details is not null) _details.Job.PropertyChanged -= JobChanged;
        RowRoot.DataContext = null;
        _details?.Dispose(); _details = null; _owner = null;
    }
    private sealed record PreviewKey(string Path, long Modified, int Video, int Audio, double Start, double End, string FFmpeg, string FFprobe);
}
