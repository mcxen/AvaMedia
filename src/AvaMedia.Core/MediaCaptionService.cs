namespace AvaMedia.Core;

public sealed record MediaCaptionObservation(double Seconds, string Text);

/// <summary>Optional local or OpenAI-compatible vision captions after JoyTag. Never writes source media.</summary>
public static class MediaCaptionService
{
    public const string DefaultSystemPrompt =
        "你是媒体画面描述助手，用中文描述图像中有直接视觉证据的事实。" +
        "先核对主体正在做什么、身体姿态和相对位置，再核对环境和物体；不要用服饰和家具清单代替主要内容。输出前删去无法从画面确认的细节，不展示分析过程。" +
        "不凭服饰或外观猜测人物的身份、性别、年龄、关系、职业，不凭建筑外观猜测校园、办公区等场所类型、地域或拍摄用途。" +
        "文字只有清晰可辨时才逐字转写；模糊的标牌只描述为标牌，不能补全、猜测或编造文字。" +
        "视频采样图不是连续录像：只陈述采样时刻可见的状态，只有多个画面共同支持时才描述变化，不能补写未采样的动作、因果或完整轨迹。" +
        "不同画面的人物不能直接认定为同一人，不能把跨帧人数相加；不确定的细节直接省略。" +
        "对裸露或成人画面，仅作中立、非露骨的内容识别与描述，可说明裸露和身体遮挡状态；不写色情渲染、感官体验或露骨行为细节，不把普通姿态推断为性行为。" +
        "不要罗列不存在的裸露或成人内容，不输出免责声明。只输出简洁的描述正文。";

    public const string DefaultUserPrompt =
        "请用一两段简洁中文总结画面的主要内容，先写主体及明确可见的动作或互动，再补充必要的环境。" +
        "只描述有画面证据的变化，不跨帧累计人数，无法确定的细节直接省略。";

    public static void ValidateOptions(MediaTagOptions options)
    {
        if (options.CaptionMaxTokens is < 64 or > 4096) throw new ArgumentException("画面描述最大输出须为 64–4096。");
        if (options.CaptionPrompt is { Length: > 4000 }) throw new ArgumentException("画面描述提示过长。");
        if (options.CaptionSystemPrompt is { Length: > 8000 }) throw new ArgumentException("画面描述系统提示过长。");
        if (options.CaptionLocalModelId is not null and not ModelCatalog.SummaryQwen35Id)
            throw new ArgumentException("所选本地画面描述模型无效。");
        if (options.CaptionProviderId is { Length: > 0 } && !Guid.TryParseExact(options.CaptionProviderId, "N", out _))
            throw new ArgumentException("画面描述供应商标识无效。");
    }

    /// <summary>Prepare provider options so OnlineSummaryModel can open a vision session (TextModel may be empty for vision-only Ollama).</summary>
    public static OnlineAiOptions PrepareProvider(OnlineAiOptions source)
    {
        var options = source.Clone();
        if (string.IsNullOrWhiteSpace(options.EffectiveVisionModel))
            throw new ArgumentException("请在 AI 供应商中配置视觉模型（例如 Ollama 的 moondream / llava）。");
        // Captions are free-form prose; do not force JSON response_format.
        options.ResponseFormat = OnlineAiResponseFormat.Prompt;
        return options;
    }

    public static async Task<(string Caption, string Model)> GenerateAsync(
        OnlineAiOptions? provider, IReadOnlyList<byte[]> frames, MediaTagOptions options, CancellationToken ct,
        ISummaryModel? model = null, IReadOnlyList<double>? frameSeconds = null, double videoDurationSeconds = 0,
        SummaryModelTool? frameTool = null, Action<int, int>? frameProgress = null,
        Action<MediaCaptionObservation>? observed = null, Action? summarizing = null)
    {
        if (frames.Count == 0) throw new ArgumentException("没有可用于画面描述的采样帧。");
        if (frameSeconds is not null && (frameSeconds.Count != frames.Count || !double.IsFinite(videoDurationSeconds) || videoDurationSeconds <= 0
            || frameSeconds.Any(seconds => !double.IsFinite(seconds) || seconds < 0 || seconds > videoDurationSeconds)
            || frameSeconds.Zip(frameSeconds.Skip(1), (previous, next) => previous > next).Any(unordered => unordered)))
            throw new ArgumentException("视频描述采样时间无效。");
        ValidateOptions(options);
        var system = string.IsNullOrWhiteSpace(options.CaptionSystemPrompt) ? DefaultSystemPrompt : options.CaptionSystemPrompt.Trim();
        var prompt = string.IsNullOrWhiteSpace(options.CaptionPrompt) ? DefaultUserPrompt : options.CaptionPrompt.Trim();
        if (frameSeconds is not null)
            prompt = $"以下为同一视频按时间顺序抽取的 {frames.Count} 个画面，视频总时长 {MediaTime.Format(videoDurationSeconds)}。" +
                "每张图前附实际采样时间，采样点之间的内容未知。先概括可见场景，再描述有画面依据的变化；必要时注明采样时间。\n" + prompt;
        // Keep every requested sample, including similar-looking frames: tag-score reuse must not erase motion evidence.
        IReadOnlyList<SummaryModelImage>? images = frames.Count == 1 && frameSeconds is null ? null
            : frames.Select((png, index) => new SummaryModelImage(frameSeconds is null ? $"画面 {index + 1}"
                : $"采样画面 {index + 1} · {MediaTime.Format(frameSeconds[index])}", png)).ToArray();

        if (frameTool is not null && frameSeconds is not null)
            system += "遇到采样间动作、遮挡或局部细节无法确认时，可用 get_video_frames 查看指定时间的画面，必要时指定局部区域。" +
                "最多补充 8 帧、2 轮；工具结果后的图像是实际画面证据。工具失败或仍看不清时省略该细节，不能把工具参数或请求目的当作事实。";
        await using var ownedModel = model is null
            ? new OnlineSummaryModel(PrepareProvider(provider ?? throw new ArgumentException("缺少画面描述供应商。")), vision: true)
            : null;
        var vision = model ?? ownedModel!;
        byte[]? image = images is null ? frames[0] : null;
        if (LocalSummaryModelCache.IsLocal(vision) && frameSeconds is not null)
        {
            // Joint image input can carry a subject from one sample into an unrelated sample.
            // Observe each frame independently before asking the same session to organize the evidence.
            var observations = new System.Text.StringBuilder();
            for (var index = 0; index < frames.Count; index++)
            {
                ct.ThrowIfCancellationRequested(); frameProgress?.Invoke(index, frames.Count);
                var observation = RequireCaption(await vision.CompleteAsync(system,
                    $"这是视频 {MediaTime.Format(frameSeconds[index])} 的单张采样画面。" +
                    "只用一两句话记录这张图中的主要内容：先写清晰可见的动作、姿态、人物与物体的相对位置，再写必要的环境。" +
                    "不要只列服饰和家具；纯色或看不清的画面照实说明。" +
                    "精确人数和细节只有看清时才写，不能描述之前、之后或采样间的动作。",
                    ct, image: frames[index], tokens: 256).ConfigureAwait(false));
                observations.Append('[').Append(MediaTime.Format(frameSeconds[index])).Append("] ").AppendLine(observation);
                observed?.Invoke(new(frameSeconds[index], observation));
                frameProgress?.Invoke(index + 1, frames.Count);
            }
            prompt += "\n\n以下是每张采样图独立观察的记录，请按时间整理成简洁描述。" +
                "各时间点只使用对应记录，不能把前一帧的主体延续到下一帧；不要补写采样间的动作。\n" + observations;
            // Keep original visual evidence in the synthesis request; frame descriptions may contain mistakes.
            // Three small, timestamped images leave room for observations and any requested tool frames.
            var indices = new[] { 0, frames.Count / 2, frames.Count - 1 }.Distinct().ToArray();
            images = indices.Select(index => new SummaryModelImage($"核对画面 · {MediaTime.Format(frameSeconds[index])}",
                VideoSummarySampling.ModelImage(frames[index]))).ToArray();
            prompt += "\n随附部分原始采样图用于核对。记录与对应图像冲突时以图像为准，删去误读，不能将局部身体或跨帧主体拼成新增人物。";
            image = null;
        }
        summarizing?.Invoke();
        string caption;
        if (frameTool is not null && frameSeconds is not null)
        {
            if (vision is not ISummaryToolModel toolModel) throw new ArgumentException("所选模型不支持画面工具。");
            caption = await toolModel.CompleteWithToolsAsync(system, prompt, images ?? [], [frameTool], ct,
                options.CaptionMaxTokens).ConfigureAwait(false);
        }
        else caption = await vision.CompleteAsync(system, prompt, ct, image, options.CaptionMaxTokens, images: images).ConfigureAwait(false);
        return (RequireCaption(caption), vision.ModelId);
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
        if (LooksLikeRefusal(caption)) throw new InvalidDataException("画面描述模型拒绝请求，请调整描述要求或更换视觉模型。");
        return caption;
    }

    public static bool LooksLikeRefusal(string caption)
    {
        ReadOnlySpan<string> markers =
        [
            "i cannot", "i can't", "i'm unable", "i am unable", "as an ai", "sorry, but", "i won't", "i will not",
            "cannot assist", "can't assist", "cannot help with", "can't help with", "not able to provide", "against my guidelines",
            "无法协助", "无法描述", "不能描述", "我不能", "抱歉，我无法", "作为人工智能", "内容违规", "违反政策",
            "无法满足", "不便描述", "无法为你", "无法为您", "不能提供", "我无法回答"
        ];
        var lower = caption.ToLowerInvariant();
        foreach (var marker in markers)
            if (lower.Contains(marker, StringComparison.Ordinal)) return true;
        return false;
    }
}
