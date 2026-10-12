namespace AvaMedia.Core;

public interface IOcrEngine
{
    bool Available { get; }
    Task<string> ReadAsync(byte[] image, CancellationToken ct);
}
public sealed class NullOcrEngine : IOcrEngine
{
    public bool Available => false;
    public Task<string> ReadAsync(byte[] image, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(""); }
}
