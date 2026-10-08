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
    public const string JoyTagId = "joytag";
    public const string JoyTagFile = "model.onnx";
    public const string JoyTagLabels = "top_tags.txt";
    private const string JoyTagRevision = "6b7f16331a6ccf0fdce37d5a9564715f6e772b22";
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
        DownloadableModel Speech(SpeechModel model)
        {
            var artifact = SpeechModelInstaller.Artifact(model);
            return new(SpeechModelInstaller.Id(model), model == SpeechModel.Base ? "Whisper · 标准" : "Whisper · 轻量", "自动字幕", "MIT",
                "https://github.com/ggml-org/whisper.cpp", [new(artifact.FileName, artifact.Size, artifact.Sha256, [artifact.Url, artifact.Url + "?download=true"])]);
        }
        return [
            Speech(SpeechModel.Base), Speech(SpeechModel.Tiny),
            new(JoyTagId, "JoyTag", "图片 / 视频 AI 标签 · Beta", "Apache-2.0", "https://github.com/fpgaminer/joytag",
                [new(JoyTagFile, 366116154, "f85b7130e6e549b5b0822537007b7482e8c4c8e754c8d9a5bee08e27050e1097",
                    [$"https://huggingface.co/fancyfeast/joytag/resolve/{JoyTagRevision}/{JoyTagFile}"]),
                 new(JoyTagLabels, 76752, "32b1963a234af848643b2bbf47d8eff1f1c7889406810c57b980f41b2b9e01d0",
                    [$"https://huggingface.co/fancyfeast/joytag/resolve/{JoyTagRevision}/{JoyTagLabels}"])]),
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
            : OperatingSystem.IsWindows() ? (arm ? "win-vulkan-arm64" : "win-vulkan-x64")
            : OperatingSystem.IsLinux() ? (arm ? "ubuntu-vulkan-arm64" : "ubuntu-vulkan-x64") : "";
        var (size, hash) = platform switch
        {
            "macos-arm64" => (12012019L, "577634a1b8a59e8dabe02ba10de1e610be0574dfaf1cf3020e6dd42853ed877e"),
            "macos-x64" => (11530774L, "c2a0dfe7622a99fc3279454814045923e99f1cfdddb8f121c5969c5c675fc073"),
            "win-vulkan-arm64" => (25918979L, "53659ca6e67dc624c62f6df48f204cd89d9d1ff12a642173ba67235be9ff4bf7"),
            "win-vulkan-x64" => (33380424L, "5c71e7b749697da4a8d46e9ee55486845cbba27c9dfbecb4007f31ba6610d523"),
            "ubuntu-vulkan-arm64" => (24888315L, "8dae2f39afee01d3032a101d7c398a6734d6fd9697690d8f93bdce4e3a9efea2"),
            "ubuntu-vulkan-x64" => (31684207L, "5bb4306d7917f33e81efda02e6f791ae6a82e86bee121227a3ab2b4e8e40427f"),
            _ => (0L, "")
        };
        if (size == 0) return null;
        var extension = OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
        return new("runtime" + extension, size, hash,
            [$"https://github.com/ggml-org/llama.cpp/releases/download/b11476/llama-b11476-bin-{platform}{extension}"]);
    }
}
