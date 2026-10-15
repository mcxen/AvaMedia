namespace AvaMedia.Core;

/// <summary>Stages of Operation.Transcribe. Extraction, VAD and cue timing run together per recognition chunk.</summary>
public static class SubtitleStageIds
{
    public const string ReadAudio = "read-audio";
    public const string PrepareModel = "prepare-model";
    public const string Recognize = "recognize";
    public const string ValidateTimeline = "validate-timeline";
    public const string WriteSubtitles = "write-subtitles";

    public static IReadOnlyList<TaskStageDescriptor> All { get; } = Array.AsReadOnly(new TaskStageDescriptor[]
    {
        new(ReadAudio, "读取音频", "探测所选音轨、识别区间与输出能力"),
        new(PrepareModel, "准备语音模型", "校验、下载并加载 Whisper 与语音检测模型"),
        new(Recognize, "语音识别", "逐块提取音频、检测语音并生成带时间戳的字幕"),
        new(ValidateTimeline, "校验时间轴", "检查字幕结果、时间范围及源文件是否改变"),
        new(WriteSubtitles, "写出字幕", "保存识别结果并写出字幕或烧录视频")
    });
}
