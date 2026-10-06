namespace AvaMedia.Core;

public enum MediaFileKind { Video, Image, Audio, Document, Other }
public sealed record MediaRouteSource(string Path, MediaFileKind Kind);
public sealed record MediaRouteOption(Feature Feature, string Title, string Description, string[] Files,
    int SkippedCount, string DisabledReason = "")
{
    public bool Enabled => Files.Length > 0 && DisabledReason.Length == 0;
}
public interface IMediaFileRouter
{
    MediaFileKind Classify(string path);
    IReadOnlyList<MediaRouteOption> Routes(IReadOnlyList<MediaRouteSource> selected);
}

/// <summary>File acceptance and batch requirements, independent of the desktop presentation.</summary>
public sealed class MediaFileRouter : IMediaFileRouter
{
    public MediaFileKind Classify(string path)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (VideoFormats.IsVideo(path)) return MediaFileKind.Video;
        if (MediaEngine.IsImage(extension) || extension == "gif") return MediaFileKind.Image;
        if (extension is "mp3" or "flac" or "wav" or "m4a" or "ogg" or "aac" or "ac3" or "wma" or "opus" or "aiff" or "aif" or "alac") return MediaFileKind.Audio;
        if (extension is "pdf" or "txt" or "md" or "docx" or "xlsx" or "pptx" or "csv") return MediaFileKind.Document;
        return MediaFileKind.Other;
    }

    public IReadOnlyList<MediaRouteOption> Routes(IReadOnlyList<MediaRouteSource> selected)
    {
        List<MediaRouteOption> routes = [];
        void Add(string id, string title, string description, Func<MediaRouteSource, bool> accepts, int minimum = 1, string? requirement = null)
        {
            var files = selected.Where(accepts).Select(item => item.Path).ToArray();
            if (files.Length == 0) return;
            routes.Add(new(Catalog.Find(id), title, description, files, selected.Count - files.Length,
                files.Length < minimum ? requirement ?? "请至少选择两个文件。" : ""));
        }
        bool Video(MediaRouteSource source) => source.Kind == MediaFileKind.Video;
        bool Image(MediaRouteSource source) => source.Kind == MediaFileKind.Image;
        bool Audio(MediaRouteSource source) => source.Kind == MediaFileKind.Audio;
        bool Media(MediaRouteSource source) => source.Kind is MediaFileKind.Video or MediaFileKind.Audio or MediaFileKind.Image;
        bool Extension(MediaRouteSource source, string extension) => Path.GetExtension(source.Path).Equals(extension, StringComparison.OrdinalIgnoreCase);
        var groups = selected.GroupBy(source => source.Kind).OrderByDescending(group => group.Count()).Select(group => group.Key);
        foreach (var group in groups)
            switch (group)
            {
                case MediaFileKind.Video:
                    Add("video-compress", "视频压缩", "自动画质档、质量、码率与目标体积", Video);
                    Add("clip", "快速剪辑", "截取片段、调整速度、分段导出", Video);
                    Add("rotate", "批量旋转", "统一或逐个旋转，自动识别方向", Video);
                    Add("crop", "画面裁剪", "框选画面，共享或逐个调整", Video);
                    Add("mp4", "视频格式转换", "MP4 / MOV / MKV 等格式与编码", Video);
                    Add("join", "视频合并", "按文件顺序连接为一个视频", Video, 2, "请至少选择两个视频。");
                    Add("split", "提取音频", "把视频声音导出为独立音频", Video);
                    Add("frames", "导出视频帧", "按时间间隔保存画面", Video);
                    Add("delogo", "去除水印", "选择区域并进行模糊处理", Video);
                    Add("repair", "重新封装", "复制媒体流并更换容器", Video);
                    break;
                case MediaFileKind.Image:
                    Add("image-compress", "图片压缩", "真实压缩与滑动 / 并排对比", source => Image(source) && ImageCompression.Supports(source.Path));
                    Add("image-tools", "图片缩放 / 旋转", "调整尺寸、裁剪与旋转", Image);
                    Add("image-png", "图片格式转换", "JPEG / PNG / WebP / AVIF 等格式", Image);
                    Add("images-pdf", "图片合成 PDF", "按文件顺序排成 PDF 页面", Image);
                    break;
                case MediaFileKind.Audio:
                    Add("audio-mp3", "音频格式转换", "MP3 / AAC / FLAC / WAV 等格式", Audio);
                    Add("audio-clip", "音频剪辑", "截取区间并调整音频参数", Audio);
                    Add("audio-join", "音频合并", "按文件顺序连接音频", Audio, 2, "请至少选择两个音频。");
                    Add("audio-mix", "音频混合", "把多个声音混合到同一音轨", Audio, 2, "请至少选择两个音频。");
                    break;
                case MediaFileKind.Document:
                    Add("pdf-merge", "PDF 合并", "按文件顺序合并页面", source => Extension(source, ".pdf"), 2, "请至少选择两个 PDF。");
                    Add("pdf-split", "PDF 拆分", "拆出独立页面", source => Extension(source, ".pdf"));
                    Add("pdf-docx", "PDF 提取为 Word", "提取文字到 DOCX", source => Extension(source, ".pdf"));
                    Add("pdf-xlsx", "PDF 提取为 Excel", "提取文字到 XLSX", source => Extension(source, ".pdf"));
                    Add("pdf-text", "PDF 提取文字", "保存为 TXT 文本", source => Extension(source, ".pdf"));
                    Add("text-pdf", "文本转 PDF", "把 TXT 文本排成 PDF 页面", source => Extension(source, ".txt"));
                    break;
            }
        if (selected.Any(Video) && selected.Any(Audio))
        {
            var files = selected.Where(Video).Concat(selected.Where(Audio)).Select(source => source.Path).ToArray();
            routes.Add(new(Catalog.Find("mux"), "视频 / 音频混流", "用独立音频为视频配音", files, selected.Count - files.Length,
                files.Length == 2 ? "" : "请选中一个视频和一个音频。"));
        }
        Add("player", "打开播放器", "立即播放视频或音频", source => Video(source) || Audio(source));
        Add("info", "媒体信息", "查看编码、时长、尺寸与轨道", Media);
        Add("unzip", "解压 ZIP", "解压到独立文件夹", source => Extension(source, ".zip"));
        Add("zip", "打包 ZIP", "把选中文件打包到压缩文件", _ => true);
        Add("hash", "文件校验", "计算 SHA256 校验值", _ => true);
        return routes;
    }
}
