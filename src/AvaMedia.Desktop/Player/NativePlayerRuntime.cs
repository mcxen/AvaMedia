using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaMedia.Desktop.Player;

internal sealed record PlayerRuntimeProgress(string Stage, long Received = 0, long Total = 0);

/// <summary>Official, pinned mpv releases. The runtime is downloaded once, outside the application package.</summary>
internal static class NativePlayerRuntime
{
    public const string Version = "0.41.0";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private sealed record Package(string Name, string Hash, long Size, string Executable);
    private static Package? Current => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? new("mpv-v0.41.0-x86_64-w64-mingw32.zip", "a49811c0752c108b8260636f9c6f6fcb97406641c98b30f1e7b500dfb20177de", 39002683, "mpv.exe")
        : OperatingSystem.IsMacOSVersionAtLeast(14) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? new("mpv-v0.41.0-macos-14-arm.zip", "5c96f9b21355fc0a11d2e2161ad65f33031070e9fb3f6bd9865fb459b94587e6", 43693888, "mpv.app/Contents/MacOS/mpv") : null;
    public static bool Supported => Current is not null || External() is not null;
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "Player", "mpv-" + Version);
    private static string? External()
    {
        var configured = Environment.GetEnvironmentVariable("AVAMEDIA_NATIVE_PLAYER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        return null;
    }
    public static async Task<string> EnsureAsync(IProgress<PlayerRuntimeProgress> progress, CancellationToken ct)
    {
        if (External() is { } external) return external;
        var package = Current ?? throw new PlatformNotSupportedException("原生播放运行包支持 Windows x64 和 macOS 14 以上的 Apple Silicon。可通过 AVAMEDIA_NATIVE_PLAYER 指定其它平台的 mpv。");
        await Gate.WaitAsync(ct);
        string? work = null;
        try
        {
            var executable = Path.Combine(Root, package.Executable);
            var marker = Path.Combine(Root, "runtime.json");
            if (File.Exists(executable) && File.Exists(marker))
            {
                try
                {
                    using var json = JsonDocument.Parse(await File.ReadAllTextAsync(marker, ct));
                    if (json.RootElement.TryGetProperty("sha256", out var hash) && hash.GetString() == package.Hash) return executable;
                }
                catch (JsonException) { }
            }
            work = Root + ".partial-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(work);
            var archive = Path.Combine(work, "download.zip");
            progress.Report(new("下载原生播放引擎", 0, package.Size));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://github.com/mpv-player/mpv/releases/download/v" + Version + "/" + package.Name);
            request.Headers.UserAgent.ParseAdd("AvaMedia/" + AvaMedia.Core.AppIdentity.Version);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920]; long received = 0, lastReport = 0; int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    received += count; if (received > package.Size) throw new InvalidDataException("原生播放引擎大小不匹配。");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    var now = Environment.TickCount64;
                    if (now - lastReport >= 150 || received == package.Size) { lastReport = now; progress.Report(new("下载原生播放引擎", received, package.Size)); }
                }
                if (received != package.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(package.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("原生播放引擎 SHA256 校验失败。");
            }
            progress.Report(new("准备原生播放引擎"));
            var destination = Path.Combine(work, "runtime"); Directory.CreateDirectory(destination);
            await Task.Run(() => Extract(archive, destination, ct), ct);
            if (!File.Exists(Path.Combine(destination, package.Executable))) throw new InvalidDataException("原生播放引擎缺少程序文件。");
            await File.WriteAllTextAsync(Path.Combine(destination, "runtime.json"), JsonSerializer.Serialize(new { version = Version, sha256 = package.Hash, source = "https://github.com/mpv-player/mpv" }), ct);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            Directory.Move(destination, Root);
            return executable;
        }
        finally
        {
            try { if (work is not null && Directory.Exists(work)) Directory.Delete(work, true); }
            catch (IOException error) { AppDiagnostics.Record("Native player download cleanup", error); }
            finally { Gate.Release(); }
        }
    }
    private static void Extract(string archive, string destination, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);
        // The official GitHub assets wrap the platform archive inside a ZIP.
        if (zip.Entries.Count != 1) throw new InvalidDataException("原生播放引擎归档结构无效。");
        var entry = zip.Entries[0]; ct.ThrowIfCancellationRequested();
        using var stream = entry.Open();
        if (OperatingSystem.IsWindows())
        {
            using var nestedBytes = new MemoryStream(); stream.CopyTo(nestedBytes); nestedBytes.Position = 0;
            using var nested = new ZipArchive(nestedBytes, ZipArchiveMode.Read);
            nested.ExtractToDirectory(destination);
        }
        else
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: false);
            var executable = Path.Combine(destination, "mpv.app/Contents/MacOS/mpv");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        ct.ThrowIfCancellationRequested();
    }
}
