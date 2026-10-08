using System.Globalization;
using Avalonia.Media.Imaging;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

// Display-only data stays out of the persisted queue and the conversion engine.
public sealed class JobRowDetails(Job job) : Observable, IDisposable
{
    private MediaInfo? _media;
    private string _inspection = "";
    private Bitmap? _cover;
    private string _fileSummary = "";
    private string _outputSize = "";
    private string[]? _sizeInputs;
    private string _sizeOutput = "";
    private long? _inputBytes;
    private long _outputBytes = -1;
    private CancellationTokenSource? _metadata;
    private bool _disposed;
    public Task MetadataReady { get; private set; } = Task.CompletedTask;
    public Job Job { get; } = job;
    public Bitmap? Cover => _cover;
    public bool HasCover => Cover is not null;
    public bool NoCover => !HasCover;
    public bool IsRunning => Job.State == JobState.Running;
    public bool CanPreview => !IsRunning;
    public string PreviewTip => IsRunning ? "任务正在运行" : "编辑任务";
    public string Icon => Catalog.Find(Job.FeatureId).Icon;
    public string Extension => Path.GetExtension(Job.Inputs.FirstOrDefault() ?? "").TrimStart('.').ToUpperInvariant();
    public string Name => Job.FeatureId == "download" ? Job.Name : Job.Inputs.Length == 0 ? Localization.Text(Catalog.Find(Job.FeatureId).Label.Replace("\n", " ")) : Path.GetFileName(Job.Inputs[0]) + (Job.Inputs.Length > 1 ? "  +" + Localization.Format($"{Job.Inputs.Length - 1} 个文件") : "");
    public string OutputName => Path.GetFileName(Job.Output.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    public string FileSummary => _fileSummary;
    public string MediaSummary
    {
        get
        {
            if (_media is null) return _inspection;
            var parts = new List<string>();
            if (_media.Duration > 0) parts.Add(Time(_media.Duration));
            if (_media.HasVideo && _media.Width > 0 && _media.Height > 0) parts.Add($"{_media.Width} × {_media.Height}");
            if (_media.HasVideo && _media.FrameRate > 0 && _media.Duration > 0) parts.Add(Number(_media.FrameRate) + " fps");
            if (parts.Count == 0) parts.Add("媒体信息已读取");
            return Job.Inputs.Length > 1 ? Localization.Format($"首个：{Localization.Join(" · ", parts)}") : Localization.Join(" · ", parts);
        }
    }
    public string CodecSummary
    {
        get
        {
            if (_media is null) return "";
            var parts = new List<string>();
            if (_media.HasVideo) parts.Add(Codec(_media.VideoCodec));
            if (_media.HasAudio)
            {
                parts.Add(Codec(_media.AudioCodec));
                if (_media.AudioSampleRate > 0) parts.Add(Number(_media.AudioSampleRate / 1000d) + " kHz");
                if (_media.AudioChannels > 0) parts.Add(_media.AudioChannels switch { 1 => "单声道", 2 => "立体声", _ => Localization.Format($"{_media.AudioChannels} 声道") });
            }
            else if (_media.HasVideo && _media.Duration > 0) parts.Add("无音轨");
            return Localization.Join(" · ", parts.Where(p => p.Length > 0));
        }
    }
    public bool HasCodecSummary => CodecSummary.Length > 0;
    public string SourceTip => string.Join("\n", new[] { Job.Source, MediaSummary, CodecSummary, FileSummary }.Where(s => s.Length > 0));
    public string SettingsSummary
    {
        get
        {
            var feature = Catalog.Find(Job.FeatureId);
            var o = Job.Options;
            var label = feature.Label.StartsWith('→') ? o.Format.ToUpperInvariant() : feature.Label.Replace("\n", " ");
            var parts = new List<string> { label };
            if (o.VideoCompression is { } videoCompression)
            {
                parts.Add(videoCompression.Mode switch
                {
                    VideoCompressionMode.Automatic => Localization.Join(" · ", ["自动档", videoCompression.Preset switch
                        { VideoCompressionPreset.High => "高 · 画质优先", VideoCompressionPreset.Small => "低 · 体积优先", _ => "中 · 均衡（推荐）" }]),
                    VideoCompressionMode.Quality => Localization.Format($"质量 {videoCompression.Quality}"),
                    VideoCompressionMode.Bitrate => Localization.Format($"视频 {videoCompression.VideoBitrate} kbps"),
                    VideoCompressionMode.Percentage => Localization.Format($"目标 {videoCompression.Percentage:0.#}%"),
                    _ => Localization.Format($"目标 {videoCompression.TargetMegabytes:0.##} MB")
                });
                parts.Add(videoCompression.Codec == "hevc" ? "HEVC" : "H.264");
                parts.Add(videoCompression.Format.ToUpperInvariant());
                return Localization.Join(" · ", parts);
            }
            if (feature.Operation == Operation.ImageCompress && o.ImageCompression is { } compression)
            {
                parts.Add(compression.Format.ToUpperInvariant());
                parts.Add(compression.Format == "png" || compression.Lossless ? "无损编码" : Localization.Format($"质量 {compression.Quality}"));
                if (compression.MaxDimension > 0) parts.Add(Localization.Format($"最长边 {compression.MaxDimension} px"));
            }
            else if (o.Pdf is {} pdf)
            {
                parts.Add(Localization.Format($"{pdf.Pages.Count} 页"));
                if(feature.Operation==Operation.PdfSplit)parts.Add(Localization.Format($"{PdfTools.Groups(pdf).Count} 个 PDF"));
                if(feature.Operation==Operation.PdfAge)parts.Add(Localization.Format($"做旧 {pdf.Age} · 折痕 {pdf.Folds} · 污渍 {pdf.Stains}"));
                if(feature.Operation==Operation.PdfCompress)parts.Add(pdf.Rasterize?"整页压缩":"保留文字");
            }
            else if (feature.Operation == Operation.Download)
            {
                parts.Add(o.Format.ToUpperInvariant());
            }
            else if (feature.Operation == Operation.Transcribe)
            {
                parts.Add(o.Format.ToUpperInvariant());
                parts.Add(o.Transcription?.Model == SpeechModel.Tiny ? "轻量模型" : "标准模型");
                if (o.Format is "mp4" or "mkv" or "ass") parts.Add(Localization.Format($"字号 {o.SubtitleFontSize}"));
            }
            else if (feature.Category is "视频" or "音频" or "图片")
            {
                if (!label.Equals(o.Format, StringComparison.OrdinalIgnoreCase)) parts.Add(o.Format.ToUpperInvariant());
                if (feature.Operation == Operation.Frames) parts.Add(Localization.Format($"每 {Number(o.FrameInterval)} 秒一帧"));
                else if (o.LosslessRotation is { } direction) parts.Add(Localization.Format($"Fast Copy · 方向标记 {direction}°"));
                else if (o.PreserveSourceAttributes) parts.Add("原视频编码 · 其他轨道直拷");
                else if (o.CopyStreams) parts.Add("直接复制流");
                else
                {
                    if (!MediaEngine.IsAudio(o.Format) && !MediaEngine.IsImage(o.Format)) parts.Add(o.VideoCodec == "自动" ? "自动编码" : Codec(o.VideoCodec));
                    if (o.Width > 0 || o.Height > 0) parts.Add(Localization.Format($"{(o.Width > 0 ? o.Width.ToString() : Localization.Text("自动"))} × {(o.Height > 0 ? o.Height.ToString() : Localization.Text("自动"))}"));
                    if (o.Fps > 0) parts.Add(Number(o.Fps) + " fps");
                    if (o.Format is not ("flac" or "wav" or "aiff") && MediaEngine.IsAudio(o.Format)) parts.Add(o.AudioBitrate + " kbps");
                }
                if (o.Start > 0 || o.End > 0) parts.Add(Localization.Format($"截取 {Time(o.Start, true)}–{(o.End > 0 ? Time(o.End, true) : Localization.Text("结尾"))}"));
                if (o.Speed != 1) parts.Add(Localization.Format($"{Number(o.Speed)}× 速度"));
                if (o.Rotation != 0) parts.Add(Localization.Format($"旋转 {o.Rotation}°"));
                if (o.CropWidth > 0) parts.Add(Localization.Format($"裁剪 {o.CropWidth} × {o.CropHeight}"));
                if (o.Mute && !MediaEngine.IsImage(o.Format)) parts.Add("静音");
                if (o.SubtitleMode == SubtitleMode.BurnIn) parts.Add("烧录字幕");
                if (o.VoiceEnhancement) parts.Add(Localization.Format($"人声增强 {o.VoiceEnhancementStrength}%"));
                if (Job.InputOptions?.Count > 0) parts.Add("逐文件编辑");
            }
            return Localization.Join(" · ", parts);
        }
    }
    public string StateText => Job.State switch
    {
        JobState.Waiting => "等待开始",
        JobState.Running => Localization.Format($"{Localization.Key(Job.FeatureId == "download" ? "下载中" : "处理中")}  {Job.Progress:0.0}%"),
        JobState.Completed => Localization.Join(" · ", new[] { "已完成", _outputSize, Job.Options.VideoCompression is not null || Job.Options.ImageCompression is not null || Job.Options.Pdf is not null || Job.Options.Transcription is not null ? Job.ProgressDetail : "" }.Where(s=>s.Length>0)),
        JobState.Failed => "失败",
        _ => "已停止"
    };
    public string StateDetail => Job.State switch
    {
        JobState.Running => Job.Status.IndexOf(" · ", StringComparison.Ordinal) is >= 0 and var separator ? Job.Status[(separator + 3)..] : "",
        JobState.Failed => string.IsNullOrWhiteSpace(Job.Error) ? "右键查看日志" : Job.Error.Trim(),
        JobState.Cancelled => "可右键重试任务",
        _ => ""
    };
    public bool HasStateDetail => StateDetail.Length > 0;
    public void Refresh()
    {
        if (_disposed) return;
        var output = Job.State == JobState.Completed ? Job.Output : "";
        if (!ReferenceEquals(_sizeInputs, Job.Inputs) || _sizeOutput != output)
        {
            _sizeInputs = Job.Inputs; _sizeOutput = output;
            _inputBytes = null; _outputBytes = -1;
            _metadata?.Cancel();
            var cancellation = new CancellationTokenSource(); _metadata = cancellation;
            MetadataReady = ReadSizesAsync(Job.Inputs.ToArray(), output, cancellation);
        }
        var local = Job.Inputs.Where(p => !Uri.TryCreate(p, UriKind.Absolute, out var uri) || uri.IsFile).ToArray();
        _fileSummary = Job.Inputs.Length == 0 ? "未指定源文件" : local.Length == 0 ? "在线来源" :
            Localization.Join(" · ", new[] { Job.Inputs.Length > 1 ? Localization.Format($"{Job.Inputs.Length} 个文件") : Extension,
                _inputBytes is { } total ? Size(total) : "大小未知" }.Where(s=>s.Length>0));
        _outputSize = Job.State == JobState.Completed && _outputBytes >= 0 ? Size(_outputBytes) : "";
        Raise(string.Empty);
    }
    internal void RefreshProgress()
    { Raise(nameof(StateText)); Raise(nameof(StateDetail)); Raise(nameof(HasStateDetail)); }
    private async Task ReadSizesAsync(string[] inputs, string output, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            var sizes = await Task.Run(() =>
            {
                long? total = 0;
                foreach (var path in inputs)
                {
                    token.ThrowIfCancellationRequested();
                    if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) continue;
                    var size = FileSize(path);
                    total = size < 0 || total is null ? null : total + size;
                }
                token.ThrowIfCancellationRequested();
                return (Input: total, Output: output.Length > 0 ? FileSize(output) : -1);
            }, token);
            if (_disposed || token.IsCancellationRequested || !ReferenceEquals(_metadata, cancellation)) return;
            _inputBytes = sizes.Input; _outputBytes = sizes.Output; Refresh();
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_metadata, cancellation)) _metadata = null; cancellation.Dispose(); }
    }
    internal void SetAppearancePreview(MediaInfo media)
    {
        _fileSummary = "示例任务";
        SetMedia(media, "");
    }
    internal void SetMedia(MediaInfo? media, string inspection, byte[]? cover = null)
    {
        // Queue summaries never need the full probe JSON (which may contain thousands of chapters).
        _media = media is null ? null : media with { RawJson = "" }; _inspection = inspection;
        var previous = _cover;
        using var stream = cover is { Length: > 0 } ? new MemoryStream(cover) : null;
        _cover = stream is not null ? new Bitmap(stream) : null;
        Raise(string.Empty);
        previous?.Dispose();
    }
    internal void ReleaseCover()
    {
        var cover = _cover; _cover = null;
        Raise(nameof(Cover)); Raise(nameof(HasCover)); Raise(nameof(NoCover)); cover?.Dispose();
    }
    public void Dispose() { _disposed = true; _metadata?.Cancel(); _media = null; ReleaseCover(); }
    private static long FileSize(string path) { try { var file = new FileInfo(path); return file.Exists ? file.Length : -1; } catch { return -1; } }
    private static string Size(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" }; var value = (double)bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return Number(value) + " " + units[unit];
    }
    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Time(double seconds, bool precise = false)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "00:00";
        var total = (long)Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds);
        var text = total >= 3600 ? $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}" : $"{total / 60:00}:{total % 60:00}";
        return text + (precise && seconds % 1 >= .001 ? (seconds % 1).ToString(".###", CultureInfo.InvariantCulture) : "");
    }
    private static string Codec(string codec) => codec.ToLowerInvariant() switch
    {
        "h264" or "libx264" => "H.264", "hevc" or "h265" or "libx265" => "H.265", "vp9" or "libvpx-vp9" => "VP9",
        "av1" or "libaom-av1" => "AV1", "aac" => "AAC", "mp3" or "libmp3lame" => "MP3", "opus" or "libopus" => "Opus", _ => codec.ToUpperInvariant()
    };
}
