using System.Net;
using System.Security.Cryptography;

namespace AvaMedia.Core;

/// <summary>Installs the pinned LaMa artifact; the small YuNet detector remains embedded.</summary>
public sealed class LaMaModelInstaller(string? directory = null)
{
    public const string FileName = "inpainting_lama_2025jan.onnx";
    public const long FileSize = 92591623;
    public const string Sha256 = "7df918ac3921d3daf0aae1d219776cf0dc4e4935f035af81841b40adcf74fdf2";
    public const string HubUrl = "https://huggingface.co/opencv/inpainting_lama/resolve/main/" + FileName;
    public const string FallbackUrl = "https://lz.qaiu.top/parser?url=https://share.feijipan.com/s/zwwYN8FN";
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    public string ModelPath { get; } = Path.Combine(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "models"), FileName);

    public async Task<string> EnsureInstalledAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await InstallGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await IsInstalledAsync(ct).ConfigureAwait(false)) return ModelPath;
            progress?.Report(0);
            Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
            using var client = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = true, MaxAutomaticRedirections = 10,
                ConnectTimeout = TimeSpan.FromSeconds(15)
            }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AvaMedia/" + AppIdentity.Version);
            // Resolve afresh rather than persisting a signed, expiring CDN URL.
            string[] sources = [HubUrl, HubUrl + "?download=true&refresh=" + Guid.NewGuid().ToString("N"), FallbackUrl];
            var failures = new List<Exception>();
            foreach (var source in sources)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await DownloadAsync(client, source, progress, ct).ConfigureAwait(false);
                    return ModelPath;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                { failures.Add(error); }
            }
            throw new IOException("图片修复模型安装失败，请检查网络后重试。", new AggregateException(failures));
        }
        finally { InstallGate.Release(); }
    }

    public async Task<bool> IsInstalledAsync(CancellationToken ct = default)
    {
        try
        {
            await using var file = new FileStream(ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != FileSize) return false;
            var hash = await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).Equals(Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }

    private async Task DownloadAsync(HttpClient client, string source, IProgress<int>? progress, CancellationToken ct)
    {
        var temporary = ModelPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        stall.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            progress?.Report(0);
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is { } length && length != FileSize)
                throw new InvalidDataException("模型下载返回了错误的文件大小。");
            await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            var reported = -1;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[65536];
                while (true)
                {
                    stall.CancelAfter(TimeSpan.FromSeconds(30));
                    var count = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > FileSize) throw new InvalidDataException("模型下载超过了预期大小。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), stall.Token).ConfigureAwait(false);
                    var percent = (int)(received * 100 / FileSize);
                    if (percent != reported) { progress?.Report(Math.Min(percent, 99)); reported = percent; }
                }
                if (received != FileSize || !Convert.ToHexString(hash.GetHashAndReset()).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("图片修复模型校验失败。");
                await output.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, ModelPath, true);
            progress?.Report(100);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
