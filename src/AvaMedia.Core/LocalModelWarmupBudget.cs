using System.Diagnostics;
using System.Security.Cryptography;

namespace AvaMedia.Core;

/// <summary>Background duty cycle; native kernels cannot be assigned a hard utilization quota.</summary>
public sealed class LocalModelWarmupBudget(AppSettings settings)
{
    private static readonly AsyncLocal<LocalModelWarmupBudget?> Active = new();
    private long _selectedAt;
    private int _foreground;
    public void SelectMedia(bool selected)
    {
        if (selected) Interlocked.CompareExchange(ref _selectedAt, Stopwatch.GetTimestamp(), 0);
        else Interlocked.Exchange(ref _selectedAt, 0);
    }
    public void Promote() => Volatile.Write(ref _foreground, 1);
    internal void BeginPreparation() => Volatile.Write(ref _foreground, 0);
    internal bool Foreground => Volatile.Read(ref _foreground) != 0;
    private double Percent(bool gpu)
    {
        if (Volatile.Read(ref _foreground) != 0) return 100;
        var initial = Math.Clamp(gpu ? settings.ModelWarmupInitialGpuPercent : settings.ModelWarmupInitialCpuPercent, 1, 100);
        var maximum = Math.Clamp(gpu ? settings.ModelWarmupGpuPercent : settings.ModelWarmupCpuPercent, initial, 100);
        var selected = Volatile.Read(ref _selectedAt);
        var fraction = selected == 0 ? 0 : Math.Clamp(Stopwatch.GetElapsedTime(selected).TotalSeconds
            / Math.Clamp(settings.ModelWarmupRampSeconds, 1, 30), 0, 1);
        return initial + (maximum - initial) * fraction;
    }
    internal IDisposable Enter()
    {
        var previous = Active.Value; Active.Value = this;
        return new Scope(() => Active.Value = previous);
    }
    private sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }
    internal async Task PaceAsync(long started, bool gpu, CancellationToken ct)
    {
        var work = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var restStarted = Stopwatch.GetTimestamp();
        while (true)
        {
            var percent = gpu ? Math.Min(Percent(false), Percent(true)) : Percent(false);
            var rest = work * (100 / percent - 1) - Stopwatch.GetElapsedTime(restStarted).TotalMilliseconds;
            if (rest <= 0) return;
            await Task.Delay((int)Math.Clamp(rest, 1, 100), ct).ConfigureAwait(false);
        }
    }
    internal static async Task<byte[]> HashAsync(Stream stream, CancellationToken ct)
    {
        if (Active.Value is not { } budget) return await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[1024 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(bytes, ct).ConfigureAwait(false);
            if (count == 0) break;
            var started = Stopwatch.GetTimestamp();
            hash.AppendData(bytes, 0, count);
            await budget.PaceAsync(started, gpu: false, ct).ConfigureAwait(false);
        }
        return hash.GetHashAndReset();
    }
}
