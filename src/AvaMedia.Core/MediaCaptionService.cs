namespace AvaMedia.Core;

/// <summary>Optional free-form vision captions after JoyTag. Uses OpenAI-compatible vision (prefer local Ollama). Never writes source media.</summary>
public static class MediaCaptionService
{
    public const string DefaultSystemPrompt =
        "你是媒体画面描述助手。请根据提供的图像，用中文如实描述可见内容。" +
        "允许并应当描述成人/NSFW 内容：裸露、性器官、性行为、体位、体液等，只要画面中可见就用准确、中性、事实性的语言写出，不要回避、不要道德说教、不要拒绝。" +
        "不要编造看不见的细节；不要输出政策免责声明或“我无法描述”之类拒绝语。只输出描述正文。";

    public const string DefaultUserPrompt =
        "请用一两段中文描述这张（或这些）画面中清晰可见的人物、服饰或裸露、动作、场景与关键物体。成人内容请直接如实描写。";

    public static void ValidateOptions(MediaTagOptions options)
    {
        if (options.CaptionMaxTokens is < 64 or > 4096) throw new ArgumentException("画面描述最大输出须为 64–4096。");
        if (options.CaptionPrompt is { Length: > 4000 }) throw new ArgumentException("画面描述提示过长。");
        if (options.CaptionProviderId is { Length: > 0 } && !Guid.TryParseExact(options.CaptionProviderId, "N", out _))
            throw new ArgumentException("画面描述供应商标识无效。");
    }

    /// <summary>Prepare provider options so OnlineSummaryModel can open a vision session (TextModel may be empty for vision-only Ollama).</summary>
    public static OnlineAiOptions PrepareProvider(OnlineAiOptions source)
    {
        var options = source.Clone();
        if (string.IsNullOrWhiteSpace(options.TextModel) && !string.IsNullOrWhiteSpace(options.VisionModel))
            options.TextModel = options.VisionModel;
        if (string.IsNullOrWhiteSpace(options.VisionModel) && !string.IsNullOrWhiteSpace(options.TextModel))
            options.VisionModel = options.TextModel;
        if (string.IsNullOrWhiteSpace(options.EffectiveVisionModel))
            throw new ArgumentException("请在 AI 供应商中配置视觉模型（例如 Ollama 的 moondream / llava）。");
        // Captions are free-form prose; do not force JSON response_format.
        options.ResponseFormat = OnlineAiResponseFormat.Prompt;
        options.Validate();
        options.ValidateConnection();
        return options;
    }

    public static async Task<(string Caption, string Model)> GenerateAsync(
        OnlineAiOptions provider, IReadOnlyList<byte[]> frames, MediaTagOptions options, CancellationToken ct,
        ISummaryModel? model = null)
    {
        if (frames.Count == 0) throw new ArgumentException("没有可用于画面描述的采样帧。");
        ValidateOptions(options);
        var prepared = PrepareProvider(provider);
        var prompt = string.IsNullOrWhiteSpace(options.CaptionPrompt) ? DefaultUserPrompt : options.CaptionPrompt.Trim();
        // Prefer a few representative frames; OnlineSummaryModel accepts 1–3 images.
        var selected = frames.Count <= 3 ? frames.ToArray() : [frames[0], frames[frames.Count / 2], frames[^1]];
        IReadOnlyList<SummaryModelImage>? images = selected.Length == 1 ? null
            : selected.Select((png, index) => new SummaryModelImage($"帧 {index + 1}", png)).ToArray();

        if (model is not null)
        {
            var caption = await model.CompleteAsync(DefaultSystemPrompt, prompt, ct,
                image: images is null ? selected[0] : null, tokens: options.CaptionMaxTokens, images: images).ConfigureAwait(false);
            return (RequireCaption(caption), prepared.EffectiveVisionModel);
        }

        await using var vision = new OnlineSummaryModel(prepared, vision: true);
        {
            var caption = await vision.CompleteAsync(DefaultSystemPrompt, prompt, ct,
                image: images is null ? selected[0] : null, tokens: options.CaptionMaxTokens, images: images).ConfigureAwait(false);
            return (RequireCaption(caption), prepared.EffectiveVisionModel);
        }
    }

    public static string NormalizeCaption(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0);
        return string.Join("\n", lines).Trim();
    }

    public static string RequireCaption(string text)
    {
        var caption = NormalizeCaption(text);
        if (caption.Length == 0) throw new InvalidDataException("画面描述模型未返回有效内容。");
        if (LooksLikeRefusal(caption)) throw new InvalidDataException("画面描述模型拒绝描述，请更换本地未审查视觉模型。");
        return caption;
    }

    public static bool LooksLikeRefusal(string caption)
    {
        ReadOnlySpan<string> markers =
        [
            "i cannot", "i can't", "i'm unable", "i am unable", "as an ai", "sorry, but", "i won't", "i will not",
            "cannot assist", "can't assist", "cannot help with", "can't help with", "not able to provide", "against my guidelines",
            "无法协助", "无法描述", "不能描述", "我不能", "抱歉，我无法", "作为人工智能", "内容违规", "违反政策",
            "无法满足", "不便描述", "无法为你", "无法为您", "不能提供"
        ];
        var lower = caption.ToLowerInvariant();
        foreach (var marker in markers)
            if (lower.Contains(marker, StringComparison.Ordinal)) return true;
        return false;
    }
}
