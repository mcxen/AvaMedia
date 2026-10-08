namespace AvaMedia.Core;

public enum VideoTranscriptSource { Automatic, Embedded, Speech, External }
public enum VideoSummaryProvider { Local, Online }

public sealed class VideoSummaryOptions
{
    public VideoSummaryProvider Provider { get; set; }
    public bool ExtractAbstract { get; set; } = true;
    public bool SummarizeContent { get; set; } = true;
    public bool ExtractSubtitles { get; set; } = true;
    public bool AnalyzeContent { get; set; } = true;
    public VideoTranscriptSource TranscriptSource { get; set; }
    public string SubtitleFile { get; set; } = "";
    public int SubtitleTrack { get; set; } = -1;
    public int AudioTrack { get; set; }
    public TranscriptionOptions Speech { get; set; } = new() { Model = SpeechModel.Small };
    public bool AnalyzeFrames { get; set; } = true;
    public int FrameCount { get; set; } = 12;
    public string OutputLanguage { get; set; } = "简体中文";
    public string Focus { get; set; } = "";
    public int ChunkCharacters { get; set; } = 2400;
    public bool PreferGpu { get; set; } = true;
    public bool NeedsAi => ExtractAbstract || SummarizeContent || AnalyzeContent;

    public VideoSummaryOptions Clone()
    {
        var copy = (VideoSummaryOptions)MemberwiseClone(); copy.Speech = Speech.Clone(); return copy;
    }

    public void Validate()
    {
        if (!NeedsAi && !ExtractSubtitles) throw new ArgumentException("请至少选择一种输出内容。");
        if (!Enum.IsDefined(Provider) || !Enum.IsDefined(TranscriptSource) || SubtitleTrack < -1 || AudioTrack < 0)
            throw new ArgumentException("字幕来源或轨道索引无效。");
        if (TranscriptSource == VideoTranscriptSource.External && !File.Exists(SubtitleFile))
            throw new ArgumentException("请选择有效的外部字幕文件。");
        if (TranscriptSource == VideoTranscriptSource.External && Path.GetExtension(SubtitleFile).ToLowerInvariant() is not (".srt" or ".vtt" or ".ass" or ".ssa"))
            throw new ArgumentException("外部字幕支持 SRT、VTT、ASS 和 SSA。");
        Speech.Validate();
        if (FrameCount is < 1 or > 48 || ChunkCharacters is < 1000 or > 4000)
            throw new ArgumentException("画面采样数须为 1–48，分段字符数须为 1000–4000。");
        if (OutputLanguage is not ("简体中文" or "English" or "日本語")) throw new ArgumentException("请选择输出语言。");
        if (Focus.Length > 500) throw new ArgumentException("分析重点不能超过 500 字符。");
    }
}
