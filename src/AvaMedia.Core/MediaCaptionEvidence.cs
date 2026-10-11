using System.Text.Json;
using SkiaSharp;

namespace AvaMedia.Core;

/// <summary>Conservative visual facts, independent local checks, and source selection with enforced temporal coverage.</summary>
internal static class MediaCaptionEvidence
{
    private static readonly string[] Kinds = ["人物", "动物", "物体", "环境", "文字图表", "无法确认"];
    private static readonly string[] Gestures = ["身体前倾", "手臂伸展", "手部弯曲", "手部接触", "移动或转向", "持物", "无法确认"];
    private static readonly string[] Contacts = ["前景局部身体", "自身身体或衣物", "附近物体", "未见接触", "无法确认"];
    private static readonly string[] Clothing = ["内衣", "普通衣着", "裸露", "无法确认"];
    private static readonly string[] Themes = ["人物近距离互动", "人物活动", "动物活动", "物体或环境展示", "文字图表", "无法确认主要内容"];
    private static readonly string[] Regions = ["完整画面", "中心", "左上", "右上", "左下", "右下"];
    private static readonly string[] Judgments = ["可确认", "未见", "不确定"];
    private static readonly string[] ContactEvidence = ["口部与性器官接触", "性器官之间接触", "手部与性器官接触", "关键接触区域遮挡或出画", "未见直接性接触", "直接接触证据不足"];
    private sealed record Nsfw(string SexualActivity, string OralSex, string Glasses, string FeminineAppearance, string Mirror, string FacingMirror, string Evidence);
    private sealed record Frame(string Kind, string[] Actions, string Contact, string Clothing, string Background,
        string Detail, bool Uncertain, string Focus)
    {
        public Nsfw? Nsfw { get; init; }
        public string Facts
        {
            get
            {
                if (Kind != "人物") return string.IsNullOrWhiteSpace(Detail) ? Kind == "无法确认" ? "主要内容无法确认" : $"可见{Kind}" : Detail.Trim();
                var action = Actions.Where(value => value != "无法确认").ToArray();
                var content = "可见人物" + (action.Length == 0 ? "，主要动作未能明确确认" : string.Join("、", action));
                if (Contact == "前景局部身体")
                    content += Uncertain ? "，前景可见局部身体，归属或接触细节未确认" : "，手部靠近或接触前景局部身体";
                else if (!Uncertain && Contact is "自身身体或衣物" or "附近物体") content += $"，手部靠近或接触{Contact}";
                return content + (Nsfw is { } nsfw ? $"；性行为：{nsfw.SexualActivity}；口交：{nsfw.OralSex}" : "");
            }
        }
        public string Display => Facts + (Background.Length > 0 ? $"。背景：{Background}" : "")
            + (Uncertain ? "。接触对象或身体归属尚不确定" : "")
            + (Nsfw is { } nsfw ? $"。行为依据：{nsfw.Evidence}。眼镜：{nsfw.Glasses}；女性外观：{nsfw.FeminineAppearance}；镜子：{nsfw.Mirror}；朝向镜子：{nsfw.FacingMirror}" : "");
    }
    private sealed record Selection(Dictionary<string, string[]> Parts);
    private sealed record Overview(string Theme, string EnvironmentFrame);
    private sealed class FrameBudget { public int Remaining = 2; }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static object Choice(string[] values) => new { type = "string", @enum = values };
    private static object Text(int maximum) => new { type = "string", maxLength = maximum };
    private static readonly JsonElement FrameSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object", additionalProperties = false,
        properties = new
        {
            kind = Choice(Kinds), actions = new { type = "array", minItems = 1, maxItems = 2, items = Choice(Gestures) },
            contact = Choice(Contacts), clothing = Choice(Clothing), background = Text(48), detail = Text(80),
            uncertain = new { type = "boolean" }, focus = Choice(Regions)
        },
        required = new[] { "kind", "actions", "contact", "clothing", "background", "detail", "uncertain", "focus" }
    });
    private const string FrameRequest =
        "逐项辨识当前这一张图，输出 JSON。kind 选择主体类型；actions 选择最多两个清晰可见的动作状态，" +
        "单图不能确认移动或转向就用无法确认；contact 选择手部靠近或接触的对象类别，不能细化身体部位或推断所属；" +
        "clothing 只按可见衣物类别选择；background 只写必要环境物体，不含人物姿态；" +
        "detail 仅用于非人物主体的简洁事实，人物图用空字符串；uncertain 表示主要动作或接触对象类别是否遮挡/不明，非人物没有接触则不适用；" +
        "身体所属不明不妨碍记录可见的局部身体，但不能把它归属到任何主体。focus 选择主要动作/接触所在区域。" +
        "不凭背景家具或前景局部身体补出主体的坐躺姿态、腿脚、鞋子、身份、关系或动机。";

    private static JsonElement Schema(bool nsfw)
    {
        if (!nsfw) return FrameSchema;
        var properties = FrameSchema.GetProperty("properties").EnumerateObject().ToDictionary(value => value.Name, value => (object)value.Value.Clone());
        properties["nsfw"] = new
        {
            type = "object", additionalProperties = false,
            properties = new { evidence = Choice(ContactEvidence), sexualActivity = Choice(Judgments), oralSex = Choice(Judgments), glasses = Choice(Judgments),
                feminineAppearance = Choice(Judgments), mirror = Choice(Judgments), facingMirror = Choice(Judgments) },
            required = new[] { "sexualActivity", "oralSex", "glasses", "feminineAppearance", "mirror", "facingMirror", "evidence" }
        };
        return JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false, properties,
            required = FrameSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()!).Append("nsfw").ToArray() });
    }

    internal static async Task<string> GenerateAsync(ISummaryModel model, IReadOnlyList<byte[]> frames,
        IReadOnlyList<double> seconds, double duration, string system, string request, int tokens, bool nsfwMode,
        SummaryModelTool? frameTool, Action<int, int>? frameProgress, Action<MediaCaptionObservation>? observed,
        Action<int, int>? segmentProgress, Action? summarizing, CancellationToken ct)
    {
        var grounding = system + "\n输出要求改为 JSON。优先辨识主要动作和接触，身体所属看不清则保留不确定。" +
            "只有主要动作完全看不清才使用补帧工具，本任务最多补充两帧；身体所属不明直接省略，不为此反复补帧。";
        var budget = new FrameBudget();
        var schema = Schema(nsfwMode);
        var frameRequest = FrameRequest + (nsfwMode ? "\nnsfw 中逐项判断 sexualActivity 性行为、oralSex 口交、glasses 戴眼镜、" +
            "feminineAppearance 女性外观、mirror 可见镜子、facingMirror 朝向镜子，均按系统提示的证据规则填写。" +
            "先填写 evidence 接触证据，再据此判断行为。evidence 记录直接性接触的类型：只有性器官本身清晰可辨且看清接触时才能选择前三项；" +
            "不能将普通身体接触、内衣、腿部或前倾当作性接触证据。关键接触部位在画外或被遮挡，选择关键接触区域遮挡或出画，" +
            "性行为和口交均填不确定；没有直接性接触则选择未见直接性接触。不能为了支持行为判断反过来编造依据。" : "");
        var frameTokens = nsfwMode ? 416 : 256;
        var observations = new List<Frame>(frames.Count);
        for (var index = 0; index < frames.Count; index++)
        {
            ct.ThrowIfCancellationRequested(); frameProgress?.Invoke(index, frames.Count);
            var frame = ReadFrame(await model.CompleteAsync(grounding, frameRequest,
                ct, image: frames[index], tokens: frameTokens, schema: schema).ConfigureAwait(false), nsfwMode);
            observations.Add(frame);
            observed?.Invoke(new(seconds[index], MediaCaptionService.RequireCaption(frame.Display)) { SampleIndex = index });
            frameProgress?.Invoke(index + 1, frames.Count);
        }

        // Select from all observations without images. The final image cannot displace earlier evidence.
        var count = Math.Min(3, frames.Count);
        var ranges = Enumerable.Range(0, count).Select(part => Enumerable.Range(part * frames.Count / count,
            (part + 1) * frames.Count / count - part * frames.Count / count).ToArray()).ToArray();
        var allowed = ranges.Select(indices => indices.Select(Id).ToArray()).ToArray();
        var properties = Enumerable.Range(0, count).ToDictionary(part => $"p{part}", part => (object)new
        {
            type = "array", minItems = 1, maxItems = Math.Min(2, ranges[part].Length),
            items = Choice(allowed[part])
        });
        var selectionSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object", additionalProperties = false,
            properties = new { parts = new { type = "object", additionalProperties = false, properties, required = properties.Keys.ToArray() } },
            required = new[] { "parts" }
        });
        var records = string.Join("\n", Enumerable.Range(0, frames.Count).Select(index =>
            $"{Id(index)} [{MediaTime.Format(seconds[index])}] {observations[index].Display}"));
        segmentProgress?.Invoke(0, count);
        var selected = Read<Selection>(await model.CompleteAsync(grounding,
            $"视频时长 {MediaTime.Format(duration)}。\n{records}\n" +
            "仅选择采样编号，不生成新描述。每段选择一至两个最能体现主要动作或互动的时刻，" +
            "优先保留主要行为的直接证据、伸手、接触、姿态差异，避免用最后一帧或背景清单代替整段。每段都须覆盖，不跨段引用。\n" +
            (nsfwMode ? "NSFW 模式还须优先保留行为证据变化及眼镜、镜子等可确认的新属性。\n" : "") +
            string.Join("\n", allowed.Select((ids, part) => $"p{part}: {string.Join(',', ids)}")) + "\n用户要求：" + request,
            ct, tokens: 256, schema: selectionSchema).ConfigureAwait(false));
        if (selected.Parts is null || selected.Parts.Count != count) throw new InvalidDataException("总结缺少时间段依据。");
        var sources = new List<int[]>();
        for (var part = 0; part < count; part++)
        {
            if (!selected.Parts.TryGetValue($"p{part}", out var ids) || ids is null || ids.Length is < 1 or > 2
                || ids.Any(id => !allowed[part].Contains(id, StringComparer.Ordinal)))
                throw new InvalidDataException("总结引用了无效的采样编号。");
            var indices = ranges[part].Where(index => ids.Contains(Id(index), StringComparer.Ordinal))
                .DistinctBy(index => observations[index].Facts + (observations[index].Nsfw is { } value ? string.Join('|', Values(value)) : "")).ToArray();
            sources.Add(indices); segmentProgress?.Invoke(part + 1, count);
            foreach (var index in indices)
            {
                // An independent single-frame local view can challenge a claim; a multi-frame review cannot establish ownership.
                var original = observations[index];
                var focused = nsfwMode ? frames[index] : FocusImage(frames[index], original.Focus);
                var prompt = frameRequest + (nsfwMode ? "\n这是同一采样时刻的完整核对图，须重新核对行为及外观属性。" : "\n这是同一采样时刻的局部核对图。") + "画外信息用无法确认，" +
                    "只按这张图重新辨识，不延续前一次结论。只输出相同结构的 JSON。";
                Frame check;
                if (frameTool is not null && budget.Remaining > 0 && original.Actions.All(value => value == "无法确认")
                    && original.Contact == "无法确认" && model is ISummaryToolModel toolModel)
                {
                    // Supplemental frames stay inside this temporal window. Only agreement with the original observation is retained.
                    var boundedTool = WindowTool(frameTool, seconds[ranges[part][0]], seconds[ranges[part][^1]], budget);
                    var raw = await toolModel.CompleteWithToolsAsync(grounding, prompt + "\n输出须符合以下 JSON 字段和枚举：" + schema.GetRawText(),
                        [new($"核对 {MediaTime.Format(seconds[index])}", focused)], [boundedTool], ct, frameTokens).ConfigureAwait(false);
                    MediaCaptionService.RequireCaption(raw);
                    try { check = ReadFrame(raw, nsfwMode); }
                    catch (InvalidDataException)
                    {
                        check = ReadFrame(await model.CompleteAsync(grounding, prompt, ct, image: focused,
                            tokens: frameTokens, schema: schema).ConfigureAwait(false), nsfwMode);
                    }
                }
                else check = ReadFrame(await model.CompleteAsync(grounding, prompt, ct, image: focused,
                    tokens: frameTokens, schema: schema).ConfigureAwait(false), nsfwMode);
                var contact = original.Contact == check.Contact ? original.Contact : "无法确认";
                var actions = original.Actions.Intersect(check.Actions, StringComparer.Ordinal).ToArray();
                observations[index] = original with
                {
                    Kind = original.Kind == check.Kind ? original.Kind : "无法确认",
                    Actions = actions.Length > 0 ? actions : ["无法确认"], Contact = contact,
                    Clothing = original.Clothing == check.Clothing ? original.Clothing : "无法确认",
                    Uncertain = original.Uncertain || check.Uncertain || contact == "无法确认",
                    Detail = original.Kind == check.Kind && !original.Uncertain && !check.Uncertain ? original.Detail : "",
                    Nsfw = Merge(original.Nsfw, check.Nsfw)
                };
                observations[index] = GroundContact(observations[index]);
                observed?.Invoke(new(seconds[index], MediaCaptionService.RequireCaption(observations[index].Display)) { SampleIndex = index, IsVerified = true });
            }
        }

        summarizing?.Invoke();
        var finalIds = sources.SelectMany(indices => indices).Select(Id).ToArray();
        var overviewSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object", additionalProperties = false,
            properties = new { theme = Choice(Themes), environmentFrame = Choice(finalIds) },
            required = new[] { "theme", "environmentFrame" }
        });
        var verified = string.Join("\n", sources.Select((indices, part) => $"第 {part + 1} 段：" + string.Join("；", indices.Select(index =>
            $"{Id(index)} {observations[index].Display}"))));
        var overview = Read<Overview>(await model.CompleteAsync(grounding,
            verified + "\n以全部段落的核对结果选择全片主题，以及必要背景的采样编号；不生成新的事实。\n用户要求：" + request,
            ct, tokens: tokens, schema: overviewSchema).ConfigureAwait(false));
        if (!Themes.Contains(overview.Theme, StringComparer.Ordinal) || !finalIds.Contains(overview.EnvironmentFrame, StringComparer.Ordinal))
            throw new InvalidDataException("总结总述没有有效画面依据。");
        var environment = observations[Enumerable.Range(0, frames.Count).Single(index => Id(index) == overview.EnvironmentFrame)].Background;
        var clothing = sources.SelectMany(indices => indices).Select(index => observations[index].Clothing).Distinct().ToArray();
        var attire = clothing.Contains("内衣", StringComparer.Ordinal) ? "部分采样可见内衣。"
            : clothing.Length == 1 && clothing[0] == "裸露" ? "可见裸露。" : "";
        // The model selects meaning and sources; the renderer cannot invent seating, shoes, identities or missing transitions.
        return MediaCaptionService.RequireCaption($"采样画面主要呈现{overview.Theme}。{attire}" +
            (environment.Length > 0 ? $"背景：{environment}。" : "") +
            (nsfwMode ? "\n" + NsfwSummary(sources.SelectMany(indices => indices).Select(index => (seconds[index], observations[index].Nsfw!)).ToArray()) : "") +
            "\n\n" + string.Join("\n", sources.Select((indices, part) =>
                $"[{MediaTime.Format(seconds[ranges[part][0]])}–{MediaTime.Format(seconds[ranges[part][^1]])} 采样] " +
                string.Join("；", indices.Select(index => $"{MediaTime.Format(seconds[index])}：{observations[index].Facts}")))));
    }

    private static string Id(int index) => $"F{index + 1}";
    private static Frame ReadFrame(string raw, bool nsfwMode)
    {
        var frame = Read<Frame>(raw);
        if (!Kinds.Contains(frame.Kind, StringComparer.Ordinal) || frame.Actions is null || frame.Actions.Length is < 1 or > 2
            || frame.Actions.Any(value => !Gestures.Contains(value, StringComparer.Ordinal))
            || !Contacts.Contains(frame.Contact, StringComparer.Ordinal) || !Clothing.Contains(frame.Clothing, StringComparer.Ordinal)
            || frame.Background is null || frame.Detail is null || !Regions.Contains(frame.Focus, StringComparer.Ordinal)
            || nsfwMode && (frame.Nsfw is null || Values(frame.Nsfw).Any(value => !Judgments.Contains(value, StringComparer.Ordinal))
                || !ContactEvidence.Contains(frame.Nsfw.Evidence, StringComparer.Ordinal)))
            throw new InvalidDataException("画面观察字段无效。");
        if (frame.Nsfw is { } nsfw)
        {
            // An occluded act remains unknown; a visible specific act also supports its umbrella category.
            nsfw = Ground(nsfw);
            if (nsfw.Mirror != "可确认" && nsfw.FacingMirror == "可确认") nsfw = nsfw with { FacingMirror = "不确定" };
            frame = frame with { Nsfw = nsfw };
        }
        return GroundContact(frame);
    }
    private static Frame GroundContact(Frame frame) => frame.Nsfw is { Evidence: "手部与性器官接触" } value
        && (frame.Uncertain || frame.Contact is not ("前景局部身体" or "自身身体或衣物") || !frame.Actions.Contains("手部接触", StringComparer.Ordinal))
        ? frame with { Nsfw = Ground(value with { Evidence = "直接接触证据不足" }) } : frame;
    private static string[] Values(Nsfw value) => [value.SexualActivity, value.OralSex, value.Glasses, value.FeminineAppearance, value.Mirror, value.FacingMirror];
    private static Nsfw? Merge(Nsfw? original, Nsfw? check)
    {
        if (original is null || check is null) return null;
        var values = Values(original).Zip(Values(check), (left, right) => left == right ? left : "不确定").ToArray();
        var evidence = original.Evidence == check.Evidence ? original.Evidence : "直接接触证据不足";
        return Ground(new(values[0], values[1], values[2], values[3], values[4], values[5], evidence));
    }
    private static Nsfw Ground(Nsfw value)
    {
        if (value.Evidence is "关键接触区域遮挡或出画" or "直接接触证据不足") return value with { SexualActivity = "不确定", OralSex = "不确定" };
        if (value.Evidence == "未见直接性接触") return value with { SexualActivity = "未见", OralSex = "未见" };
        if (value.OralSex == "可确认" && value.Evidence != "口部与性器官接触") value = value with { OralSex = "不确定" };
        if (value.OralSex == "可确认") return value with { SexualActivity = "可确认" };
        // A proposed contact category is not evidence when the same observation cannot confirm the act.
        return value.SexualActivity == "可确认" ? value
            : value with { SexualActivity = "不确定", OralSex = "不确定", Evidence = "直接接触证据不足" };
    }
    private static string NsfwSummary((double Seconds, Nsfw Value)[] frames)
    {
        string[] labels = ["性行为", "口交", "戴眼镜", "女性外观", "可见镜子", "朝向镜子"];
        return "NSFW 判断（仅覆盖已核对采样）：" + string.Join("；", labels.Select((label, index) =>
        {
            var positive = frames.Where(frame => Values(frame.Value)[index] == "可确认").ToArray();
            if (positive.Length > 0) return $"{label}可确认（{string.Join('、', positive.Select(frame => MediaTime.Format(frame.Seconds))) }）";
            return label + (frames.All(frame => Values(frame.Value)[index] == "未见") ? "未见" : "不确定");
        })) + "。";
    }
    private static byte[] FocusImage(byte[] png, string focus)
    {
        // Broad, overlapping regions retain neighbouring objects without asking a small model for precise coordinates.
        if (focus == "完整画面") return png;
        using var source = SKBitmap.Decode(png) ?? throw new InvalidDataException("无法解码核对画面。");
        var x = focus == "中心" ? .15 : focus.StartsWith("右", StringComparison.Ordinal) ? .25 : 0;
        var y = focus == "中心" ? .15 : focus.EndsWith("下", StringComparison.Ordinal) ? .25 : 0;
        var extent = focus == "中心" ? .7 : .75;
        var rect = new SKRectI((int)(x * source.Width), (int)(y * source.Height),
            (int)Math.Ceiling((x + extent) * source.Width), (int)Math.Ceiling((y + extent) * source.Height));
        using var crop = new SKBitmap();
        if (!source.ExtractSubset(crop, rect)) return png;
        using var encoded = crop.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
    private static SummaryModelTool WindowTool(SummaryModelTool tool, double start, double end, FrameBudget budget) => tool with
    {
        ExecuteAsync = (arguments, ct) =>
        {
            if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("seconds", out var times) || times.ValueKind != JsonValueKind.Array
                || times.EnumerateArray().Any(time => time.ValueKind != JsonValueKind.Number || !time.TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds < start || seconds > end))
                return Task.FromResult(new SummaryModelToolResult("仅能核对当前片段的时间范围。无法确认的细节应省略。"));
            if (times.GetArrayLength() > budget.Remaining)
                return Task.FromResult(new SummaryModelToolResult("补帧预算不足，依据已有画面保留未知。"));
            budget.Remaining -= times.GetArrayLength();
            return tool.ExecuteAsync(arguments, ct);
        }
    };
    private static T Read<T>(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal))
        {
            var line = text.IndexOf('\n');
            if (line >= 0) text = text[(line + 1)..^3].Trim();
        }
        try { return JsonSerializer.Deserialize<T>(text, JsonOptions) ?? throw new InvalidDataException("画面模型未返回结构化证据。"); }
        catch (JsonException error) { throw new InvalidDataException("画面模型返回的证据格式无效。", error); }
    }
}
