using System.Text.Json;

namespace AvaMedia.Core.Compute;

public sealed record ThroughputKey(string Tool, ComputeBackend Backend, string ResolutionBand, string Codec = "");
public sealed record ThroughputSample(ThroughputKey Key, double Rate, int Observations, DateTimeOffset UpdatedAtUtc);

/// <summary>Media seconds / wall second (or frames / second for inference). Separate backend histories.</summary>
public sealed class ThroughputModel
{
    private readonly IComputeFiles _files;
    private readonly IComputeClock _clock;
    private readonly string _path;
    private readonly Dictionary<ThroughputKey, ThroughputSample> _samples = [];
    private readonly object _gate = new();
    public ThroughputModel(IComputeFiles? files = null, IComputeClock? clock = null, string? path = null)
    {
        _files = files ?? new ComputeFiles(); _clock = clock ?? new SystemComputeClock();
        _path = path ?? Path.Combine(ComputeEnvironment.Folder, "compute-throughput.json");
        try
        {
            if (_files.Read(_path) is { } json)
                foreach (var sample in JsonSerializer.Deserialize<ThroughputSample[]>(json, ComputeJson.Options) ?? [])
                    if (sample?.Key is not null && Valid(sample.Rate) && sample.Observations > 0) _samples[sample.Key] = sample;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        { ComputeRuntimeFormatter.Log("吞吐缓存不可读，使用保守先验：" + e.Message); }
    }
    public static string ResolutionBand(int width, int height) => ((long)width * height) switch
    { <= 1280L * 720 => "720p", <= 1920L * 1080 => "1080p", <= 3840L * 2160 => "2160p", _ => "above2160p" };
    public static double Prior(ThroughputKey key)
    {
        var scale = key.ResolutionBand switch { "720p" => 1.5, "2160p" => .25, "above2160p" => .1, _ => 1 };
        var codec = key.Codec.Contains("265", StringComparison.OrdinalIgnoreCase) || key.Codec.Contains("hevc", StringComparison.OrdinalIgnoreCase) ? .5
            : key.Codec.Contains("av1", StringComparison.OrdinalIgnoreCase) ? .25 : 1;
        if (key.Tool is "ONNX" or "抽帧推理") return key.Backend == ComputeBackend.Cpu ? 1 : 4;
        return (key.Backend == ComputeBackend.Cpu ? .8 : 4) * scale * codec;
    }
    private static bool Valid(double value) => double.IsFinite(value) && value > 0 && value <= 1000000;
    public double Rate(ThroughputKey key) { lock (_gate) return _samples.TryGetValue(key, out var sample) ? sample.Rate : Prior(key); }
    public void Observe(ThroughputKey key, double work, TimeSpan elapsed)
    {
        var rate = work / elapsed.TotalSeconds;
        if (!Valid(rate) || !double.IsFinite(work) || work <= 0 || elapsed.TotalSeconds < .1) return;
        lock (_gate)
        {
            var previous = _samples.GetValueOrDefault(key);
            var ewma = previous is null ? Prior(key) * .7 + rate * .3 : previous.Rate * .7 + rate * .3;
            _samples[key] = new(key, ewma, Math.Min(int.MaxValue - 1, previous?.Observations ?? 0) + 1, _clock.UtcNow);
            try { _files.Write(_path, JsonSerializer.Serialize(_samples.Values.TakeLast(512).ToArray(), ComputeJson.Options)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ComputeRuntimeFormatter.Log("吞吐缓存保存失败：" + e.Message); }
        }
    }
}
