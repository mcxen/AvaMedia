using System.Text.Json;

namespace AvaMedia.Core.Compute;

public interface IComputeProbe
{
    Task<CapabilityResult> ProbeAsync(ComputeRequest request, ComputeBackend backend, string fingerprint, TimeSpan timeout, CancellationToken ct);
}

public sealed class ComputeCapabilities
{
    private readonly IComputeFiles _files;
    private readonly IComputeClock _clock;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, CapabilityResult> _results = new();
    private readonly HashSet<string> _sessionFailures = [];
    public ComputeCapabilities(IComputeFiles? files = null, IComputeClock? clock = null, string? path = null)
    {
        _files = files ?? new ComputeFiles(); _clock = clock ?? new SystemComputeClock();
        _path = path ?? Path.Combine(ComputeEnvironment.Folder, "compute-capabilities.json");
        try
        {
            if (_files.Read(_path) is { } json)
                foreach (var value in JsonSerializer.Deserialize<CapabilityResult[]>(json, ComputeJson.Options) ?? [])
                    if (value is not null && Enum.IsDefined(value.Backend) && Enum.IsDefined(value.Capability)) _results[Key(value.Capability, value.Backend, value.Variant, value.Fingerprint)] = value;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        { ComputeRuntimeFormatter.Log("计算能力缓存不可读，将重新探测：" + e.Message); }
    }
    private static string Key(ComputeCapability capability, ComputeBackend backend, string variant, string fingerprint)
        => string.Join('|', capability, backend, variant, fingerprint);
    public static TimeSpan ValidFor(CapabilityResult result) => result.Available ? TimeSpan.FromDays(7)
        : result.Failure == ProbeFailure.Timeout ? TimeSpan.FromHours(1) : result.Failure == ProbeFailure.Runtime ? TimeSpan.FromMinutes(10) : TimeSpan.FromDays(1);
    public async Task<CapabilityResult> GetAsync(ComputeRequest request, ComputeBackend backend, string fingerprint,
        IComputeProbe probe, CancellationToken ct = default, bool refresh = false)
    {
        var key = Key(request.Capability, backend, request.CodecOrModel, fingerprint);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && _results.TryGetValue(key, out var cached) &&
                (_sessionFailures.Contains(key) || _clock.UtcNow >= cached.ProbedAtUtc && _clock.UtcNow - cached.ProbedAtUtc < ValidFor(cached))) return cached;
            CapabilityResult result;
            async Task<CapabilityResult> Attempt(TimeSpan timeout)
            {
                try { return await probe.ProbeAsync(request, backend, fingerprint, timeout, ct).ConfigureAwait(false); }
                catch (TimeoutException e) { return new(request.Capability, backend, false, e.Message, _clock.UtcNow, fingerprint, request.CodecOrModel, ProbeFailure.Timeout); }
                catch (Exception e) when (e is not OperationCanceledException)
                { return new(request.Capability, backend, false, e.Message, _clock.UtcNow, fingerprint, request.CodecOrModel, ProbeFailure.Unavailable); }
            }
            result = await Attempt(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            if (result.Failure == ProbeFailure.Timeout) result = await Attempt(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            result = result with { ProbedAtUtc = _clock.UtcNow, Fingerprint = fingerprint, Variant = request.CodecOrModel, Capability = request.Capability, Backend = backend };
            _results[key] = result; _sessionFailures.Remove(key); Save(); return result;
        }
        finally { _gate.Release(); }
    }
    public void ReportFailure(ComputeRequest request, ComputeBackend backend, string fingerprint, string reason)
    {
        _gate.Wait();
        try
        {
            var key = Key(request.Capability, backend, request.CodecOrModel, fingerprint);
            _results[key] = new(request.Capability, backend, false, reason, _clock.UtcNow, fingerprint, request.CodecOrModel, ProbeFailure.Runtime);
            _sessionFailures.Add(key); Save();
        }
        finally { _gate.Release(); }
    }
    public void Invalidate()
    { _gate.Wait(); try { _results.Clear(); _sessionFailures.Clear(); Save(); } finally { _gate.Release(); } }
    private void Save()
    {
        try { _files.Write(_path, JsonSerializer.Serialize(_results.Values.TakeLast(512).ToArray(), ComputeJson.Options)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ComputeRuntimeFormatter.Log("计算能力缓存保存失败：" + e.Message); }
    }
}
