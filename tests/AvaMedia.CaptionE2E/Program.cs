using System.Text;
using AvaMedia.Core;

static void Check(bool ok, string name)
{
    if (!ok) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

var root = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "artifacts/caption-e2e");
Directory.CreateDirectory(root);
var image = Path.Combine(root, "adult-synthetic.png");
if (!File.Exists(image)) throw new FileNotFoundException("missing test image", image);

var png = await File.ReadAllBytesAsync(image);
Check(png.Length > 0, "test image bytes");

// Mock path: adult caption must pass refusal filter and sidecar write
var mockCaption = "画面中一名裸体成年女性躺在床上，胸部与外阴可见，背景为室内床铺。";
Check(!MediaCaptionService.LooksLikeRefusal(mockCaption), "mock adult caption is not a refusal");
Check(MediaCaptionService.LooksLikeRefusal("Sorry, but I cannot describe this image."), "refusal detector catches English refusal");

var settings = new AppSettings
{
    FFmpegPath = "/usr/bin/ffmpeg",
    FFprobePath = "/usr/bin/ffprobe",
    OnlineAi = new OnlineAiSettings
    {
        Providers =
        [
            new OnlineAiOptions
            {
                Name = "Ollama",
                Preset = "ollama",
                Endpoint = "http://localhost:11434/v1",
                TextModel = "moondream",
                VisionModel = "moondream",
                TokenLimit = OnlineAiTokenLimit.MaxTokens,
                ResponseFormat = OnlineAiResponseFormat.Prompt,
                TimeoutSeconds = 300
            }
        ]
    }
};
settings.OnlineAi.DefaultProviderId = settings.OnlineAi.Providers[0].Id;
var provider = MediaCaptionService.PrepareProvider(settings.OnlineAi.Resolve());
Check(provider.EffectiveVisionModel == "moondream", "vision model is moondream");

var options = new MediaTagOptions(GenerateCaptions: true, CaptionMaxTokens: 256);
options.Validate();

Console.WriteLine("Calling Ollama vision via OnlineSummaryModel...");
var (caption, model) = await MediaCaptionService.GenerateAsync(provider, [png], options, CancellationToken.None);
Console.WriteLine("MODEL: " + model);
Console.WriteLine("CAPTION: " + caption);
await File.WriteAllTextAsync(Path.Combine(root, "live-caption.txt"), caption, new UTF8Encoding(false));
Check(!string.IsNullOrWhiteSpace(caption), "live caption non-empty");
Check(!MediaCaptionService.LooksLikeRefusal(caption), "live caption is not a refusal");
Check(model.Contains("moondream", StringComparison.OrdinalIgnoreCase), "caption model name");

// Sidecar write without JoyTag scores
var now = DateTime.UtcNow;
var info = new FileInfo(image);
var result = new MediaTagResult(info.FullName, [], 1, 1, "e2e", info.Length, info.LastWriteTimeUtc)
{
    Caption = caption,
    CaptionModel = model,
    Frames = [new MediaTagFrame(0, [])]
};
var labels = new MediaTagTextLabel[] { new("合成测试", "E2E", 1, "manual", "manual", []) };
var sidecar = await MediaTagText.SaveAsync(result, labels, .4, .55, .03, CancellationToken.None);
Console.WriteLine("SIDECAR: " + sidecar);
var text = await File.ReadAllTextAsync(sidecar);
await File.WriteAllTextAsync(Path.Combine(root, "sidecar-copy.txt"), text, new UTF8Encoding(false));
Check(text.StartsWith("AvaMedia AI Tags / 2", StringComparison.Ordinal), "sidecar header v2");
Check(text.Contains("画面描述", StringComparison.Ordinal), "sidecar has 画面描述 section");
Check(text.Contains(caption.Split('\n')[0], StringComparison.Ordinal), "sidecar contains caption text");
Check(text.Contains("描述模型：" + model, StringComparison.Ordinal), "sidecar lists caption model");

// Also verify mock adult caption survives SaveAsync
var adultResult = result with { Caption = mockCaption, CaptionModel = "mock" };
var adultSidecar = await MediaTagText.SaveAsync(adultResult, labels, .4, .55, .03, CancellationToken.None);
var adultText = await File.ReadAllTextAsync(adultSidecar);
Check(adultText.Contains(mockCaption, StringComparison.Ordinal), "sidecar preserves explicit adult caption");
Check(adultText.Contains("画面描述", StringComparison.Ordinal), "adult sidecar has 画面描述");

Console.WriteLine("ALL CAPTION E2E CHECKS PASSED");
