
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
    public static async Task<IReadOnlyList<HardwareEncoderResult>> TestAsync(string executable, CancellationToken token = default,
        bool refresh = true, Action<HardwareEncoderResult>? progress = null)
    {
        var cache = Compute.ComputeEnvironment.Capabilities;
        var fingerprint = Compute.ComputeEnvironment.Fingerprint(executable);
        var probe = new Compute.FfmpegEncoderProbe(executable);
        var results = new List<HardwareEncoderResult>();
        foreach (var encoder in Encoders)
        {
            var request = new Compute.ComputeRequest("FFmpeg 编码", Compute.ComputeCapability.VideoEncode, encoder.Codec);
            var result = await cache.GetAsync(request, Compute.FfmpegBackend.ForEncoder(encoder.Codec), fingerprint, probe, token, refresh).ConfigureAwait(false);
            var report = new HardwareEncoderResult(encoder.Name, encoder.Codec, result.Available, result.Reason);
            results.Add(report); progress?.Invoke(report);
        }
        return results.AsReadOnly();
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
