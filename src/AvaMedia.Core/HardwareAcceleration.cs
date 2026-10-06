using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AvaMedia.Core;

public sealed record HardwareEncoder(string Name, string Codec, HardwareVideoFormat Format = HardwareVideoFormat.H264);
public sealed record HardwareEncoderResult(string Name, string Codec, bool Supported, string Detail)
{
    public string Summary => Name + (Supported ? " is supported" : " is NOT supported");
}

public static class HardwareAcceleration
{
    public static IReadOnlyList<HardwareEncoder> Encoders { get; } = Array.AsReadOnly(HardwareTranscoding.Backends
        .Where(backend => backend.IsAvailableOnPlatform).SelectMany(backend => backend.Encoders).ToArray());
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
                        var backend = HardwareTranscoding.Backend(encoder.Codec)!;
                        var test = await ProcessRunner.Run(file, ["-hide_banner", "-v", "error", "-nostdin", ..backend.InitializationArguments, "-f", "lavfi", "-i",
                            "color=c=black:s=1280x720:r=25", "-frames:v", "3", "-an", "-c:v", encoder.Codec,
                            "-pix_fmt", "nv12", ..backend.EncodingArguments(encoder, new(23, 1280, 720, 25, false)), "-f", "null", "-"], timeout.Token).ConfigureAwait(false);
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

    public static IReadOnlyList<string> CompatibleCodecs(string format) => Encoders
        .Where(encoder => HardwareTranscoding.Compatible(format, encoder.Format)).OrderBy(encoder => encoder.Format)
        .Select(encoder => encoder.Codec).ToArray();

    public static IReadOnlyList<string> Candidates(string format, IReadOnlyList<HardwareEncoderResult> report, string sourceCodec = "")
    {
        var supported = report.Where(r => r.Supported).Select(r => r.Codec).ToHashSet(StringComparer.Ordinal);
        var source = HardwareTranscoding.VideoFormat(sourceCodec);
        return CompatibleCodecs(format).Where(supported.Contains)
            .OrderBy(codec => HardwareTranscoding.Encoder(codec)?.Format == source ? 0 : 1).ToArray();
    }

    public static string? SelectCodec(string format, IReadOnlyList<HardwareEncoderResult> report) => Candidates(format, report).FirstOrDefault();

    private static IReadOnlyList<HardwareEncoderResult> Unavailable(string detail, Action<HardwareEncoderResult>? progress)
    {
        var results = Encoders.Select(e => new HardwareEncoderResult(e.Name, e.Codec, false, detail)).ToArray();
        foreach (var result in results) progress?.Invoke(result); return results;
    }
}
