namespace AvaMedia.Core;

public static class SettingsPolicy
{
    public static void Validate(AppSettings settings)
    {
        settings.OnlineAi.Validate(requireModel: false);
        if (string.IsNullOrWhiteSpace(settings.OutputFolder)) throw new ArgumentException("请选择输出目录。");
        _ = Path.GetFullPath(settings.OutputFolder);
        if (settings.CpuThreads is < 1 or > 16) throw new ArgumentException("多线程数量必须在 1 到 16 之间。");
        if (settings.ParallelJobs is < 1 or > 8) throw new ArgumentException("并行任务数必须在 1 到 8 之间。");
        if (settings.JpegQuality is < 1 or > 100 || settings.WebpQuality is < 1 or > 100)
            throw new ArgumentException("JPG / WebP Quality 必须在 1 到 100 之间。");
        foreach (var path in new[] { settings.FFmpegPath, settings.FFprobePath, settings.YtDlpPath })
            if (!MediaEngine.UsesBundledTools && !string.IsNullOrWhiteSpace(path) && !File.Exists(path)) throw new FileNotFoundException("工具路径不存在。", path);
    }

    public static ConversionOptions Resolve(ConversionOptions source, AppSettings settings)
    {
        var options = source.Clone();
        if (!settings.MultiThread) options.Threads = 1;
        else if (options.Threads == 0) options.Threads = Math.Clamp(settings.CpuThreads, 1, 16);
        // Keep legacy explicitly edited quality values; the original default now follows app settings.
        if (options.ImageQuality is null && options.Quality == 23)
        {
            if (options.Format == "jpg") options.ImageQuality = Math.Clamp(settings.JpegQuality, 1, 100);
            if (options.Format == "webp") options.ImageQuality = Math.Clamp(settings.WebpQuality, 1, 100);
        }
        return options;
    }
}
