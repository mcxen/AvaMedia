using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public sealed record HardwareEncoder(string Name, string Codec);
public sealed record HardwareEncoderResult(string Name, string Codec, bool Supported, string Detail)
{
    public string Summary => Name + (Supported ? " is supported" : " is NOT supported");
}

public static class HardwareAcceleration
{
    public static IReadOnlyList<HardwareEncoder> Encoders { get; } = new HardwareEncoder[]
    {
        new("NV H264", "h264_nvenc"), new("NV H265", "hevc_nvenc"),
        new("AMF H264", "h264_amf"), new("AMF H265", "hevc_amf"),
        new("Intel QSV H264", "h264_qsv"), new("Intel QSV H265", "hevc_qsv"), new("Intel QSV VP9", "vp9_qsv")
    };
    private static readonly ConcurrentDictionary<string, IReadOnlyList<HardwareEncoderResult>> Cache = new();
    private static readonly SemaphoreSlim Gate = new(1);

    public static async Task<IReadOnlyList<HardwareEncoderResult>> TestAsync(string executable, CancellationToken token = default,
        bool refresh = true, Action<HardwareEncoderResult>? progress = null)
    {
        var file = Path.GetFullPath(executable);
        var key = file + "|" + (File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0);
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!refresh && Cache.TryGetValue(key, out var cached)) return cached;
            ProcessResult listing;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                try { listing = await ProcessRunner.Run(file, ["-hide_banner", "-encoders"], timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { return Unavailable("读取 FFmpeg 编码器列表超时。", progress); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { return Unavailable("无法启动 FFmpeg：" + ex.Message, progress); }
            }
            if (listing.ExitCode != 0) return Unavailable("无法读取 FFmpeg 编码器列表：" + listing.Error, progress);
            var available = Regex.Matches(listing.Output + "\n" + listing.Error, @"(?m)^\s*V[A-Z\.]{5}\s+(\S+)")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var results = new List<HardwareEncoderResult>();
            foreach (var encoder in Encoders)
            {
                token.ThrowIfCancellationRequested();
                HardwareEncoderResult result;
                if (!available.Contains(encoder.Codec)) result = new(encoder.Name, encoder.Codec, false, "当前 FFmpeg 构建不包含此编码器。");
                else
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(6));
                    try
                    {
                        var test = await ProcessRunner.Run(file, ["-hide_banner", "-v", "error", "-nostdin", "-f", "lavfi", "-i",
                            "color=c=black:s=1280x720:r=25", "-frames:v", "3", "-an", "-c:v", encoder.Codec,
                            "-pix_fmt", "nv12", "-f", "null", "-"], timeout.Token).ConfigureAwait(false);
                        result = new(encoder.Name, encoder.Codec, test.ExitCode == 0,
                            test.ExitCode == 0 ? "已成功编码 3 帧测试画面。" : test.Error.Trim());
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    { result = new(encoder.Name, encoder.Codec, false, "硬件编码测试超过 6 秒，已终止。"); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { result = new(encoder.Name, encoder.Codec, false, ex.Message); }
                }
                results.Add(result); progress?.Invoke(result);
            }
            Cache[key] = results.AsReadOnly(); return Cache[key];
        }
        finally { Gate.Release(); }
    }

    public static IReadOnlyList<string> CompatibleCodecs(string format) => format switch
    {
        "mp4" or "mov" or "m4v" or "ts" => ["h264_nvenc", "h264_amf", "h264_qsv", "hevc_nvenc", "hevc_amf", "hevc_qsv"],
        "mkv" => ["h264_nvenc", "h264_amf", "h264_qsv", "hevc_nvenc", "hevc_amf", "hevc_qsv", "vp9_qsv"],
        "avi" or "flv" => ["h264_nvenc", "h264_amf", "h264_qsv"],
        "webm" => ["vp9_qsv"],
        _ => []
    };

    public static IReadOnlyList<string> Candidates(string format, IReadOnlyList<HardwareEncoderResult> report)
    {
        var supported = report.Where(r => r.Supported).Select(r => r.Codec).ToHashSet(StringComparer.Ordinal);
        return CompatibleCodecs(format).Where(supported.Contains).ToArray();
    }

    public static string? SelectCodec(string format, IReadOnlyList<HardwareEncoderResult> report) => Candidates(format, report).FirstOrDefault();

    private static IReadOnlyList<HardwareEncoderResult> Unavailable(string detail, Action<HardwareEncoderResult>? progress)
    {
        var results = Encoders.Select(e => new HardwareEncoderResult(e.Name, e.Codec, false, detail)).ToArray();
        foreach (var result in results) progress?.Invoke(result); return results;
    }
}
