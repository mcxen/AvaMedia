namespace AvaMedia.Core;

public enum BatchCropMode { Pixels, Relative }
public sealed record CropArea(int X, int Y, int Width, int Height);
public sealed record BatchCropInput(string Path, MediaInfo Info);
public sealed record BatchCropRequest(IReadOnlyList<BatchCropInput> Inputs, CropArea Area,
    MediaInfo Reference, BatchCropMode Mode, ConversionOptions Options, string OutputFolder);

/// <summary>Resolves one shared region and validates a complete batch before creating jobs.</summary>
public static class BatchCrop
{
    public static void ValidateArea(CropArea area, MediaInfo media)
    {
        if (!media.HasVideo || media.Width < 2 || media.Height < 2)
            throw new ArgumentException("文件不包含可裁剪的视频画面。");
        if (area.X < 0 || area.Y < 0 || area.Width < 2 || area.Height < 2)
            throw new ArgumentException("裁剪坐标不能为负，宽度和高度至少为 2 像素。");
        if ((area.X | area.Y | area.Width | area.Height) % 2 != 0)
            throw new ArgumentException("裁剪 X、Y、宽度和高度须为偶数像素。");
        if ((long)area.X + area.Width > media.Width || (long)area.Y + area.Height > media.Height)
            throw new ArgumentException($"裁剪区域超出 {media.Width} × {media.Height} 画面。");
    }

    public static CropArea Resolve(CropArea area, MediaInfo reference, MediaInfo target, BatchCropMode mode)
    {
        ValidateArea(area, reference);
        if (!Enum.IsDefined(mode)) throw new ArgumentException("裁剪应用方式无效。");
        var resolved = area;
        if (mode == BatchCropMode.Relative)
        {
            if (!target.HasVideo || target.Width < 2 || target.Height < 2)
                throw new ArgumentException("文件不包含可裁剪的视频画面。");
            var left = EvenFloor((double)area.X / reference.Width * target.Width);
            var top = EvenFloor((double)area.Y / reference.Height * target.Height);
            var right = EvenFloor(((double)area.X + area.Width) / reference.Width * target.Width);
            var bottom = EvenFloor(((double)area.Y + area.Height) / reference.Height * target.Height);
            resolved = new(left, top, right - left, bottom - top);
        }
        ValidateArea(resolved, target);
        return resolved;
    }

    public static ConversionOptions ResolveOptions(CropArea area, MediaInfo reference, MediaInfo target,
        BatchCropMode mode, ConversionOptions defaults)
    {
        if (reference.VideoStreamIndex != defaults.VideoStreamIndex || target.VideoStreamIndex != defaults.VideoStreamIndex)
            throw new ArgumentException("媒体信息与所选视频轨不一致，请重新读取视频。");
        var resolved = Resolve(area, reference, target, mode);
        var options = defaults.Clone();
        options.CropX = resolved.X; options.CropY = resolved.Y;
        options.CropWidth = resolved.Width; options.CropHeight = resolved.Height;
        if (options.CopyStreams || options.VideoCodec == "copy")
            throw new ArgumentException("画面裁剪需要重新编码，请关闭视频流复制。");
        if (options.Format is not ("mp4" or "mkv" or "webm" or "mov" or "avi"))
            throw new ArgumentException("请选择 MP4、MKV、WebM、MOV 或 AVI 输出。");
        MediaEngine.ValidateEncodingOptions(options);
        return options;
    }

    public static IReadOnlyList<Job> CreateJobs(BatchCropRequest request, IEnumerable<string>? reserved = null)
    {
        if (request.Inputs.Count == 0) throw new ArgumentException("请先勾选要裁剪的视频。");
        if (string.IsNullOrWhiteSpace(request.OutputFolder)) throw new ArgumentException("请选择输出目录。");
        var folder = Path.GetFullPath(request.OutputFolder);
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var unique = new HashSet<string>(comparison);
        // This pass has no output-directory side effects and rejects the whole invalid draft.
        var drafts = request.Inputs.Select(input =>
        {
            var path = Path.GetFullPath(input.Path);
            if (!unique.Add(path)) throw new ArgumentException("批量裁剪列表包含重复文件。");
            try
            {
                var options = ResolveOptions(request.Area, request.Reference, input.Info, request.Mode, request.Options);
                MediaEngine.Validate(new() { FeatureId = "crop", Inputs = [path], Options = options,
                    Output = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "_crop." + options.Format) });
                return (Path: path, Options: options);
            }
            catch (ArgumentException ex) { throw new ArgumentException(Path.GetFileName(path) + "：" + ex.Message, ex); }
        }).ToArray();
        var used = new HashSet<string>(reserved ?? [], comparison);
        // Also reserve sources from the entire batch, including files without a _crop suffix.
        foreach (var draft in drafts) used.Add(draft.Path);
        var jobs = new List<Job>();
        foreach (var draft in drafts)
        {
            var output = MediaEngine.UniqueOutput(folder, Path.GetFileNameWithoutExtension(draft.Path) + "_crop",
                draft.Options.Format, used);
            jobs.Add(new() { FeatureId = "crop", Inputs = [draft.Path], Options = draft.Options, Output = output });
            used.Add(output);
        }
        return jobs;
    }

    public static int EvenFloor(double value) => (int)(Math.Floor(value / 2) * 2);
}
