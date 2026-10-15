using System.Globalization;

namespace AvaMedia.Core;

/// <summary>Display text only; analysis and saved segment boundaries retain their full precision.</summary>
internal static class PersonClipPresentation
{
    internal static string Time(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "无效";
        var tenths = Math.Round(seconds * 10, MidpointRounding.AwayFromZero);
        if (tenths >= long.MaxValue) return "无效";
        var value = (long)tenths;
        var whole = value / 10;
        return whole >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}.{value % 10}")
            : string.Create(CultureInfo.InvariantCulture, $"{whole / 60:00}:{whole % 60:00}.{value % 10}");
    }

    internal static string Codec(string value) => value.ToLowerInvariant() switch
    { "h264" => "H.264", "hevc" or "h265" => "H.265", "av1" => "AV1", _ => value.ToUpperInvariant() };

    internal static string Bytes(long bytes) => bytes >= 1L << 30
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.#} GB")
        : bytes >= 1L << 20 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0} MB")
        : bytes >= 1024 ? $"{bytes / 1024} KB" : $"{bytes} B";

    internal static string Source(MediaInfo info) => $"{info.Width}×{info.Height} · {Codec(info.VideoCodec)} · {Time(info.Duration)}";
    internal static string Export(double seconds, long bytes) => $"{Time(seconds)} · {Bytes(bytes)}";

    internal static string Backend(IReadOnlyList<PersonDetectorStatistics> detectors)
    {
        var backends = detectors.Select(d => d.Backend).Distinct().ToArray();
        return backends.Length == 1 ? backends[0] : string.Join(" · ", detectors.Select(d => $"{d.Name} {d.Backend}"));
    }

    internal static string ModelDetail(IReadOnlyList<PersonDetectorStatistics> detectors) => detectors.Count == 1
        ? $"{detectors[0].Name} · {detectors[0].Backend}"
        : string.Join(" · ", detectors.Select(d => $"{d.Name} {d.Backend}"));

    internal static string Note(IReadOnlyList<PersonDetectorStatistics> detectors)
    {
        var notes = detectors.Where(d => !string.IsNullOrEmpty(d.FallbackReason)).Select(d => (d.Name, Text: Fallback(d.FallbackReason!))).ToArray();
        var distinct = notes.Select(n => n.Text).Distinct().ToArray();
        return distinct.Length == 1 ? distinct[0] : string.Join(" · ", notes.Select(n => $"{n.Name}：{n.Text}"));
    }

    internal static string Fallback(string reason) => reason switch
    {
        "No supported GPU execution provider on this platform." => "未检测到可用 GPU 执行器",
        "Core ML execution provider is unavailable." => "Core ML 执行器不可用",
        "DirectML execution provider is unavailable." => "DirectML 执行器不可用",
        _ => "GPU 执行失败，已回退 CPU：" + reason
    };
}
