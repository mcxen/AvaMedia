using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace AvaMedia.Core;
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
}
public enum Operation { Convert, Join, Mux, AudioMix, SplitAudio, SplitVideo, Frames, Optimize, PdfMerge, PdfSplit, PdfText, PdfDocx, PdfXlsx, TextPdf, ImagesPdf, Zip, Unzip, Download, Record, Info, Hash, Player, IsoCopy }
public sealed record Feature(string Id, string Label, string Category, string Format, string Icon, Operation Operation = Operation.Convert, int Span = 1);
public static class Catalog
{
    public static IReadOnlyList<Feature> All { get; } = Build();
    public static string[] Categories { get; } = ["视频", "音频", "图片", "文档", "光驱设备\\DVD\\CD\\ISO", "工具集"];
    private static List<Feature> Build()
    {
        List<Feature> f = [];
        void Add(string id, string text, string cat, string ext, string icon, Operation op = Operation.Convert, int span = 1) => f.Add(new(id,text,cat,ext,icon,op,span));
        foreach(var x in new[]{("mp4","MP4"),("mkv","MKV"),("gif","GIF"),("webm","WebM")}) Add(x.Item1,"→ "+x.Item2,"视频",x.Item1,"video");
        Add("join","视频合并 & 混流","视频","mp4","join",Operation.Join,2);
        Add("other","→ AVI FLV\nMOV Etc…","视频","avi","formats");
        Add("optimize","优化","视频","mp4","gear",Operation.Optimize);
        Add("split","分离器","视频","m4a","split",Operation.SplitAudio);
        Add("crop","画面裁剪","视频","mp4","crop");
        Add("rotate","批量旋转","视频","mp4","rotate");
        Add("clip","快速剪辑","视频","mp4","clip");
        Add("delogo","去除水印","视频","mp4","erase");
        Add("frames","导出帧","视频","png","frames",Operation.Frames);
        Add("record","屏幕录像","视频","mp4","record",Operation.Record);
        Add("player","格式播放器","视频","","player",Operation.Player);
        Add("download","视频下载","视频","mp4","download",Operation.Download);
        foreach(var x in new[]{"mp3","flac","wav","m4a","ogg","aac","ac3","wma","opus","aiff"}) Add("audio-"+x,"→ "+x.ToUpperInvariant(),"音频",x,"audio");
        Add("audio-join","音频合并","音频","mp3","join",Operation.Join);
        Add("audio-mix","音频混合","音频","mp3","audio",Operation.AudioMix);
        Add("audio-clip","音频剪辑","音频","mp3","clip");
        foreach(var x in new[]{"jpg","png","webp","bmp","tiff","gif","ico","avif"}) Add("image-"+x,"→ "+x.ToUpperInvariant(),"图片",x,"image");
        Add("image-tools","缩放 / 旋转","图片","png","crop");
        Add("images-pdf","图片 → PDF","图片","pdf","document",Operation.ImagesPdf);
        Add("pdf-merge","PDF 合并","文档","pdf","join",Operation.PdfMerge);
        Add("pdf-split","PDF 拆分","文档","pdf","split",Operation.PdfSplit);
        Add("pdf-text","PDF → TXT","文档","txt","document",Operation.PdfText);
        Add("pdf-docx","PDF → DOCX","文档","docx","document",Operation.PdfDocx);
        Add("pdf-xlsx","PDF → XLSX","文档","xlsx","document",Operation.PdfXlsx);
        Add("text-pdf","TXT → PDF","文档","pdf","document",Operation.TextPdf);
        Add("zip","压缩 ZIP","文档","zip","archive",Operation.Zip);
        Add("unzip","解压 ZIP","文档","","archive",Operation.Unzip);
        Add("dvd","DVD / VOB 转换","光驱设备\\DVD\\CD\\ISO","mp4","disc",Operation.Join);
        Add("iso","光盘 → ISO","光驱设备\\DVD\\CD\\ISO","iso","disc",Operation.IsoCopy);
        Add("mux","视频 / 音频混流","工具集","mkv","join",Operation.Mux);
        Add("extract-video","提取视频流","工具集","mkv","video",Operation.SplitVideo);
        Add("info","媒体信息","工具集","json","info",Operation.Info);
        Add("hash","文件校验 SHA256","工具集","txt","gear",Operation.Hash);
        Add("repair","修复 / 重新封装","工具集","mkv","gear");
        return f;
    }
    public static Feature Find(string id) => All.First(f=>f.Id == id);
}
public sealed class ConversionOptions
{
    public DownloadOptions? Download { get; set; }
    public string Format { get; set; } = "mp4";
    public string VideoCodec { get; set; } = "自动";
    public int Quality { get; set; } = 23;
    public int? ImageQuality { get; set; }
    public int Threads { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public int AudioBitrate { get; set; } = 192;
    public int SampleRate { get; set; }
    public string AudioCodec { get; set; } = "自动";
    public int AudioChannels { get; set; }
    public bool Mute { get; set; }
    public bool CopyStreams { get; set; }
    public bool PreserveSourceAttributes { get; set; }
    public int? LosslessRotation { get; set; }
    public bool KeepAllAudioStreams { get; set; }
    public int VideoStreamIndex { get; set; }
    public int AudioStreamIndex { get; set; }
    public bool KeepMetadata { get; set; } = true;
    public double Start { get; set; }
    public double End { get; set; }
    public double Speed { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public int CropX { get; set; }
    public int CropY { get; set; }
    public int CropWidth { get; set; }
    public int CropHeight { get; set; }
    public int DelogoX { get; set; }
    public int DelogoY { get; set; }
    public int DelogoWidth { get; set; }
    public int DelogoHeight { get; set; }
    public int Rotation { get; set; }
    public bool Flip { get; set; }
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    // Null preserves the legacy queue behavior in which audio follows the video fades.
    public double? AudioFadeIn { get; set; }
    public double? AudioFadeOut { get; set; }
    public bool Echo { get; set; }
    public bool NoiseReduction { get; set; }
    public bool ReverseAudio { get; set; }
    public double FrameInterval { get; set; } = 1;
    public string Subtitle { get; set; } = "";
    public SubtitleMode SubtitleMode { get; set; }
    public int SubtitleStreamIndex { get; set; } = -1;
    public string SubtitleLanguage { get; set; } = "";
    public string SubtitleFont { get; set; } = "";
    public int SubtitleFontSize { get; set; }
    public string SubtitleColor { get; set; } = "#FFFFFF";
    public int SubtitleAlignment { get; set; } = 2;
    public int SubtitleMargin { get; set; } = 20;
    public string RecordSource { get; set; } = "desktop";
    public double RecordSeconds { get; set; } = 30;
    public ConversionOptions Clone() => (ConversionOptions)MemberwiseClone();
}
public enum JobState { Waiting, Running, Completed, Failed, Cancelled }
public sealed class Job : Observable
{
    private string _downloadTitle="";
    public string DownloadTitle { get=>_downloadTitle; set { if(Set(ref _downloadTitle,value))Raise(nameof(Name)); } }
    private string _progressDetail="";
    public string ProgressDetail { get=>_progressDetail; set { if(Set(ref _progressDetail,value))Raise(nameof(Status)); } }
    private ProgressEstimate? _estimate;
    [JsonIgnore]
    public ProgressEstimate? Estimate { get=>_estimate; set { if(Set(ref _estimate,value)){Raise(nameof(RemainingTimeText));Raise(nameof(Status));} } }
    [JsonIgnore]
    public string RemainingTimeText => State==JobState.Running ? Estimate?.Text??"" : "";
    public Guid Id { get; set; } = Guid.NewGuid();
    private string _featureId="mp4";
    public string FeatureId { get=>_featureId;set{if(Set(ref _featureId,value))Raise(nameof(Target));} }
    private string[] _inputs=[];
    public string[] Inputs { get=>_inputs;set{if(Set(ref _inputs,value)){Raise(nameof(Name));Raise(nameof(Source));}} }
    private string _output="";
    public string Output { get=>_output; set{if(Set(ref _output,value))Raise(nameof(Target));} }
    public ConversionOptions Options { get; set; } = new();
    public List<ConversionOptions>? InputOptions { get; set; }
    public double Duration { get; set; }
    private JobState _state;
    public JobState State { get=>_state; set {if(Set(ref _state,value)) {if(value!=JobState.Running)Estimate=null;Raise(nameof(Status));Raise(nameof(RemainingTimeText));Raise(nameof(CanRetry));}} }
    private double _progress;
    public double Progress { get=>_progress; set {if(Set(ref _progress,value)) Raise(nameof(Status));} }
    public string Error { get; set; } = "";
    public string Log { get; set; } = "";
    public string Name => string.IsNullOrWhiteSpace(DownloadTitle)?string.Join(" + ", Inputs.Select(Path.GetFileName)):DownloadTitle;
    public string Source => string.Join(Environment.NewLine, Inputs);
    public string Target => $"{Catalog.Find(FeatureId).Label.Replace("\n"," ")}  →  {Output}";
    [JsonIgnore]
    public string Status => State switch {JobState.Waiting=>"等待中",JobState.Running=>$"{(FeatureId=="download"?"下载中":"转换中")}  {Progress:0.0}%"+(ProgressDetail.Length>0?" · "+ProgressDetail:"")+(RemainingTimeText.Length>0?" · "+RemainingTimeText:""),JobState.Completed=>"完成",JobState.Failed=>"失败 · 双击查看日志",_=>"已停止"};
    public bool CanRetry => State is JobState.Failed or JobState.Cancelled;
}
public enum SubtitleMode { Auto, None, BurnIn, Preserve, ExternalTrack }
public sealed record MediaInfo(double Duration, int Width, int Height, bool HasAudio, bool HasVideo, string RawJson, string VideoCodec = "", string AudioCodec = "", int AudioSampleRate = 0, int AudioChannels = 0, int VideoStreamIndex = 0, int AudioStreamIndex = 0, double FrameRate = 0);
public sealed class AppSettings
{
    public string OutputFolder { get; set; } = MediaFolders.DefaultOutput;
    public bool OutputToSource { get; set; }
    public bool AddSettingName { get; set; }
    public bool OpenOutputFolderOnComplete { get; set; }
    public bool ShutdownOnComplete { get; set; }
    public bool PlayOperationSound { get; set; }
    public bool PlayCompleteSound { get; set; } = true;
    public bool PlayErrorSound { get; set; } = true;
    public bool SystemContextMenu { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public string FFmpegPath { get; set; } = "";
    public string FFprobePath { get; set; } = "";
    public string YtDlpPath { get; set; } = "";
    public int ParallelJobs { get; set; } = 2;
    public bool MultiThread { get; set; } = true;
    public bool NotifyComplete { get; set; } = true;
    public bool ReduceMotion { get; set; }
    public string Theme { get; set; } = "Light";
    public bool ConfirmPlayerDeletion { get; set; }
    public bool AutoDetectGpu { get; set; } = true;
    public int CpuThreads { get; set; } = 8;
    public int JpegQuality { get; set; } = 90;
    public int WebpQuality { get; set; } = 90;
    public AppSettings Clone() => (AppSettings)MemberwiseClone();
    public void CopyFrom(AppSettings source)
    {
        OutputFolder=source.OutputFolder;FFmpegPath=source.FFmpegPath;FFprobePath=source.FFprobePath;YtDlpPath=source.YtDlpPath;
        OutputToSource=source.OutputToSource;AddSettingName=source.AddSettingName;OpenOutputFolderOnComplete=source.OpenOutputFolderOnComplete;
        ShutdownOnComplete=source.ShutdownOnComplete;PlayOperationSound=source.PlayOperationSound;PlayCompleteSound=source.PlayCompleteSound;
        PlayErrorSound=source.PlayErrorSound;SystemContextMenu=source.SystemContextMenu;MinimizeToTray=source.MinimizeToTray;CheckForUpdates=source.CheckForUpdates;
        CloseToTray=source.CloseToTray;
        ParallelJobs=source.ParallelJobs;MultiThread=source.MultiThread;NotifyComplete=source.NotifyComplete;
        ReduceMotion=source.ReduceMotion;Theme=source.Theme;AutoDetectGpu=source.AutoDetectGpu;
        ConfirmPlayerDeletion=source.ConfirmPlayerDeletion;
        CpuThreads=source.CpuThreads;JpegQuality=source.JpegQuality;WebpQuality=source.WebpQuality;
    }
}
