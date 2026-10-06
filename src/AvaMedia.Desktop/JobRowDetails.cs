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
    public Job Job { get; } = job;
    public Bitmap? Cover => _cover;
    public bool HasCover => Cover is not null;
    public bool NoCover => !HasCover;
    public bool IsRunning => Job.State == JobState.Running;
    public bool CanPreview => !IsRunning;
    public string PreviewTip => IsRunning ? "任务正在运行" : Catalog.Find(Job.FeatureId).Category is "文档" or "光驱设备\\DVD\\CD\\ISO" || Job.FeatureId == "download" || Job.State == JobState.Failed ? "查看任务详情" : "预览 / 编辑";
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
            if (feature.Operation == Operation.Download)
            {
                parts.Add(o.Format.ToUpperInvariant());
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
                if (Job.InputOptions?.Count > 0) parts.Add("逐文件编辑");
            }
            return Localization.Join(" · ", parts);
        }
    }
    public string StateText => Job.State switch
    {
        JobState.Waiting => "等待开始",
        JobState.Running => Localization.Format($"{Localization.Key(Job.FeatureId == "download" ? "下载中" : "处理中")}  {Job.Progress:0.0}%"),
        JobState.Completed => Localization.Join(" · ", new[] { "已完成", _outputSize }.Where(s=>s.Length>0)),
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
        var local = Job.Inputs.Where(p => !Uri.TryCreate(p, UriKind.Absolute, out var uri) || uri.IsFile).ToArray();
        var sizes = local.Select(FileSize).ToArray();
        _fileSummary = Job.Inputs.Length == 0 ? "录制任务" : local.Length == 0 ? "在线来源" :
            Localization.Join(" · ", new[] { Job.Inputs.Length > 1 ? Localization.Format($"{Job.Inputs.Length} 个文件") : Extension,
                sizes.All(s => s >= 0) ? Size(sizes.Sum()) : "大小未知" }.Where(s=>s.Length>0));
        _outputSize = Job.State == JobState.Completed && FileSize(Job.Output) is >= 0 and var size ? Size(size) : "";
        Raise(string.Empty);
    }
    internal void SetMedia(MediaInfo? media, string inspection, byte[]? cover = null)
    {
        _media = media; _inspection = inspection;
        var previous = _cover;
        using var stream = cover is { Length: > 0 } ? new MemoryStream(cover) : null;
        _cover = stream is not null ? new Bitmap(stream) : null;
        Raise(string.Empty);
        previous?.Dispose();
    }
    public void Dispose() { var cover = _cover; _cover = null; Raise(nameof(Cover)); cover?.Dispose(); }
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
