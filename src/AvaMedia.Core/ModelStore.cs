using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaMedia.Core;

public sealed record ModelDownloadProgress(long Received, long Total, string Stage, int Attempt = 0, int MaxAttempts = 0)
{
    public int Percent => Total == 0 ? 0 : (int)Math.Clamp(Received * 100 / Total, 0, 100);
    public string Source { get; init; } = "";
    /// <summary>Readable source name (Hugging Face, ModelScope, HF-Mirror, GitHub or host).</summary>
    public string SourceName => Source.Length == 0 ? "" : ModelDownloadSources.Describe(Source);
    public int SourceIndex { get; init; }
    public int SourceCount { get; init; }
}

public sealed class ModelLease(string directory, Action release) : IDisposable
{
    public string Directory { get; } = directory;
    private Action? _release = release;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>Shared download/use/delete locks, resumable staging, verified atomic publication.</summary>
public sealed class ModelStore(string? root = null)
{
    private static readonly ConcurrentDictionary<string, ModelAccessGate> Gates = new(BatchRename.PathComparer);
    private sealed record FileStamp(long Length, DateTime ModifiedUtc, DateTime CreatedUtc, string Target);
    private sealed record VerifiedFile(FileStamp Stamp, string Hash);
    private sealed class FileVerification
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal VerifiedFile? Verified;
    }
    private static readonly ConcurrentDictionary<string, FileVerification> Verifications = new(BatchRename.PathComparer);
    private sealed record InstalledFile(string Path, long Size, string Sha256);
    public string Root { get; } = Path.GetFullPath(root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "models"));
    public string DirectoryFor(string id) { _ = ModelCatalog.Find(id); return Path.Combine(Root, id); }
    public string FileFor(string id, string file) => SafePath(DirectoryFor(id), file);
    private ModelAccessGate Gate(string id) => Gates.GetOrAdd(DirectoryFor(id), _ => new());
    public bool IsBusy(string id) => Gate(id).IsBusyExceptReaders(LocalSummaryModelCache.IdleReaders(Root, id));
    internal string InstalledStamp(string id)
    {
        try
        {
            var folder = DirectoryFor(id); var manifestPath = Path.Combine(folder, "installed.json");
            var manifest = JsonSerializer.Deserialize<InstalledFile[]>(File.ReadAllText(manifestPath)) ?? [];
            return string.Join('|', manifest.Select(file => SafePath(folder, file.Path)).Append(manifestPath).Select(path =>
            {
                var file = new FileInfo(path); var target = file.LinkTarget is null ? file : file.ResolveLinkTarget(true) as FileInfo ?? file;
                return target.Exists ? $"{target.FullName}:{target.Length}:{target.LastWriteTimeUtc.Ticks}:{target.CreationTimeUtc.Ticks}" : "missing";
            }));
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { return "missing"; }
    }
    public bool HasLocalData(string id) => Directory.Exists(DirectoryFor(id)) || Directory.Exists(DirectoryFor(id) + ".download");
    public long DownloadedBytes(string id)
    {
        var staging = DirectoryFor(id) + ".download";
        return ModelCatalog.Find(id).Files.Sum(file => PartialBytes(SafePath(staging, file.Path), file.Size));
    }
    private static long PartialBytes(string destination, long size)
    {
        try
        {
            var path = File.Exists(destination + ".part") ? destination + ".part" : destination;
            return File.Exists(path) ? Math.Clamp(new FileInfo(path).Length, 0, size) : 0;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    public Task<bool> IsInstalledAsync(string id, bool verify = false, CancellationToken ct = default, bool reuseVerification = false)
        => CheckInstalledAsync(id, verify, reuseVerification, ct);

    private async Task<bool> CheckInstalledAsync(string id, bool verify, bool reuseVerification, CancellationToken ct,
        Action<long, long>? verificationProgress = null)
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
            long completed = 0, total = manifest.Sum(file => file.Size);
            verificationProgress?.Invoke(0, total);
            foreach (var file in manifest)
            {
                if (!await MatchesAsync(SafePath(folder, file.Path), file.Size, file.Sha256, verify, ct, reuseVerification,
                    verificationProgress is null ? null : bytes => verificationProgress(completed + bytes, total))) return false;
                completed += file.Size;
                verificationProgress?.Invoke(completed, total);
            }
            return !ModelCatalog.IncludesRuntime(id) || FindRuntime(folder) is not null;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Verify unchanged files once per process; explicit model-management verification forces a fresh hash.</summary>
    public async Task<ModelLease> AcquireAsync(string id, CancellationToken ct = default, bool verify = true, bool forceVerification = false,
        Action<long, long>? verificationProgress = null)
    {
        var gate = Gate(id);
        await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
        var release = await gate.AcquireReadAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await CheckInstalledAsync(id, verify || forceVerification, !forceVerification, ct, verificationProgress)) throw new InvalidOperationException("请在选项的模型管理中下载或修复所需模型。");
            return new(DirectoryFor(id), release);
        }
        catch { release(); throw; }
    }

    public async Task DownloadAsync(string id, IProgress<ModelDownloadProgress>? progress = null, CancellationToken ct = default,
        ModelSourcePreference? sourcePreference = null)
    {
        var model = ModelCatalog.Find(id);
        if (!model.Supported) throw new PlatformNotSupportedException("当前平台不支持此模型的本地推理工具。");
        sourcePreference ??= ModelDownloadSources.Preference;
        sourcePreference.Validate();
        var china = ModelDownloadSources.PrefersChinaSources();
        if (await IsInstalledAsync(id, true, ct).ConfigureAwait(false))
        { progress?.Report(new(model.DownloadSize, model.DownloadSize, "完成")); return; }
        progress?.Report(new(0, model.DownloadSize, "等待模型"));
        await MediaTagModelCache.InvalidateAsync(Root, id, ct).ConfigureAwait(false);
        await LocalSummaryModelCache.InvalidateAsync(Root, id, ct).ConfigureAwait(false);
        var gate = Gate(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            progress?.Report(new(0, model.DownloadSize, "校验模型"));
            if (await IsInstalledAsync(id, true, ct)) return;
            var staging = DirectoryFor(id) + ".download";
            Directory.CreateDirectory(staging);
            using var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(20) }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AvaMedia/" + AppIdentity.Version);
            long completed = 0;
            string? preferredOrigin = null;
            foreach (var artifact in model.Files)
            {
                var destination = SafePath(staging, artifact.Path);
                if (!await MatchesAsync(destination, artifact.Size, artifact.Sha256, true, ct))
                {
                    if (File.Exists(destination)) File.Delete(destination);
                    var existing = SafePath(DirectoryFor(id), artifact.Path);
                    if (await MatchesAsync(existing, artifact.Size, artifact.Sha256, true, ct))
                    {
                        File.Copy(existing, destination, true);
                        completed += artifact.Size;
                        progress?.Report(new(completed, model.DownloadSize, "下载"));
                        continue;
                    }
                    var errors = new List<Exception>();
                    var failures = new Dictionary<string, Exception>(StringComparer.Ordinal);
                    var rejected = new HashSet<string>(StringComparer.Ordinal);
                    var downloaded = false;
                    const int attempts = 3;
                    for (var attempt = 1; attempt <= attempts; attempt++)
                    {
                        var sources = ModelDownloadSources.Resolve(artifact, sourcePreference, china)
                            .Where(source => !rejected.Contains(source))
                            .OrderBy(source => new Uri(source).GetLeftPart(UriPartial.Authority) == preferredOrigin ? 0 : 1).ToArray();
                        if (sources.Length == 0) break;
                        for (var sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var source = sources[sourceIndex];
                            var sourceProgress = new SourceProgress(progress, source, sourceIndex + 1, sources.Length, attempt, attempts);
                            if (sourceIndex > 0)
                                sourceProgress.Report(new(completed + PartialBytes(destination, artifact.Size), model.DownloadSize, "切换下载源"));
                            try
                            {
                                await DownloadFileAsync(client, source, destination, artifact, completed, model.DownloadSize, sourceProgress, ct);
                                preferredOrigin = new Uri(source).GetLeftPart(UriPartial.Authority);
                                downloaded = true; break;
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                            {
                                errors.Add(new IOException("下载源 " + source, error));
                                failures[source] = error;
                                if (!Retryable(error)) rejected.Add(source);
                            }
                        }
                        if (downloaded) break;
                        if (attempt < attempts && sources.Any(source => !rejected.Contains(source)))
                        {
                            progress?.Report(new(completed + PartialBytes(destination, artifact.Size), model.DownloadSize, "等待重试", attempt + 1, attempts));
                            await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                        }
                    }
                    if (!downloaded)
                    {
                        var reasons = failures.Select(failure => new Uri(failure.Key).Host + " · "
                            + (failure.Value is OperationCanceledException ? "连接或下载超时。" : failure.Value.GetBaseException().Message)).Distinct();
                        throw new IOException("模型下载失败，已保留下载进度。请检查网络后重试。\n" + string.Join("\n", reasons), new AggregateException(errors));
                    }
                }
                completed += artifact.Size;
                progress?.Report(new(completed, model.DownloadSize, "下载"));
            }
            await PublishAsync(id, model, staging, completed, progress, ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Import user-supplied files (for example downloaded in a browser or copied from another computer). Files are matched by
    /// catalog size and SHA-256, so renamed files work; matched files are staged and published exactly like a download.
    /// </summary>
    public async Task ImportAsync(string id, IReadOnlyList<string> paths, IProgress<ModelDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var model = ModelCatalog.Find(id);
        if (!model.Supported) throw new PlatformNotSupportedException("当前平台不支持此模型的本地推理工具。");
        await MediaTagModelCache.InvalidateAsync(Root, id, ct).ConfigureAwait(false);
        await LocalSummaryModelCache.InvalidateAsync(Root, id, ct).ConfigureAwait(false);
        var gate = Gate(id);
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("模型正在下载或使用，请稍后重试。");
        try
        {
            var candidates = await Task.Run(() => paths.SelectMany(path => Directory.Exists(path)
                    ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
                    : File.Exists(path) ? [path] : Array.Empty<string>())
                .Select(Path.GetFullPath).Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal).Select(path => new FileInfo(path)).ToArray(), ct);
            if (candidates.Length == 0) throw new FileNotFoundException("所选位置没有可导入的文件。");
            var staging = DirectoryFor(id) + ".download";
            Directory.CreateDirectory(staging);
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            var missing = new List<string>(); var mismatched = new List<string>();
            long completed = 0;
            foreach (var artifact in model.Files)
            {
                ct.ThrowIfCancellationRequested();
                var destination = SafePath(staging, artifact.Path);
                var name = Path.GetFileName(artifact.Path);
                FileInfo? match = null;
                // Same-name files first, then any file of the exact catalog size (renamed downloads).
                foreach (var file in candidates.Where(file => file.Length == artifact.Size)
                    .OrderBy(file => file.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                {
                    progress?.Report(new(completed, model.DownloadSize, "校验文件"));
                    if (!hashes.TryGetValue(file.FullName, out var hash))
                    {
                        await using var input = File.OpenRead(file.FullName);
                        hashes[file.FullName] = hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
                    }
                    if (hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)) { match = file; break; }
                }
                if (match is null)
                {
                    if (await MatchesAsync(destination, artifact.Size, artifact.Sha256, true, ct)) { completed += artifact.Size; continue; }
                    (candidates.Any(file => file.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? mismatched : missing).Add(name);
                    continue;
                }
                progress?.Report(new(completed, model.DownloadSize, "导入文件"));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(match.FullName, destination + ".part", true);
                if (!await MatchesAsync(destination + ".part", artifact.Size, artifact.Sha256, true, ct))
                { File.Delete(destination + ".part"); throw new IOException("导入时文件被修改：" + match.FullName); }
                File.Move(destination + ".part", destination, true);
                completed += artifact.Size;
                progress?.Report(new(completed, model.DownloadSize, "导入文件"));
            }
            if (mismatched.Count > 0)
                throw new InvalidDataException("文件与模型清单不符（大小或 SHA-256 不同），请确认版本：" + string.Join("、", mismatched)
                    + (missing.Count > 0 ? "\n缺少：" + string.Join("、", missing) : ""));
            if (missing.Count > 0)
                throw new FileNotFoundException("缺少模型文件：" + string.Join("、", missing) + "。已导入的文件会保留，可继续下载其余部分。");
            await PublishAsync(id, model, staging, completed, progress, ct);
        }
        finally { gate.Release(); }
    }

    private async Task PublishAsync(string id, DownloadableModel model, string staging, long completed,
        IProgress<ModelDownloadProgress>? progress, CancellationToken ct)
    {
        if (ModelCatalog.IncludesRuntime(id))
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

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await MediaTagModelCache.InvalidateAsync(Root, id, ct).ConfigureAwait(false);
        await LocalSummaryModelCache.InvalidateAsync(Root, id, ct).ConfigureAwait(false);
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

    private static async Task<bool> MatchesAsync(string path, long size, string hash, bool verify, CancellationToken ct,
        bool reuseVerification = false, Action<long>? verificationProgress = null)
    {
        if (!File.Exists(path)) return false;
        var check = verify ? Verifications.GetOrAdd(path, _ => new()) : null;
        if (check is not null) await check.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Runtime archives contain dylib links. FileInfo.Length may report the link's
            // length; the manifest records the content actually opened for hashing.
            await using var input = File.OpenRead(path);
            if (input.Length != size) { if (check is not null) check.Verified = null; return false; }
            if (!verify) return true;
            var before = Stamp();
            if (reuseVerification && check!.Verified is { } cached && cached.Stamp == before
                && cached.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase)) return true;
            check!.Verified = null;
            var actual = Convert.ToHexString(await LocalModelWarmupBudget.HashAsync(input, ct, verificationProgress)).ToLowerInvariant();
            var after = Stamp();
            if (actual.Equals(hash, StringComparison.OrdinalIgnoreCase) && before == after)
            { check.Verified = new(after, actual); return true; }
            return false;

            FileStamp Stamp()
            {
                var file = new FileInfo(path);
                var target = file.LinkTarget is null ? file : file.ResolveLinkTarget(true) as FileInfo ?? file;
                target.Refresh();
                return new(input.Length, target.LastWriteTimeUtc, target.CreationTimeUtc, target.FullName);
            }
        }
        finally { check?.Gate.Release(); }
    }

    private static bool Retryable(Exception error) => error switch
    {
        HttpRequestException http => http.StatusCode is not { } status || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int)status >= 500,
        InvalidDataException => false,
        IOException or OperationCanceledException => true,
        _ => false
    };

    private sealed class SourceProgress(IProgress<ModelDownloadProgress>? progress, string source, int index, int count,
        int attempt, int attempts) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => progress?.Report(value with
        {
            Source = source, SourceIndex = index, SourceCount = count,
            Attempt = attempt > 1 ? attempt : 0, MaxAttempts = attempts
        });
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
        if (offset != artifact.Size) throw new IOException("下载中断，已保留下载进度。");
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
