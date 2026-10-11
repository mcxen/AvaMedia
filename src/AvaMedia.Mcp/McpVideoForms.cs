using System.Text.Json;
using System.Text.Json.Nodes;
using AvaMedia.Core;

namespace AvaMedia.Mcp;

public sealed record McpOptionChoice(string Label, JsonNode? Value);
public sealed record McpOptionVisibility(string Path, JsonNode?[] Values);
public sealed record McpOptionField(string Path, string Label, string Kind, string Group,
    double Minimum = 0, double Maximum = 1000000, double Step = 1, McpOptionChoice[]? Choices = null,
    McpOptionVisibility[]? VisibleWhen = null);
public sealed record McpTaskForm(JsonObject Defaults, McpOptionField[] Fields, string Hint, bool Preview);

/// <summary>Editable service parameters, attached to the existing Catalog capabilities.</summary>
internal static class McpVideoForms
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static ConversionOptions Defaults(Feature feature) => new()
    {
        Format = feature.Format.Length > 0 ? feature.Format : "mp4",
        CopyStreams = feature.Id is "repair" or "extract-video",
        VoiceEnhancement = feature.Id == "voice-enhance",
        Rotation = feature.Id == "rotate" ? 90 : 0,
        Transcription = feature.Operation == Operation.Transcribe ? new() : null,
        VideoSummary = feature.Operation == Operation.VideoSummary ? new() : null,
        PersonClip = feature.Operation == Operation.PersonClip ? new() : null,
        VideoCompression = feature.Operation == Operation.VideoCompress ? new() : null,
        VideoSlimming = feature.Operation == Operation.VideoSlim ? new() : null,
        ContactSheet = feature.Id == "contact-sheet" ? new() : null,
        Download = feature.Operation == Operation.Download ? new() : null
    };

    public static McpTaskForm? For(Feature feature, AppSettings settings)
    {
        if (feature.Category != "视频" && feature.Id is not ("crop" or "rotate" or "contact-sheet")) return null;
        var fields = new List<McpOptionField>();
        void Number(string path, string label, string group, double min = 0, double max = 1000000, double step = 1) => fields.Add(new(path, label, "number", group, min, max, step));
        void Check(string path, string label, string group) => fields.Add(new(path, label, "boolean", group));
        void File(string path, string label, string group) => fields.Add(new(path, label, "file", group));
        void Track(string path, string label, string kind) => fields.Add(new(path, label, kind + "-track", "轨道"));
        void Choice(string path, string label, string group, string[] labels, object[] values) => fields.Add(new(path, label, "choice", group,
            Choices: labels.Select((label, index) => new McpOptionChoice(label, JsonSerializer.SerializeToNode(values[index], Json))).ToArray()));
        void Strings(string path, string label, string group, params string[] values) => Choice(path, label, group, values, values.Cast<object>().ToArray());
        void Visible(string path, string dependency, params object[] values)
        {
            var index = fields.FindIndex(field => field.Path == path);
            if (index < 0) return;
            var field = fields[index];
            fields[index] = field with { VisibleWhen = [.. field.VisibleWhen ?? [], new(dependency, values.Select(value => JsonSerializer.SerializeToNode(value, Json)).ToArray())] };
        }
        void Format(params string[] values) => Strings("format", "输出格式", "输出", values);
        void Time()
        {
            Number("start", "开始（秒）", "剪辑区间", step: .1);
            Number("end", "结束（秒，0 到结尾）", "剪辑区间", step: .1);
        }
        void Encoding()
        {
            Format("mp4", "mkv", "mov", "webm", "ts", "avi");
            Strings("videoCodec", "视频编码", "输出", "自动", "libx264", "libx265", "libvpx-vp9");
            Choice("videoRateMode", "码率控制", "输出", ["沿用源文件", "质量", "指定码率"], [0, 1, 2]);
            Number("quality", "质量值（越小越清晰）", "输出", 0, 51);
            Choice("videoBitrate", "视频码率（kbps）", "输出", ["1000", "2000", "4000", "8000", "12000", "20000", "50000"], [1000, 2000, 4000, 8000, 12000, 20000, 50000]);
            Choice("width", "输出宽度", "输出", ["自动", "320", "480", "640", "720", "1080", "1280", "1920", "2160", "3840"], [0, 320, 480, 640, 720, 1080, 1280, 1920, 2160, 3840]);
            Choice("height", "输出高度", "输出", ["自动", "320", "480", "640", "720", "1080", "1280", "1920", "2160", "3840"], [0, 320, 480, 640, 720, 1080, 1280, 1920, 2160, 3840]);
            Choice("fps", "帧率", "输出", ["原帧率", "24", "25", "30", "50", "60", "120"], [0, 24, 25, 30, 50, 60, 120]);
            Check("mute", "移除声音", "声音"); Number("audioBitrate", "音频码率（kbps）", "声音", 16, 1024);
            Strings("audioCodec", "音频编码", "声音", "自动", "aac", "libmp3lame", "flac", "libopus", "pcm_s16le");
            Choice("sampleRate", "采样率", "声音", ["原采样率", "22050 Hz", "44100 Hz", "48000 Hz", "96000 Hz"], [0, 22050, 44100, 48000, 96000]);
            Choice("audioChannels", "声道", "声音", ["原声道", "单声道", "立体声", "5.1 声道"], [0, 1, 2, 6]);
            Number("volume", "音量倍数", "声音", 0, 10, .1);
            Check("keepMetadata", "保留元数据", "输出");
            Track("videoStreamIndex", "视频轨道", "video"); Track("audioStreamIndex", "音频轨道", "audio");
            Check("keepAllAudioStreams", "保留全部音轨", "轨道");
        }
        void Region(string prefix, string title)
        {
            Number(prefix + "X", "左侧 X（像素）", title, 0, 32768, 2);
            Number(prefix + "Y", "顶部 Y（像素）", title, 0, 32768, 2);
            Number(prefix + "Width", "宽度（像素）", title, 0, 32768, 2);
            Number(prefix + "Height", "高度（像素）", title, 0, 32768, 2);
        }
        void Rotate()
        {
            Choice("rotation", "旋转方向", "画面", ["不旋转", "顺时针 90°", "180°", "逆时针 90°"], [0, 90, 180, 270]);
            Check("flip", "水平翻转", "画面");
        }
        void Subtitle()
        {
            Choice("subtitleMode", "字幕处理", "字幕", ["自动", "移除", "烧录到画面", "保留轨道", "添加外挂轨道"], [0, 1, 2, 3, 4]);
            File("subtitle", "服务器字幕文件", "字幕");
            Number("subtitleFontSize", "字号（0 自动）", "字幕", 0, 200);
            Choice("subtitleColor", "字幕颜色", "字幕", ["白色", "黄色", "黑色", "青色"], ["#FFFFFF", "#FFFF00", "#000000", "#00FFFF"]);
            Choice("subtitleAlignment", "字幕位置", "字幕", ["左下", "底部居中", "右下", "左中", "画面中心", "右中", "左上", "顶部居中", "右上"], [1, 2, 3, 4, 5, 6, 7, 8, 9]);
            Number("subtitleMargin", "字幕边距", "字幕", 0, 300);
            Track("subtitleStreamIndex", "内嵌字幕轨道", "subtitle");
        }
        var hint = "参数应用到每个输入文件，原文件保持不变。";
        switch (feature.Id)
        {
            case "mp4": Encoding(); Time(); Subtitle(); break;
            case "clip":
                Time(); Number("speed", "播放速度", "剪辑区间", .25, 4, .05);
                Region("crop", "裁剪区域"); Rotate();
                Number("fadeIn", "画面淡入（秒）", "淡入淡出", 0, 60, .1);
                Number("fadeOut", "画面淡出（秒）", "淡入淡出", 0, 60, .1);
                Encoding(); Subtitle(); fields.RemoveAll(field => field.Path == "format");
                hint = "每个文件独立编辑多个片段；拖动区间边界，支持裁剪、旋转、速度、淡入淡出和字幕。原格式 / 原码率按关键帧切分。"; break;
            case "crop": Region("crop", "裁剪区域"); Encoding(); hint = "在预览中拖出裁剪框，坐标自动对齐偶数像素。多个文件共用同一区域。"; break;
            case "delogo": Region("delogo", "水印区域"); Encoding(); hint = "在预览中框选水印区域，服务器处理区域内的画面。"; break;
            case "rotate": Rotate(); Encoding(); break;
            case "join": Encoding(); hint = "按下方文件顺序合并；使用上移、下移调整顺序。"; break;
            case "mux": Encoding(); hint = "依次添加视频、音频两个文件；读取实际轨道后选择，支持调整文件顺序。"; break;
            case "split": Format("m4a", "mp3", "wav", "flac", "opus"); Time(); Track("audioStreamIndex", "音频轨道", "audio"); Number("audioBitrate", "音频码率（kbps）", "输出", 16, 1024); break;
            case "extract-video": Format("mkv", "mp4", "ts"); Track("videoStreamIndex", "视频轨道", "video"); break;
            case "repair": Format("mkv", "mp4", "mov", "ts"); hint = "以流复制重新封装，保留源编码；无法修复已经丢失的画面数据。"; break;
            case "frames": Time(); Number("frameInterval", "帧间隔（秒）", "采样", .04, 3600, .1); Number("width", "图片宽度（0 原尺寸）", "输出", 0, 16384); Format("png", "jpg"); break;
            case "contact-sheet": Number("contactSheet.columns", "列数", "网格", 1, 20); Number("contactSheet.rows", "行数", "网格", 1, 20); Number("contactSheet.cellWidth", "单格宽度", "网格", 64, 1920); break;
            case "voice-enhance": Encoding(); Number("voiceEnhancementStrength", "人声增强强度", "声音", 0, 100); Check("noiseReduction", "降噪", "声音"); break;
            case "video-compress":
                Strings("videoCompression.format", "输出格式", "压缩", "mp4", "mkv", "mov", "ts");
                Strings("videoCompression.codec", "编码", "压缩", "h264", "hevc");
                Choice("videoCompression.mode", "压缩模式", "压缩", ["自动", "质量", "码率", "原体积百分比", "目标 MB"], [0, 1, 2, 3, 4]);
                Choice("videoCompression.preset", "自动档位", "压缩", ["高画质", "均衡", "更小"], [0, 1, 2]);
                Number("videoCompression.quality", "质量值", "压缩", 1, 51);
                Number("videoCompression.videoBitrate", "码率（kbps）", "压缩", 64, 200000);
                Number("videoCompression.percentage", "原体积百分比", "压缩", 5, 95);
                Number("videoCompression.targetMegabytes", "目标 MB", "压缩", .1, 1000000, .1);
                Choice("videoCompression.speed", "编码速度", "压缩", ["快速", "均衡", "精细"], [0, 1, 2]);
                Choice("videoCompression.maxDimension", "分辨率上限", "压缩", ["原尺寸", "1080p", "720p", "480p"], [0, 1920, 1280, 854]);
                Choice("videoCompression.maxFrameRate", "帧率上限", "压缩", ["原帧率", "30", "24"], [0, 30, 24]);
                Check("videoCompression.keepAudio", "保留声音", "压缩"); Number("videoCompression.audioBitrate", "音频码率（kbps）", "压缩", 32, 320);
                Check("videoCompression.preferGpu", "优先 GPU", "压缩"); break;
            case "video-slim":
                Choice("videoSlimming.preset", "瘦身档位", "瘦身", ["保留画质", "均衡", "更小"], [0, 1, 2]);
                Strings("videoSlimming.codec", "编码", "瘦身", "hevc", "h264"); Strings("videoSlimming.format", "输出格式", "瘦身", "mkv", "mp4");
                hint = "服务器分析画质与体积后执行瘦身，保持完整视频。"; break;
            case "auto-subtitle":
                Strings("transcription.language", "识别语言", "语音", TranscriptionOptions.Languages);
                Choice("transcription.model", "语音模型", "语音", ["Base", "Tiny", "Small"], [0, 1, 2]);
                Check("transcription.recognitionOnly", "仅识别字幕结果", "语音"); Format("mp4", "mkv", "srt", "ass"); Number("subtitleFontSize", "字幕字号（0 自动）", "字幕", 0, 200); break;
            case "person-clip":
                Number("personClip.detection.framesPerSecond", "每秒采样帧数", "人物检测", .25, 16, .25);
                Number("personClip.detection.threshold", "置信度阈值", "人物检测", .1, .9, .05);
                Number("personClip.detection.paddingSeconds", "片段前后保留（秒）", "人物检测", 0, 30, .1);
                Number("personClip.detection.mergeGapSeconds", "合并间隔（秒）", "人物检测", 0, 30, .1);
                Number("personClip.detection.minimumSeconds", "最短片段（秒）", "人物检测", 0, 30, .1);
                Check("personClip.detection.keepUncertain", "保留不确定片段", "人物检测"); Check("personClip.analysisOnly", "仅分析片段", "人物检测");
                Strings("personClip.exportPreset", "输出预设", "输出", QuickClipBatch.Presets); break;
            case "video-summary":
                Choice("videoSummary.provider", "处理方式", "总结", ["本地模型", "线上 API"], [0, 1]);
                var providers = settings.OnlineAi.Providers.Where(provider => provider.Enabled).ToArray();
                Choice("videoSummary.onlineProviderId", "AI 供应商", "总结", ["服务器默认", .. providers.Select(provider => provider.Name)], ["", .. providers.Select(provider => (object)provider.Id)]);
                Choice("videoSummary.focus", "分析重点", "总结", ["通用总结", "人物与对话", "操作步骤", "事件时间线"], ["", "人物与对话", "操作步骤", "事件时间线"]);
                Strings("videoSummary.outputLanguage", "输出语言", "总结", "简体中文", "English", "日本語");
                Choice("videoSummary.transcriptSource", "字幕来源", "总结", ["自动", "内嵌字幕", "语音识别", "外部字幕"], [0, 1, 2, 3]);
                File("videoSummary.subtitleFile", "服务器字幕文件", "总结");
                Number("videoSummary.frameCount", "画面采样数", "总结", 1, 48);
                Number("videoSummary.chunkCharacters", "分段字符数", "总结", 1000, 4000, 100);
                Check("videoSummary.extractAbstract", "提取摘要", "总结"); Check("videoSummary.summarizeContent", "内容总结", "总结");
                Check("videoSummary.extractSubtitles", "提取字幕", "总结"); Check("videoSummary.analyzeContent", "内容分析", "总结"); Check("videoSummary.analyzeFrames", "分析画面", "总结"); break;
            case "download":
                Format("mp4", "mkv", "mp3", "m4a"); Number("download.maxHeight", "最高画质（0 最佳）", "下载", 0, 4320);
                Check("download.subtitles", "下载字幕", "下载"); Check("download.autoSubtitles", "自动字幕", "下载");
                File("download.cookieFile", "服务器 cookies.txt", "下载"); break;
            case "player": case "info": hint = "添加文件后读取媒体信息或在浏览器播放服务器文件。"; break;
        }
        Visible("quality", "videoRateMode", 1); Visible("videoBitrate", "videoRateMode", 2);
        foreach (var path in new[] { "audioCodec", "audioBitrate", "audioChannels", "sampleRate", "volume" }) Visible(path, "mute", false);
        Visible("subtitle", "subtitleMode", 0, 2, 4);
        Visible("subtitleStreamIndex", "subtitleMode", 0, 2, 3);
        Visible("audioStreamIndex", "keepAllAudioStreams", false);
        foreach (var path in new[] { "subtitleFontSize", "subtitleColor", "subtitleAlignment", "subtitleMargin" }) Visible(path, "subtitleMode", 2);
        Visible("videoCompression.preset", "videoCompression.mode", 0);
        Visible("videoCompression.quality", "videoCompression.mode", 1);
        Visible("videoCompression.videoBitrate", "videoCompression.mode", 2);
        Visible("videoCompression.percentage", "videoCompression.mode", 3);
        Visible("videoCompression.targetMegabytes", "videoCompression.mode", 4);
        Visible("videoCompression.audioBitrate", "videoCompression.keepAudio", true);
        foreach (var path in new[] { "speed", "maxDimension", "maxFrameRate", "audioBitrate" })
            Visible("videoCompression." + path, "videoCompression.mode", 1, 2, 3, 4);
        Visible("videoSummary.onlineProviderId", "videoSummary.provider", 1);
        Visible("videoSummary.subtitleFile", "videoSummary.transcriptSource", 3);
        Visible("videoSummary.frameCount", "videoSummary.analyzeFrames", true);
        return new((JsonObject)JsonSerializer.SerializeToNode(Defaults(feature), Json)!, fields.ToArray(), hint,
            feature.Id is not ("download" or "video-summary"));
    }
}
