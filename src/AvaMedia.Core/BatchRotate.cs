namespace AvaMedia.Core;

public sealed record BatchRotateInput(string Path, MediaInfo Info, int? Rotation = null);
public sealed record BatchRotateRequest(IReadOnlyList<BatchRotateInput> Inputs, int Rotation, string Format, string OutputFolder);

/// <summary>Validates the complete batch, then creates independent jobs for videos that need rotation.</summary>
public static class BatchRotate
{
    public static string Direction(int rotation) => rotation switch
    {
        0 => "无需旋转", 90 => "顺时针 90°", 180 => "旋转 180°", 270 => "逆时针 90°",
        _ => throw new ArgumentException("请选择无需旋转、顺时针 90°、逆时针 90°或 180°。")
    };

    public static (int Width, int Height) OutputSize(MediaInfo info, int rotation)
    {
        ValidateVideo(info); _ = Direction(rotation);
        return rotation is 0 or 180 ? (info.Width, info.Height) : (info.Height, info.Width);
    }

    public static ConversionOptions ResolveOptions(MediaInfo info, int rotation, string format, string? sourcePath = null)
    {
        _ = OutputSize(info, rotation);
        var original = format == SourceVideoExport.Original;
        var fast = format == SourceVideoExport.FastRotation;
        format = SourceVideoExport.Format(format, sourcePath);
        if (!original && !fast && format is not ("mp4" or "mkv" or "webm" or "mov" or "avi"))
            throw new ArgumentException("请选择 MP4、MKV、WebM、MOV 或 AVI 输出。");
        var options = new ConversionOptions { Format = format, Rotation = fast ? 0 : rotation,
            PreserveSourceAttributes = original, CopyStreams = fast, LosslessRotation = fast ? rotation : null,
            VideoStreamIndex = info.VideoStreamIndex, AudioStreamIndex = info.AudioStreamIndex };
        MediaEngine.ValidateEncodingOptions(options);
        return options;
    }

    public static IReadOnlyList<Job> CreateJobs(BatchRotateRequest request, IEnumerable<string>? reserved = null)
    {
        if (request.Inputs.Count == 0) throw new ArgumentException("请先勾选要旋转的视频。");
        if (string.IsNullOrWhiteSpace(request.OutputFolder)) throw new ArgumentException("请选择输出目录。");
        var folder = Path.GetFullPath(request.OutputFolder);
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var unique = new HashSet<string>(comparison);
        // Validate every input before allocating output names or creating the output directory.
        var drafts = request.Inputs.Select(input =>
        {
            var path = Path.GetFullPath(input.Path);
            if (!unique.Add(path)) throw new ArgumentException("批量旋转列表包含重复文件。");
            try
            {
                var angle = input.Rotation ?? request.Rotation;
                var options = ResolveOptions(input.Info, angle, request.Format, path);
                MediaEngine.Validate(new() { FeatureId = "rotate", Inputs = [path], Options = options,
                    Output = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "_rotate" + angle + "." + options.Format) });
                return (Path: path, Options: options, Angle: angle);
            }
            catch (ArgumentException ex) { throw new ArgumentException(Path.GetFileName(path) + "：" + ex.Message, ex); }
        }).ToArray();
        var used = new HashSet<string>(reserved ?? [], comparison);
        foreach (var draft in drafts) used.Add(draft.Path);
        var jobs = new List<Job>();
        foreach (var draft in drafts.Where(d => d.Angle != 0))
        {
            var output = MediaEngine.UniqueOutput(folder, Path.GetFileNameWithoutExtension(draft.Path) + "_rotate" + draft.Angle,
                draft.Options.Format, used);
            jobs.Add(new() { FeatureId = "rotate", Inputs = [draft.Path], Options = draft.Options, Output = output });
            used.Add(output);
        }
        return jobs;
    }

    private static void ValidateVideo(MediaInfo info)
    {
        if (!info.HasVideo || info.Width < 2 || info.Height < 2 || !double.IsFinite(info.Duration) || info.Duration <= 0)
            throw new ArgumentException("文件不包含可旋转的视频画面。");
        if (info.VideoStreamIndex < 0 || info.AudioStreamIndex < 0)
            throw new ArgumentException("视频或音频轨索引无效。");
    }
}
