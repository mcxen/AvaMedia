using System.Security.Cryptography;

namespace AvaMedia.Core;

public enum SpeechModel { Base, Tiny }

public sealed class TranscriptionOptions
{
    public string Language { get; set; } = "auto";
    public SpeechModel Model { get; set; }
    public TranscriptionOptions Clone() => (TranscriptionOptions)MemberwiseClone();
    public void Validate()
    {
        if (!Enum.IsDefined(Model) || !Languages.Contains(Language))
            throw new ArgumentException("请选择有效的识别语言和模型。");
    }
    public static readonly string[] Languages = ["auto", "zh", "en", "ja", "ko", "fr", "de", "es", "ru"];
}

public sealed record SpeechModelArtifact(string FileName, long Size, string Sha256)
{
    public string Url => "https://huggingface.co/ggerganov/whisper.cpp/resolve/f281eb45af861ab5e5297d23694b7d46e090c02c/" + FileName;
}

public sealed class SpeechModelInstaller(string? directory = null)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly ModelStore _store = new(directory);
    public string DirectoryPath => _store.Root;
    public static string Id(SpeechModel model) => model == SpeechModel.Base ? "whisper-base" : model == SpeechModel.Tiny ? "whisper-tiny" : throw new ArgumentOutOfRangeException(nameof(model));
    public static SpeechModelArtifact Artifact(SpeechModel model) => model switch
    {
        SpeechModel.Base => new("ggml-base-q5_1.bin", 59707625, "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898"),
        SpeechModel.Tiny => new("ggml-tiny-q5_1.bin", 32152673, "818710568da3ca15689e31a743197b520007872ff9576237bda97bd1b469c3d7"),
        _ => throw new ArgumentOutOfRangeException(nameof(model))
    };

    public async Task<string> EnsureInstalledAsync(SpeechModel model, Action<int>? progress = null, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await _store.IsInstalledAsync(Id(model), true, ct).ConfigureAwait(false))
                await _store.DownloadAsync(Id(model), new DownloadProgress(progress), ct).ConfigureAwait(false);
            progress?.Invoke(100);
            return _store.FileFor(Id(model), Artifact(model).FileName);
        }
        finally { Gate.Release(); }
    }
    public Task<ModelLease> AcquireAsync(SpeechModel model, CancellationToken ct = default) => _store.AcquireAsync(Id(model), ct);
    private sealed class DownloadProgress(Action<int>? report) : IProgress<ModelDownloadProgress>
    { public void Report(ModelDownloadProgress value) => report?.Invoke(value.Percent); }
}

public static class SpeechAssets
{
    private static readonly object Gate = new();
    private static string? _denoisePath;
    private static string? _vadPath;
    public static string ModelDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "models");
    public const string DenoiseSha256 = "70bb6685eb0c2a1d18e2918dca3fbfbd39317010b1802eb1b6ea73a92f3fdec0";
    public const string VadSha256 = "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987";
    public static string EnsureDenoiseModel() => EnsureEmbeddedModel(ref _denoisePath, "speech-sh.rnnn", "AvaMedia.Core.SpeechDenoise.rnnn", DenoiseSha256);
    public static string EnsureVadModel() => EnsureEmbeddedModel(ref _vadPath, "ggml-silero-v6.2.0.bin", "AvaMedia.Core.SpeechVad.bin", VadSha256);
    private static string EnsureEmbeddedModel(ref string? cached, string name, string resource, string sha256)
    {
        lock (Gate)
        {
            if (cached is not null && File.Exists(cached)) return cached;
            Directory.CreateDirectory(ModelDirectory);
            var path = Path.Combine(ModelDirectory, name);
            if (File.Exists(path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                return cached = path;
            using var embedded = typeof(SpeechAssets).Assembly.GetManifestResourceStream(resource)
                ?? throw new FileNotFoundException("应用缺少语音模型，请重新安装。", name);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = File.Create(temporary)) embedded.CopyTo(output);
                File.Move(temporary, path, true);
                return cached = path;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
