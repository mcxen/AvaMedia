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
public enum Operation { Convert, Join, Mux, AudioMix, SplitAudio, SplitVideo, Frames, VideoCompress, PdfMerge, PdfSplit, PdfText, PdfDocx, PdfXlsx, TextPdf, ImagesPdf, Zip, Unzip, Download, Info, Player, IsoCopy, ImageCompress, PdfAge, PdfCompress, BatchTools, Transcribe }
public sealed record Feature(string Id, string Label, string Category, string Format, string Icon, Operation Operation = Operation.Convert, int Span = 1);
public static class Catalog
{
    public static IReadOnlyList<Feature> All { get; } = Build();
    public static string[] Categories { get; } = ["视频", "音频", "图片", "文档", "光驱设备\\DVD\\CD\\ISO", "工具集"];
    private static List<Feature> Build()
    {
        List<Feature> f = [];
        void Add(string id, string text, string cat, string ext, string icon, Operation op = Operation.Convert, int span = 1) => f.Add(new(id,text,cat,ext,icon,op,span));
        Add("mp4","格式转换","视频","mp4","video",Operation.Convert,2);
        Add("video-compress","视频压缩","视频","mp4","gear",Operation.VideoCompress,2);
        Add("join","视频合并 & 混流","视频","mp4","join",Operation.Join,2);
        Add("split","分离器","视频","m4a","split",Operation.SplitAudio);
        Add("clip","快速剪辑","视频","mp4","clip");
        Add("person-clip","保留有人片段 · Beta","视频","mp4","clip");
        Add("delogo","去除水印","视频","mp4","erase");
        Add("frames","导出帧","视频","png","frames",Operation.Frames);
        Add("player",AppIdentity.PlayerChineseName,"视频","","player",Operation.Player);
        Add("download","视频下载","视频","mp4","download",Operation.Download);
        Add("auto-subtitle","自动字幕","视频","mp4","document",Operation.Transcribe);
        Add("voice-enhance","人声增强","视频","mp4","audio");
        foreach(var x in new[]{"mp3","flac","wav","m4a","ogg","aac","ac3","wma","opus","aiff"}) Add("audio-"+x,"→ "+x.ToUpperInvariant(),"音频",x,"audio");
        Add("audio-join","音频合并","音频","mp3","join",Operation.Join);
        Add("audio-mix","音频混合","音频","mp3","audio",Operation.AudioMix);
        Add("audio-clip","音频剪辑","音频","mp3","clip");
        Add("audio-enhance","人声增强","音频","wav","audio");
        foreach(var x in new[]{"jpg","png","webp","bmp","tiff","gif","ico","avif"}) Add("image-"+x,"→ "+x.ToUpperInvariant(),"图片",x,"image");
        Add("image-compress","图片压缩","图片","webp","image-compress",Operation.ImageCompress);
        Add("image-tools","缩放 / 旋转","图片","png","crop");
        Add("images-pdf","图片 → PDF","图片","pdf","document",Operation.ImagesPdf);
        Add("pdf-merge","PDF 合并","文档","pdf","pdf-merge",Operation.PdfMerge);
        Add("pdf-split","PDF 拆分","文档","pdf","pdf-split",Operation.PdfSplit);
        Add("pdf-age","PDF 做旧","文档","pdf","document",Operation.PdfAge);
        Add("pdf-compress","PDF 压缩","文档","pdf","document",Operation.PdfCompress);
        Add("pdf-text","PDF → TXT","文档","txt","pdf-text",Operation.PdfText);
        Add("pdf-docx","PDF → DOCX","文档","docx","pdf-docx",Operation.PdfDocx);
        Add("pdf-xlsx","PDF → XLSX","文档","xlsx","pdf-xlsx",Operation.PdfXlsx);
        Add("text-pdf","TXT → PDF","文档","pdf","text-pdf",Operation.TextPdf);
        Add("crop","批量裁剪","工具集","mp4","crop");
        Add("rotate","批量旋转","工具集","mp4","rotate");
        Add("video-rename","视频重命名","工具集","","gear",Operation.BatchTools);
        Add("contact-sheet","多宫格截图","工具集","","frames",Operation.BatchTools);
        Add("zip","压缩 ZIP","工具集","zip","zip",Operation.Zip);
        Add("unzip","解压 ZIP","工具集","","unzip",Operation.Unzip);
        Add("dvd","DVD / VOB 转换","光驱设备\\DVD\\CD\\ISO","mp4","disc",Operation.Join);
        Add("iso","光盘 → ISO","光驱设备\\DVD\\CD\\ISO","iso","disc",Operation.IsoCopy);
        Add("mux","视频 / 音频混流","视频","mkv","join",Operation.Mux);
        Add("extract-video","提取视频流","视频","mkv","video",Operation.SplitVideo);
        Add("info","媒体信息","视频","json","info",Operation.Info);
        Add("repair","修复 / 重新封装","视频","mkv","gear");
        return f;
    }
    public static Feature Find(string id) => All.First(f=>f.Id == id);
}
public sealed class ConversionOptions
{
    public PdfToolOptions? Pdf { get; set; }
    public DownloadOptions? Download { get; set; }
    public VideoCompressionOptions? VideoCompression { get; set; }
    public ImageCompressionOptions? ImageCompression { get; set; }
    public TranscriptionOptions? Transcription { get; set; }
    public string Format { get; set; } = "mp4";
    public string VideoCodec { get; set; } = "自动";
    public int Quality { get; set; } = 23;
    public int VideoBitrate { get; set; }
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
    public bool VoiceEnhancement { get; set; }
    public int VoiceEnhancementStrength { get; set; } = 80;
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
    public double? SubtitlePositionX { get; set; }
    public double? SubtitlePositionY { get; set; }
    public ConversionOptions Clone() {var copy=(ConversionOptions)MemberwiseClone();copy.Pdf=Pdf?.Clone();copy.Transcription=Transcription?.Clone();return copy;}
}
public enum JobState { Waiting, Running, Completed, Failed, Cancelled }
public sealed partial class Job : Observable
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
    private string _error = "";
    public string Error { get => _error; set => _error = JobLogStore.Summarize(value); }
    public string Name => string.IsNullOrWhiteSpace(DownloadTitle)?string.Join(" + ", Inputs.Select(Path.GetFileName)):DownloadTitle;
    public string Source => string.Join(Environment.NewLine, Inputs);
    public string Target => $"{Catalog.Find(FeatureId).Label.Replace("\n"," ")}  →  {Output}";
    [JsonIgnore]
    public string Status => State switch {JobState.Waiting=>"等待中",JobState.Running=>$"{(FeatureId=="download"?"下载中":"转换中")}  {Progress:0.0}%"+(ProgressDetail.Length>0?" · "+ProgressDetail:"")+(RemainingTimeText.Length>0?" · "+RemainingTimeText:""),JobState.Completed=>"完成",JobState.Failed=>"失败",_=>"已停止"};
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
    public bool AutoUpdate { get; set; }
    public bool SilentUpdate { get; set; }
    public bool EnableBetaFeatures { get; set; }
    public bool AutoDownloadRepairModel { get; set; } = true;
    public string FFmpegPath { get; set; } = "";
    public string FFprobePath { get; set; } = "";
    public string YtDlpPath { get; set; } = "";
    public int ParallelJobs { get; set; } = 2;
    public bool MultiThread { get; set; } = true;
    public bool NotifyComplete { get; set; } = true;
    public bool ReduceMotion { get; set; }
    public string Theme { get; set; } = "Light";
    public string Language { get; set; } = "system";
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
        CloseToTray=source.CloseToTray;AutoUpdate=source.AutoUpdate;SilentUpdate=source.SilentUpdate;
        EnableBetaFeatures=source.EnableBetaFeatures;AutoDownloadRepairModel=source.AutoDownloadRepairModel;
        ParallelJobs=source.ParallelJobs;MultiThread=source.MultiThread;NotifyComplete=source.NotifyComplete;
        ReduceMotion=source.ReduceMotion;Theme=source.Theme;Language=source.Language;AutoDetectGpu=source.AutoDetectGpu;
        ConfirmPlayerDeletion=source.ConfirmPlayerDeletion;
        CpuThreads=source.CpuThreads;JpegQuality=source.JpegQuality;WebpQuality=source.WebpQuality;
    }
}
