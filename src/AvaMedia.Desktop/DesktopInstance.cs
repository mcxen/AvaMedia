using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AvaMedia.Desktop;

// One interactive desktop per OS user. Playback creates no conversion queue or services.
internal sealed class DesktopInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _pipeName;
    private readonly Action<string[]> _activate;
    private readonly Task _listener;
    internal static DesktopInstance? Current { get; private set; }

    private DesktopInstance(Mutex mutex, string name, Action<string[]> activate)
    {
        _mutex = mutex; _pipeName = name; _activate = activate;
        // Keep pipe continuations off Avalonia's synchronization context; shutdown joins this task.
        _listener = Task.Run(ListenAsync);
    }

    internal static bool TryStart(string[] arguments, Action<string[]> activate)
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var name = "AvaMedia.Desktop." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..24];
        var mutex = new Mutex(false, name);
        var request = arguments.Select(argument => File.Exists(argument) ? Path.GetFullPath(argument) : argument).ToArray();
        try
        {
            // The owner can still be starting or shutting down. Never start another UI on timeout.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (acquired)
                {
                    try { Current = new DesktopInstance(mutex, name, activate); return true; }
                    catch { mutex.ReleaseMutex(); throw; }
                }
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    pipe.ConnectAsync(500, timeout.Token).GetAwaiter().GetResult();
                    using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token).GetAwaiter().GetResult();
                    if (reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult() != "OK")
                        throw new IOException("已有实例未能接收打开请求。");
                    mutex.Dispose(); return false;
                }
                catch (TimeoutException) { }
                catch (IOException) when (!timeout.IsCancellationRequested) { }
            }
        }
        catch { mutex.Dispose(); throw; }
    }

    private async Task ListenAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_lifetime.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(timeout.Token);
                if (line is null || line.Length > 1024 * 1024) continue;
                if (JsonSerializer.Deserialize<string[]>(line) is not { } arguments || arguments.Any(value => value is null)) continue;
                _activate(arguments);
                await writer.WriteLineAsync("OK".AsMemory(), timeout.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or JsonException or OperationCanceledException)
            { AppDiagnostics.Record("Desktop activation", error); }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _listener.GetAwaiter().GetResult();
        _lifetime.Dispose(); _mutex.ReleaseMutex(); _mutex.Dispose(); Current = null;
    }
}
