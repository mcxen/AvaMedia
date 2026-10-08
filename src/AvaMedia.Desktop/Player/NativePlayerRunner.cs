using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Player;

internal enum DiscKind { Bluray, Dvd }
internal sealed record DiscPlayback(string Path, DiscKind Kind, int Title = 0);

internal static class NativePlayerRunner
{
    public static IReadOnlyList<string> Arguments(string input, double position, double speed, double volume, bool muted, bool playing,
        bool sdr, DiscPlayback? disc = null)
    {
        List<string> args = ["--no-config", "--load-scripts=no", "--osc=yes", "--osd-level=1", "--idle=no", "--keep-open=yes",
            "--title=" + AppIdentity.PlayerTitle, "--vo=gpu-next", "--hwdec=auto", "--vd-lavc-threads=0", "--video-sync=audio",
            "--cache=yes", "--demuxer-max-bytes=128MiB", "--demuxer-max-back-bytes=32MiB", "--target-colorspace-hint=auto",
            "--tone-mapping=auto", "--hdr-compute-peak=auto", "--interpolation=no", "--deinterlace=auto",
            "--speed=" + MediaEngine.Number(speed), "--volume=" + MediaEngine.Number(volume), "--mute=" + (muted ? "yes" : "no"),
            "--pause=" + (playing ? "no" : "yes"), "--start=" + MediaEngine.Number(position), "--screenshot-format=png"];
        if (OperatingSystem.IsWindows()) args.Add("--gpu-context=d3d11");
        if (sdr) args.AddRange(["--target-colorspace-hint=no", "--target-trc=bt.1886", "--target-prim=bt.709", "--target-peak=100"]);
        if (disc is { } source)
        {
            args.Add((source.Kind == DiscKind.Bluray ? "--bluray-device=" : "--dvd-device=") + source.Path);
            input = source.Kind == DiscKind.Bluray ? "bd://" + (source.Title == 0 ? "longest" : source.Title.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : "dvd://" + (source.Title == 0 ? "" : source.Title.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        args.Add("--"); args.Add(input); return args;
    }
    public static async Task RunAsync(string executable, IReadOnlyList<string> arguments, Action started, Action firstFrame,
        Action<string, JsonElement> propertyChanged, Func<CancellationToken, Task<string[]>> remainingFiles, CancellationToken ct)
    {
        var ipcName = "avamedia-" + Guid.NewGuid().ToString("N");
        using var socketDirectory = OperatingSystem.IsWindows() ? null : new PrivateSocketDirectory(ipcName);
        var endpoint = OperatingSystem.IsWindows() ? @"\\.\pipe\" + ipcName : Path.Combine(socketDirectory!.Path, "ipc");
        var args = arguments.ToList(); args.Insert(0, "--input-ipc-server=" + endpoint);
        using var process = ProcessRunner.Start(executable, args);
        using var registration = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(ct);
        started();
        // Keep diagnostics bounded even for multi-hour sessions.
        var errors = new Queue<string>();
        var errorTask = Drain(process.StandardError, errors); var outputTask = Drain(process.StandardOutput, null);
        var monitor = MonitorAsync(process, ipcName, endpoint, firstFrame, propertyChanged, remainingFiles, monitoring.Token);
        try { await process.WaitForExitAsync(); }
        finally { monitoring.Cancel(); }
        await Task.WhenAll(errorTask, outputTask, monitor);
        if (!OperatingSystem.IsWindows()) { try { File.Delete(endpoint); } catch (IOException) { } }
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException(errors.Count > 0 ? string.Join('\n', errors)
            : Localization.Format($"原生播放进程退出，代码 {process.ExitCode}。"));
    }
    private static async Task MonitorAsync(Process process, string name, string endpoint, Action firstFrame,
        Action<string, JsonElement> propertyChanged, Func<CancellationToken, Task<string[]>> remainingFiles, CancellationToken ct)
    {
        Stream? stream = null;
        try
        {
            for (var attempt = 0; attempt < 100 && !process.HasExited; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                        try { await pipe.ConnectAsync(100, ct); stream = pipe; } catch { pipe.Dispose(); throw; }
                    }
                    else
                    {
                        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), ct); stream = new NetworkStream(socket, ownsSocket: true); }
                        catch { socket.Dispose(); throw; }
                    }
                    break;
                }
                catch (Exception error) when (error is IOException or SocketException or TimeoutException) { await Task.Delay(50, ct); }
            }
            if (stream is null) return;
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var properties = new[] { "time-pos", "path", "duration", "pause", "hwdec-current", "video-params", "video-out-params", "vo-drop-frame-count" };
            for (var index = 0; index < properties.Length; index++)
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { command = new object[] { "observe_property", index + 1, properties[index] } }).AsMemory(), ct);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var append = AppendPlaylist();
            try
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    using var json = JsonDocument.Parse(line); var root = json.RootElement;
                    if (!root.TryGetProperty("event", out var eventName)) continue;
                    if (eventName.GetString() == "playback-restart") SignalReady();
                    else if (eventName.GetString() == "property-change" && root.TryGetProperty("name", out var property) && root.TryGetProperty("data", out var data))
                    {
                        propertyChanged(property.GetString()!, data.Clone());
                        // Observations include the current state, even if playback-restart preceded the IPC connection.
                        if (property.GetString() == "video-out-params" && data.ValueKind == JsonValueKind.Object) SignalReady();
                    }
                }
            }
            finally { ready.TrySetCanceled(); try { await append; } catch (OperationCanceledException) { } }
            void SignalReady() { if (ready.TrySetResult()) firstFrame(); }
            async Task AppendPlaylist()
            {
                await ready.Task.WaitAsync(ct);
                foreach (var file in await remainingFiles(ct))
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { command = new object[] { "loadfile", file, "append", -1, new { start = "0" } } }).AsMemory(), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or SocketException or JsonException) { AppDiagnostics.Record("Native player monitoring", error); }
        finally { if (stream is not null) await stream.DisposeAsync(); }
    }
    private static async Task Drain(StreamReader reader, Queue<string>? retained)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (retained is null) continue;
            retained.Enqueue(line.Length > 1200 ? line[..1200] : line);
            while (retained.Count > 20) retained.Dequeue();
        }
    }
    private sealed class PrivateSocketDirectory : IDisposable
    {
        public string Path { get; }
        public PrivateSocketDirectory(string name)
        {
            Path = System.IO.Path.Combine("/tmp", name); Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }
    public static DiscPlayback? Detect(string path)
    {
        if (File.Exists(path) && Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase)) return new(path, DiscKind.Bluray);
        if (!Directory.Exists(path)) return null;
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).Equals("BDMV", StringComparison.OrdinalIgnoreCase)) return new(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))!, DiscKind.Bluray);
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase)) return new(path, DiscKind.Dvd);
        if (Directory.Exists(Path.Combine(path, "BDMV"))) return new(path, DiscKind.Bluray);
        if (Directory.Exists(Path.Combine(path, "VIDEO_TS"))) return new(path, DiscKind.Dvd);
        return null;
    }
}
