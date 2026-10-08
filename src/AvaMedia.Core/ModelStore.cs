using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record ModelDownloadProgress(long Received, long Total, string Stage)
{
    public int Percent => Total == 0 ? 0 : (int)Math.Clamp(Received * 100 / Total, 0, 100);
}

public sealed class ModelLease(string directory, SemaphoreSlim gate) : IDisposable
{
    public string Directory { get; } = directory;
    private SemaphoreSlim? _gate = gate;
    public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
}

/// <summary>Shared download/use/delete locks, resumable staging, verified atomic publication.</summary>
public sealed class ModelStore(string? root = null)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private sealed record InstalledFile(string Path, long Size, string Sha256);
    public string Root { get; } = Path.GetFullPath(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "models"));
    public string DirectoryFor(string id) { _ = ModelCatalog.Find(id); return Path.Combine(Root, id); }
    public string FileFor(string id, string file) => SafePath(DirectoryFor(id), file);
    private SemaphoreSlim Gate(string id) => Gates.GetOrAdd(DirectoryFor(id), _ => new(1, 1));
    public bool IsBusy(string id) => Gate(id).CurrentCount == 0;
    public bool HasLocalData(string id) => Directory.Exists(DirectoryFor(id)) || Directory.Exists(DirectoryFor(id) + ".download");

    public async Task<bool> IsInstalledAsync(string id, bool verify = false, CancellationToken ct = default)
    {
        var model = ModelCatalog.Find(id);
        if (!model.Supported) return false;
        try
        {
            var folder = DirectoryFor(id);
            var manifest = JsonSerializer.Deserialize<InstalledFile[]>(await File.ReadAllTextAsync(Path.Combine(folder, "installed.json"), ct));
            if (manifest is null || manifest.Length < model.Files.Count) return false;
            foreach (var artifact in model.Files)
                if (!manifest.Any(file => file.Path == artifact.Path && file.Size == artifact.Size && file.Sha256 == artifact.Sha256)) return false;
            foreach (var file in manifest)
                if (!await MatchesAsync(SafePath(folder, file.Path), file.Size, file.Sha256, verify, ct)) return false;
            return id != ModelCatalog.EmbeddingId || FindRuntime(folder) is not null;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { return false; }
    }

    public async Task<ModelLease> AcquireAsync(string id, CancellationToken ct = default)
    {
        var gate = Gate(id);
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("模型正在下载或使用，请稍后重试。");
        try
        {
            if (!await IsInstalledAsync(id, true, ct)) throw new InvalidOperationException("请在选项的模型管理中下载或修复所需模型。");
            return new(DirectoryFor(id), gate);
        }
        catch { gate.Release(); throw; }
    }

    public async Task DownloadAsync(string id, IProgress<ModelDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var model = ModelCatalog.Find(id);
        if (!model.Supported) throw new PlatformNotSupportedException("当前平台不支持此模型的本地推理工具。");
        var gate = Gate(id);
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("模型正在下载或使用，请稍后重试。");
        try
        {
            progress?.Report(new(0, model.DownloadSize, "校验模型"));
            if (await IsInstalledAsync(id, true, ct)) return;
            var staging = DirectoryFor(id) + ".download";
            Directory.CreateDirectory(staging);
            using var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20) }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AvaMedia/" + AppIdentity.Version);
            long completed = 0;
            foreach (var artifact in model.Files)
            {
                var destination = SafePath(staging, artifact.Path);
                if (!await MatchesAsync(destination, artifact.Size, artifact.Sha256, true, ct))
                {
                    var existing = SafePath(DirectoryFor(id), artifact.Path);
                    if (await MatchesAsync(existing, artifact.Size, artifact.Sha256, true, ct))
                    {
                        File.Copy(existing, destination, true);
                        completed += artifact.Size;
                        progress?.Report(new(completed, model.DownloadSize, "下载"));
                        continue;
                    }
                    var errors = new List<Exception>();
                    var downloaded = false;
                    foreach (var source in artifact.Sources)
                    {
                        try { await DownloadFileAsync(client, source, destination, artifact, completed, model.DownloadSize, progress, ct); downloaded = true; break; }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { errors.Add(error); }
                    }
                    if (!downloaded) throw new IOException("模型下载失败，请检查网络后重试。", new AggregateException(errors));
                }
                completed += artifact.Size;
                progress?.Report(new(completed, model.DownloadSize, "下载"));
            }
            if (id == ModelCatalog.EmbeddingId)
            {
                progress?.Report(new(completed, model.DownloadSize, "安装推理工具"));
                await Task.Run(() => ExtractRuntime(staging, model.Files.Last().Path, ct), ct);
            }
            var manifest = new List<InstalledFile>();
            progress?.Report(new(completed, model.DownloadSize, "校验模型"));
            foreach (var path in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (path.EndsWith(".part") || Path.GetFileName(path) == "installed.json") continue;
                await using var input = File.OpenRead(path);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
                manifest.Add(new(Path.GetRelativePath(staging, path), input.Length, hash));
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "installed.json"), JsonSerializer.Serialize(manifest), ct);
            ct.ThrowIfCancellationRequested();
            var final = DirectoryFor(id);
            if (Directory.Exists(final)) Directory.Delete(final, true);
            Directory.Move(staging, final);
            progress?.Report(new(completed, model.DownloadSize, "完成"));
        }
        finally { gate.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var gate = Gate(id);
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("模型正在下载或使用，请稍后重试。");
        try
        {
            await Task.Run(() =>
            {
                foreach (var folder in new[] { DirectoryFor(id), DirectoryFor(id) + ".download" })
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }, ct);
        }
        finally { gate.Release(); }
    }

    private static async Task<bool> MatchesAsync(string path, long size, string hash, bool verify, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        if (!verify) return true;
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadFileAsync(HttpClient client, string source, string destination, ModelArtifact artifact,
        long completed, long total, IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".part";
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > artifact.Size) { File.Delete(partial); offset = 0; }
        if (offset < artifact.Size)
        {
            progress?.Report(new(completed + offset, total, "连接下载源"));
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stall.CancelAfter(TimeSpan.FromSeconds(45));
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.OK) offset = 0;
            else if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != offset
                || response.Content.Headers.ContentRange?.Length != artifact.Size) throw new InvalidDataException("模型下载区间无效。");
            if (response.Content.Headers.ContentLength is { } size && size != artifact.Size - offset)
                throw new InvalidDataException("模型文件大小不符。");
            await using var input = await response.Content.ReadAsStreamAsync(stall.Token);
            await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 131072, true);
            var buffer = new byte[131072];
            var lastPercent = -1;
            var lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
            while (true)
            {
                stall.CancelAfter(TimeSpan.FromSeconds(45));
                var count = await input.ReadAsync(buffer, stall.Token);
                if (count == 0) break;
                offset += count;
                if (offset > artifact.Size) throw new InvalidDataException("模型文件超过预期大小。");
                await output.WriteAsync(buffer.AsMemory(0, count), stall.Token);
                var update = new ModelDownloadProgress(completed + offset, total, "下载");
                if (update.Percent != lastPercent || System.Diagnostics.Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds >= 500)
                { progress?.Report(update); lastPercent = update.Percent; lastReport = System.Diagnostics.Stopwatch.GetTimestamp(); }
            }
            await output.FlushAsync(ct);
        }
        progress?.Report(new(completed + offset, total, "校验模型"));
        if (!await MatchesAsync(partial, artifact.Size, artifact.Sha256, true, ct))
        { File.Delete(partial); throw new InvalidDataException("模型 SHA-256 校验失败。"); }
        File.Move(partial, destination, true);
    }

    private static string SafePath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("模型文件路径无效。");
        return full;
    }

    private static void ExtractRuntime(string staging, string archive, CancellationToken ct)
    {
        var target = Path.Combine(staging, "runtime");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        if (archive.EndsWith(".zip"))
        {
            using var zip = ZipFile.OpenRead(Path.Combine(staging, archive));
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var path = SafePath(target, entry.FullName);
                if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(path);
                else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); entry.ExtractToFile(path); }
            }
        }
        else
        {
            using (var source = File.OpenRead(Path.Combine(staging, archive)))
            using (var gzip = new GZipStream(source, CompressionMode.Decompress))
            using (var tar = new TarReader(gzip))
            {
                while (tar.GetNextEntry() is { } entry)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.EntryType == TarEntryType.Directory && entry.Name.TrimEnd('/') is "." or "") continue;
                    var path = SafePath(target, entry.Name);
                    if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                    {
                        var linkRoot = entry.EntryType == TarEntryType.HardLink ? target : Path.GetDirectoryName(path)!;
                        var link = Path.GetFullPath(Path.Combine(linkRoot, entry.LinkName));
                        _ = SafePath(target, Path.GetRelativePath(target, link));
                    }
                }
            }
            // TarFile preserves library links; TarEntry.ExtractToFile does not support link entries.
            ct.ThrowIfCancellationRequested();
            using var archiveInput = File.OpenRead(Path.Combine(staging, archive));
            using var decompressed = new GZipStream(archiveInput, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(decompressed, target, false);
        }
        if (FindRuntime(staging) is not { } executable) throw new InvalidDataException("下载包缺少本地推理工具。");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
    }

    public static string? FindRuntime(string folder) => Directory.Exists(Path.Combine(folder, "runtime"))
        ? Directory.EnumerateFiles(Path.Combine(folder, "runtime"), OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server", SearchOption.AllDirectories).FirstOrDefault() : null;
}
