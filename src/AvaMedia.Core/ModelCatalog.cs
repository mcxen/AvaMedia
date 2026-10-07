using System.Runtime.InteropServices;

namespace AvaMedia.Core;

public sealed record ModelArtifact(string Path, long Size, string Sha256, string[] Sources);
public sealed record DownloadableModel(string Id, string Name, string Purpose, string License,
    string SourcePage, IReadOnlyList<ModelArtifact> Files, bool Supported = true)
{
    public long DownloadSize => Files.Sum(file => file.Size);
}

/// <summary>Immutable upstream revisions and hashes; models are downloaded only on request.</summary>
public static class ModelCatalog
{
    public const string PersonId = "yolox";
    public const string EmbeddingId = "embeddinggemma-2";
    public const string LamaId = "lama";
    private const string GemmaRevision = "bfcd298762cc34d0357ece5ebdd31791a3a374d8";
    public const string GemmaFile = "embeddinggemma-2-Q8_0.gguf";
    public const string ProjectorFile = "mmproj-embeddinggemma-2-Q8_0.gguf";
    public const string PersonFile = "object_detection_yolox_2022nov.onnx";
    public static IReadOnlyList<DownloadableModel> All { get; } = Build();
    public static DownloadableModel Find(string id) => All.First(model => model.Id == id);

    private static IReadOnlyList<DownloadableModel> Build()
    {
        ModelArtifact Gemma(string name, long size, string hash) => new(name, size, hash,
            [$"https://huggingface.co/ggml-org/embeddinggemma-2-GGUF/resolve/{GemmaRevision}/{name}"]);
        var runtime = Runtime();
        return [
            new(LamaId, "LaMa", "图片修复", "Apache-2.0", "https://huggingface.co/opencv/inpainting_lama",
                [new(LaMaModelInstaller.FileName, LaMaModelInstaller.FileSize, LaMaModelInstaller.Sha256,
                    [LaMaModelInstaller.HubUrl, LaMaModelInstaller.FallbackUrl])]),
            new(PersonId, "YOLOX", "自动保留有人片段", "Apache-2.0", "https://github.com/opencv/opencv_zoo/tree/main/models/object_detection_yolox",
                [new(PersonFile, 35858002, "c5c2d13e59ae883e6af3b45daea64af4833a4951c92d116ec270d9ddbe998063",
                    ["https://huggingface.co/opencv/opencv_zoo/resolve/d4938dfc9d4ec5d098bfa33e98b3f3345a236586/models/object_detection_yolox/" + PersonFile])]),
            new(EmbeddingId, "EmbeddingGemma 2 · Q8", "可选语义辅助 · 含本地推理工具", "Apache-2.0 / MIT",
                "https://ai.google.dev/gemma/docs/embeddinggemma/model_card_2",
                new[] {
                    Gemma(GemmaFile, 309855456, "2188ac1deca4b77dffefd603c2776a9d76d9d74ec01841392982ebb840b09135"),
                    Gemma(ProjectorFile, 554821024, "c4a8a52691ecef40618438928bdf9e68379b854e24166f292592353db0aab64f")
                }.Concat(runtime is null ? [] : new[] { runtime }).ToArray(), runtime is not null)
        ];
    }

    private static ModelArtifact? Runtime()
    {
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (!arm && RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;
        var platform = OperatingSystem.IsMacOS() ? (arm ? "macos-arm64" : "macos-x64")
            : OperatingSystem.IsWindows() ? (arm ? "win-cpu-arm64" : "win-cpu-x64")
            : OperatingSystem.IsLinux() ? (arm ? "ubuntu-arm64" : "ubuntu-x64") : "";
        var (size, hash) = platform switch
        {
            "macos-arm64" => (12012019L, "577634a1b8a59e8dabe02ba10de1e610be0574dfaf1cf3020e6dd42853ed877e"),
            "macos-x64" => (11530774L, "c2a0dfe7622a99fc3279454814045923e99f1cfdddb8f121c5969c5c675fc073"),
            "win-cpu-arm64" => (12267911L, "68e3a218ed7d9cd563e8ddf7a1e58d88d034a8f90a91061bdd3876bf247d8a93"),
            "win-cpu-x64" => (19441535L, "a23e548c6b3525c38bcfeceaff919786ae06741857043cb670279b70100e5483"),
            "ubuntu-arm64" => (13728259L, "9aa7c1dcea2e0491f27441b30217767ec4730bcdeefa646e288825454a71bfa1"),
            "ubuntu-x64" => (17737286L, "2cda5ff9363967f1aba5b5b096032e1b7d9eb568b011769282bf34e4f1cf4b5e"),
            _ => (0L, "")
        };
        if (size == 0) return null;
        var extension = OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
        return new("runtime" + extension, size, hash,
            [$"https://github.com/ggml-org/llama.cpp/releases/download/b11476/llama-b11476-bin-{platform}{extension}"]);
    }
}
