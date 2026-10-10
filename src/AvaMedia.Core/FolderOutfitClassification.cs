using System.Security.Cryptography;
using System.Text;

namespace AvaMedia.Core;

/// <summary>Group clothing tags with local clothing-mask colors; retain uncertain samples for review.</summary>
public static class FolderOutfitClassification
{
    private static readonly HashSet<string> Garments = new(StringComparer.OrdinalIgnoreCase)
    {
        "shirt", "t-shirt", "dress", "skirt", "pants", "shorts", "jeans", "jacket", "coat", "sweater", "cardigan",
        "hoodie", "vest", "blouse", "kimono", "yukata", "cheongsam", "china_dress", "chinese_clothes", "japanese_clothes",
        "school_uniform", "sailor_collar", "serafuku", "military_uniform", "uniform", "suit", "maid", "maid_apron",
        "apron", "pajamas", "sundress", "wedding_dress", "sailor_dress", "gym_uniform", "leotard", "bodysuit",
        "swimsuit", "bikini", "one-piece_swimsuit", "school_swimsuit", "competition_swimsuit", "lingerie", "bra",
        "panties", "underwear", "camisole", "tank_top", "crop_top", "sleeveless_shirt", "dress_shirt", "collared_shirt",
        "pleated_skirt", "miniskirt", "long_skirt", "pencil_skirt", "frilled_dress", "frilled_skirt", "sleeveless_dress",
        "strapless_dress", "short_dress", "long_dress", "sweater_dress", "turtleneck", "sleeveless_turtleneck"
    };
    private static readonly HashSet<string> Details = new(StringComparer.OrdinalIgnoreCase)
    {
        "thighhighs", "stockings", "pantyhose", "fishnets", "fishnet_legwear", "garter_straps", "garter_belt",
        "socks", "kneehighs", "boots", "high_heels", "shoes", "gloves", "elbow_gloves", "necktie", "bowtie",
        "ribbon", "hair_ribbon", "hair_bow", "choker", "collar", "maid_headdress", "bunny_ears", "animal_ears",
        "detached_sleeves", "long_sleeves", "short_sleeves", "puffy_sleeves", "sleeveless", "off_shoulder",
        "bare_shoulders", "frills", "lace", "lace_trim", "ribbon_trim", "fur_trim", "see-through", "sheer_clothes",
        "clothing_cutout", "cross-laced_clothes", "lace-up", "halter_top", "halterneck", "strapless", "backless_outfit",
        "polka_dot", "plaid", "stripes", "floral_print", "checkered_clothes", "patterned_clothing"
    };
    private static readonly string[] Colors = ["white", "black", "red", "blue", "green", "yellow", "pink", "purple", "brown", "grey", "orange", "multicolored"];
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["shirt"] = "衬衫", ["dress"] = "连衣裙", ["skirt"] = "裙子", ["pants"] = "长裤", ["shorts"] = "短裤",
        ["bra"] = "文胸", ["panties"] = "内裤", ["underwear"] = "内衣", ["lingerie"] = "内衣", ["school_uniform"] = "校服",
        ["sleeveless_dress"] = "无袖连衣裙", ["off_shoulder"] = "露肩款", ["bare_shoulders"] = "露肩款",
        ["tank_top"] = "吊带背心", ["maid"] = "女仆装", ["bodysuit"] = "连体衣", ["camisole"] = "吊带上衣",
        ["miniskirt"] = "短裙", ["sleeveless"] = "无袖", ["serafuku"] = "水手服", ["bowtie"] = "领结"
    };
    private static readonly string[] ColorLabels = ["白色", "黑色", "红色", "蓝色", "绿色", "黄色", "粉色", "紫色", "棕色", "灰色", "橙色", "多色"];
    private sealed record Feature(string Tag, double Weight, bool Garment, string? ColorPart = null, string? Color = null);
    private static readonly Lazy<Feature[]> Features = new(() => WordLibraryCatalog.JoyTags.Order(StringComparer.Ordinal).Select(Describe).OfType<Feature>().ToArray());
    private sealed record Sample(double Seconds, double[] Values, double[] Colors, double[] Embedding);
    private sealed record Profile(FolderClassifiedFile File, Sample[] Samples, double[] Values, double[] Colors, double[] Embedding, string? Error);

    private static Feature? Describe(string tag)
    {
        foreach (var color in Colors)
        {
            if (!tag.StartsWith(color + "_", StringComparison.Ordinal)) continue;
            var part = tag[(color.Length + 1)..];
            if (Garments.Contains(part)) return new(tag, 3, true, part, color);
            if (Details.Contains(part)) return new(tag, .9, false, part, color);
        }
        if (Garments.Contains(tag)) return new(tag, tag is "shirt" or "dress" or "skirt" or "underwear" or "uniform" ? 1.2 : 2, true);
        if (Details.Contains(tag)) return new(tag, .65, false);
        if ((tag.StartsWith("striped_", StringComparison.Ordinal) || tag.StartsWith("plaid_", StringComparison.Ordinal)
            || tag.StartsWith("polka_dot_", StringComparison.Ordinal) || tag.StartsWith("frilled_", StringComparison.Ordinal))
            && Garments.Contains(tag[(tag.IndexOf('_') + 1)..])) return new(tag, 2.5, true);
        return null;
    }

    private static string Label(Feature feature) => feature.Color is { } color && feature.ColorPart is { } part
        ? ColorLabels[Array.IndexOf(Colors, color)] + Labels.GetValueOrDefault(part, WordLibraryCatalog.TagLabel(part))
        : Labels.GetValueOrDefault(feature.Tag, WordLibraryCatalog.TagLabel(feature.Tag));

    public static FolderClassifiedFile[] Apply(IEnumerable<FolderClassifiedFile> files, IReadOnlyList<FolderClassificationRule> rules,
        bool includeNsfw = false, CancellationToken ct = default)
    {
        var output = files.ToArray();
        foreach (var rule in rules.Where(rule => rule.ByOutfit))
        {
            rule.Validate();
            output = output.Select(file => file with { Decisions = file.Decisions.Select(decision => decision.RuleId == rule.Id
                && decision.OutfitIncludesNsfw != includeNsfw ? decision with { Manual = false } : decision).ToArray() }).ToArray();
            var decisions = Group(output, rule, includeNsfw, ct);
            output = output.Select(file => file with { Decisions = file.Decisions.Select(decision => decision.RuleId == rule.Id && !decision.Manual
                ? decisions[file.Media.Path] : decision).ToArray() }).ToArray();
        }
        return output;
    }

    public static FolderClassificationCategory[] Categories(FolderClassificationRule rule, IEnumerable<FolderClassifiedFile> files)
        => rule.ByOutfit ? files.SelectMany(file => file.Decisions).Where(decision => decision.RuleId == rule.Id && decision.CategoryId is not null)
            .GroupBy(decision => decision.CategoryId!).Select(group => new FolderClassificationCategory(group.Key, group.First().CategoryName, group.First().CategoryName))
            .OrderBy(category => category.Name, StringComparer.Ordinal).ToArray() : rule.Categories;

    public static FolderClassifiedFile KeepGroups(FolderClassifiedFile classified, FolderClassifiedFile previous, IReadOnlyList<FolderClassificationRule> rules, bool includeNsfw)
    {
        if (classified.Media.Length != previous.Media.Length || classified.Media.LastWriteUtc != previous.Media.LastWriteUtc) return classified;
        var ids = rules.Where(rule => rule.ByOutfit).Select(rule => rule.Id).ToHashSet();
        return classified with { Decisions = classified.Decisions.Select(decision => ids.Contains(decision.RuleId)
            ? previous.Decisions.FirstOrDefault(old => old.RuleId == decision.RuleId && old.OutfitIncludesNsfw == includeNsfw) ?? decision : decision).ToArray() };
    }

    private static Dictionary<string, FolderClassificationDecision> Group(FolderClassifiedFile[] files, FolderClassificationRule rule, bool includeNsfw, CancellationToken ct)
    {
        var features = Features.Value.Where(feature => includeNsfw || !MediaPrivacy.IsSensitiveTag(feature.Tag)).ToArray();
        var threshold = rule.OutfitSimilarity;
        var profiles = files.Select(file => Build(file, features)).ToArray();
        // Common accessories carry less information than an outfit-specific color, garment or pattern.
        var weights = features.Select((feature, index) => feature.Weight * (1 + Math.Log((profiles.Length + 1d)
            / (1 + profiles.Count(profile => profile.Values[index] >= .4))))).ToArray();
        double[] Vector(double[] values) => Normalize(values.Select((value, index) => Math.Pow(Math.Max(0, value - .15), 1.4) * Math.Sqrt(weights[index])).ToArray());
        // Compare garment families as well as detailed tags, so a hidden sleeve or hem does not create a new outfit.
        var families = features.Select(feature => Family(feature.ColorPart ?? feature.Tag)).OfType<string>().Distinct().Order().ToArray();
        double[] BroadVector(double[] values)
        {
            var broad = new double[families.Length];
            for (var index = 0; index < features.Length; index++)
            {
                var feature = features[index];
                if (Family(feature.ColorPart ?? feature.Tag) is { } family)
                {
                    var target = Array.IndexOf(families, family);
                    broad[target] = Math.Max(broad[target], values[index]);
                }
            }
            return Normalize(broad.Select(value => Math.Pow(Math.Max(0, value - .15), 1.4)).ToArray());
        }
        // Garment crops change with pose and partial removal; visual features refine the tag decision rather than veto it.
        double Match(Sample left, Sample right) => PaletteConflict(left.Colors, right.Colors) || TagColorConflict(left.Values, right.Values, features) ? 0
            : .25 * Dot(left.Embedding, right.Embedding) + .2 * Dot(Palette(left.Colors), Palette(right.Colors))
                + .3 * Dot(Vector(left.Values), Vector(right.Values)) + .25 * Dot(BroadVector(left.Values), BroadVector(right.Values));
        // Ignore samples with no visible clothing, but never average two different outfits into a third one.
        profiles = profiles.Select(profile =>
        {
            if (profile.Samples.Length == 0) return profile;
            var representative = profile.Samples.OrderByDescending(sample => profile.Samples.Count(other => Match(sample, other) >= threshold - .1))
                .ThenByDescending(sample => sample.Values.Select((value, index) => features[index].Garment ? value : 0).DefaultIfEmpty().Max()).First();
            return profile with { Values = representative.Values, Colors = representative.Colors, Embedding = representative.Embedding };
        }).ToArray();
        var vectors = profiles.ToDictionary(profile => profile.File.Media.Path, profile => Vector(profile.Values), BatchRename.PathComparer);
        var broadVectors = profiles.ToDictionary(profile => profile.File.Media.Path, profile => BroadVector(profile.Values), BatchRename.PathComparer);
        var palettes = profiles.ToDictionary(profile => profile.File.Media.Path, profile => Palette(profile.Colors), BatchRename.PathComparer);
        var review = new Dictionary<string, FolderClassificationDecision>(BatchRename.PathComparer);
        foreach (var profile in profiles)
        {
            ct.ThrowIfCancellationRequested();
            var frameVectors = profile.Samples.Select(sample => Vector(sample.Values)).ToArray();
            var consistency = frameVectors.Length == 0 ? 0 : profile.Samples.Count(sample => Match(sample, new(0, profile.Values, profile.Colors, profile.Embedding)) >= threshold - .1) / (double)frameVectors.Length;
            var error = profile.Error ?? (consistency < .8 ? "采样服装有变化或服装信息不足" : null);
            if (error is not null) review[profile.File.Media.Path] = new(rule.Id, rule.Name, null, "待确认", [],
                profile.Samples.Select((sample, index) => new FolderFrameClassification(sample.Seconds, null, Dot(frameVectors[index], vectors[profile.File.Media.Path]), null)).ToArray(),
                null, consistency, error);
        }
        double Similarity(Profile left, Profile right)
        {
            if (PaletteConflict(left.Colors, right.Colors) || TagColorConflict(left.Values, right.Values, features)) return 0;
            return .25 * Dot(left.Embedding, right.Embedding) + .2 * Dot(palettes[left.File.Media.Path], palettes[right.File.Media.Path])
                + .3 * Dot(vectors[left.File.Media.Path], vectors[right.File.Media.Path])
                + .25 * Dot(broadVectors[left.File.Media.Path], broadVectors[right.File.Media.Path]);
        }
        var groups = new List<List<Profile>>();
        foreach (var profile in profiles.Where(profile => !review.ContainsKey(profile.File.Media.Path))
            .OrderByDescending(profile => profile.Values.Select((value, index) => features[index].Garment ? value : 0).DefaultIfEmpty().Max())
            .ThenBy(profile => profile.File.Media.Path, BatchRename.PathComparer))
        {
            ct.ThrowIfCancellationRequested();
            var best = groups.Select(group => (Group: group, Scores: group.Select(other => Similarity(profile, other)).ToArray()))
                .Where(match => match.Scores.Min() >= threshold - .02 && match.Scores.Average() >= threshold)
                .OrderByDescending(match => match.Scores.Average()).FirstOrDefault();
            if (best.Group is null) groups.Add([profile]); else best.Group.Add(profile);
        }
        // Merge compatible groups as a whole; a single bridging photo cannot chain unrelated outfits together.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            (int Left, int Right, double Mean)? merge = null;
            for (var left = 0; left < groups.Count; left++)
                for (var right = left + 1; right < groups.Count; right++)
                {
                    var scores = groups[left].SelectMany(a => groups[right].Select(b => Similarity(a, b))).ToArray();
                    var mean = scores.Average();
                    if (scores.Min() >= threshold - .02 && mean >= threshold && (merge is null || mean > merge.Value.Mean))
                        merge = (left, right, mean);
                }
            if (merge is not { } selected) break;
            groups[selected.Left].AddRange(groups[selected.Right]); groups.RemoveAt(selected.Right);
        }
        var numbered = groups.OrderByDescending(group => group.Count).ThenBy(group => group.Select(profile => profile.File.Media.Path).Order(BatchRename.PathComparer).First(), BatchRename.PathComparer).ToArray();
        var metadata = numbered.Select((group, number) =>
        {
            var center = Normalize(Enumerable.Range(0, features.Length).Select(index => group.Average(profile => vectors[profile.File.Media.Path][index])).ToArray());
            var broadCenter = Normalize(Enumerable.Range(0, families.Length).Select(index => group.Average(profile => broadVectors[profile.File.Media.Path][index])).ToArray());
            var paletteCenter = Normalize(Enumerable.Range(0, Colors.Length).Select(index => group.Average(profile => palettes[profile.File.Media.Path][index])).ToArray());
            var embeddingCenter = Normalize(Enumerable.Range(0, 384).Select(index => group.Average(profile => profile.Embedding[index])).ToArray());
            var descriptions = features.Select((feature, index) => (Feature: feature with { Tag = feature.ColorPart ?? feature.Tag, Color = null, ColorPart = null }, Score: group.Average(profile => profile.Values[index])))
                .GroupBy(item => item.Feature.Tag).Select(items => items.MaxBy(item => item.Score))
                .Where(item => item.Score >= .3).OrderByDescending(item => item.Score * (item.Feature.Color is not null ? 1.4 : item.Feature.Garment ? 1.1 : .6))
                .ToArray();
            var specific = descriptions.Any(item => item.Feature.Garment && item.Feature.Tag is not ("underwear" or "lingerie"));
            var labels = descriptions.Where(item => !descriptions.Any(other => other.Feature.Garment && other.Feature.ColorPart == item.Feature.Tag)
                && (!specific || item.Feature.Tag is not ("underwear" or "lingerie")))
                .Select(item => Label(item.Feature)).Distinct().Take(3).ToArray();
            var representative = group.OrderByDescending(profile => Dot(vectors[profile.File.Media.Path], center)).ThenBy(profile => profile.File.Media.Path, BatchRename.PathComparer).First();
            var id = "outfit-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(representative.File.Media.Path)))[..16].ToLowerInvariant();
            var dominant = Enumerable.Range(0, Colors.Length).OrderByDescending(index => group.Average(profile => profile.Colors[index])).First();
            var name = $"{number + 1:00} {ColorLabels[dominant]} · " + string.Join(" · ", labels);
            if (name.Length > 60) name = name[..60];
            BatchRename.ValidateRenameKeyword(name);
            return (Group: group, Center: center, BroadCenter: broadCenter, PaletteCenter: paletteCenter, EmbeddingCenter: embeddingCenter, Id: id, Name: name);
        }).ToArray();
        foreach (var group in metadata)
            foreach (var profile in group.Group)
            {
                ct.ThrowIfCancellationRequested();
                var frameVectors = profile.Samples.Select(sample => (Detail: Vector(sample.Values), Broad: BroadVector(sample.Values), Palette: Palette(sample.Colors), sample.Embedding)).ToArray();
                var frames = profile.Samples.Select((sample, index) => new FolderFrameClassification(sample.Seconds, group.Id,
                    .25 * Dot(sample.Embedding, group.EmbeddingCenter) + .2 * Dot(frameVectors[index].Palette, group.PaletteCenter) + .3 * Dot(frameVectors[index].Detail, group.Center) + .25 * Dot(frameVectors[index].Broad, group.BroadCenter), null)).ToArray();
                var scores = metadata.Select(candidate => new FolderCategoryScore(candidate.Id, candidate.Name,
                    .25 * Dot(profile.Embedding, candidate.EmbeddingCenter) + .2 * Dot(palettes[profile.File.Media.Path], candidate.PaletteCenter) + .3 * Dot(vectors[profile.File.Media.Path], candidate.Center) + .25 * Dot(broadVectors[profile.File.Media.Path], candidate.BroadCenter),
                    frameVectors.Count(frame => .25 * Dot(frame.Embedding, candidate.EmbeddingCenter) + .2 * Dot(frame.Palette, candidate.PaletteCenter) + .3 * Dot(frame.Detail, candidate.Center) + .25 * Dot(frame.Broad, candidate.BroadCenter) >= threshold - .1)))
                    .OrderByDescending(score => score.Similarity).Take(5).ToArray();
                var agreement = frames.Count(frame => frame.Similarity >= threshold - .1) / (double)frames.Length;
                review[profile.File.Media.Path] = new(rule.Id, rule.Name, agreement >= .8 ? group.Id : null, agreement >= .8 ? group.Name : "待确认", scores, frames,
                    frames.MaxBy(frame => frame.Similarity)?.Seconds, agreement, agreement < .8 ? "采样服装与分组有分歧"
                    : "服装标签与图像特征分组 · " + string.Join(" · ", features.Select((feature, index) => (feature, Value: profile.Values[index]))
                        .Where(item => item.Value >= .4).OrderByDescending(item => item.Value).Select(item => Label(item.feature with
                        { Tag = item.feature.ColorPart ?? item.feature.Tag, Color = null, ColorPart = null })).Distinct().Take(6)));
            }
        return review.ToDictionary(item => item.Key, item => item.Value with { OutfitIncludesNsfw = includeNsfw }, BatchRename.PathComparer);
    }

    private static Profile Build(FolderClassifiedFile file, Feature[] features)
    {
        var media = file.Media;
        var indices = media.Scores.Select((score, index) => (score.Tag, index)).ToDictionary(item => item.Tag, item => item.index, StringComparer.OrdinalIgnoreCase);
        double[] Read(IReadOnlyList<double> values) => features.Select(feature => indices.TryGetValue(feature.Tag, out var index) && index < values.Count
            && double.IsFinite(values[index]) && values[index] is >= 0 and <= 1 ? values[index] : 0).ToArray();
        var video = VideoFormats.IsVideo(media.Path);
        var frames = video ? media.Frames.Where(frame => frame.Values.Length == media.Scores.Count)
            .Select(frame => (frame.Seconds, Values: Read(frame.Values.Select(value => (double)value).ToArray()))).ToArray()
            : [(Seconds: 0d, Values: Read(media.Scores.Select(score => score.Score).ToArray()))];
        var samples = frames.Select(frame => (frame, Appearance: media.OutfitFrames.FirstOrDefault(appearance => Math.Abs(appearance.Seconds - frame.Seconds) < .001)))
            .Where(item => item.Appearance is { Pixels: >= 128, Confidence: >= .6, Colors.Length: 12, Embedding.Length: 384 }
                && item.Appearance.Encoder == OutfitAppearanceService.Encoder && item.Appearance.Colors.All(value => double.IsFinite(value) && value is >= 0 and <= 1)
                && item.Appearance.Embedding.All(double.IsFinite) && item.Appearance.Embedding.Any(value => value != 0))
            .Select(item => new Sample(item.frame.Seconds, item.frame.Values, item.Appearance!.Colors, item.Appearance.Embedding)).ToArray();
        var values = Enumerable.Range(0, features.Length).Select(index => samples.Length == 0 ? 0 : samples.Average(sample => sample.Values[index])).ToArray();
        var colors = Enumerable.Range(0, Colors.Length).Select(index => samples.Length == 0 ? 0 : samples.Average(sample => sample.Colors[index])).ToArray();
        var recognizable = features.Select((feature, index) => feature.Garment ? values[index] : 0).DefaultIfEmpty().Max() >= .4;
        var error = frames.Length == 0 || video && frames.Length != media.SampledFrames ? "服装采样标签缺失"
            : media.OutfitFrames.Count != frames.Length ? "服装区域识别缺失"
            : samples.Length < (video ? Math.Min(2, frames.Length) : 1) ? "未检出足够服装区域"
            : !recognizable ? "未检出足够服装信息" : null;
        var embedding = Normalize(Enumerable.Range(0, 384).Select(index => samples.Length == 0 ? 0 : samples.Average(sample => sample.Embedding[index])).ToArray());
        return new(file, samples, values, colors, embedding, error);
    }

    private static double[] Normalize(double[] values)
    {
        var length = Math.Sqrt(values.Sum(value => value * value));
        return length <= 0 ? values : values.Select(value => value / length).ToArray();
    }
    private static string? Family(string tag)
    {
        if (tag.Contains("dress", StringComparison.Ordinal) || tag is "kimono" or "yukata" or "cheongsam" or "chinese_clothes" or "japanese_clothes") return "dress";
        if (tag.Contains("skirt", StringComparison.Ordinal)) return "skirt";
        if (tag.Contains("shirt", StringComparison.Ordinal) || tag is "blouse" or "camisole" or "tank_top" or "crop_top" or "sweater" or "cardigan" or "hoodie") return "top";
        if (tag is "bra" or "panties" or "underwear" or "lingerie") return "underwear";
        if (tag.Contains("uniform", StringComparison.Ordinal) || tag is "serafuku" or "sailor_collar") return "uniform";
        if (tag.Contains("swimsuit", StringComparison.Ordinal) || tag == "bikini") return "swimwear";
        if (tag is "bodysuit" or "leotard") return "bodysuit";
        if (tag is "maid" or "maid_apron") return "maid";
        if (tag is "pants" or "shorts" or "jeans") return "trousers";
        if (tag == "pajamas") return "sleepwear";
        return null;
    }
    private static double[] Palette(double[] colors)
    {
        var output = colors.Select(value => Math.Pow(value, .7)).ToArray();
        // Adjacent colors tolerate illumination changes; unrelated colors stay distinct.
        foreach (var (left, right) in new[] { (0, 9), (1, 9), (2, 6), (5, 10), (8, 10), (3, 7) })
        { output[left] += colors[right] * .25; output[right] += colors[left] * .25; }
        return Normalize(output);
    }
    private static bool PaletteConflict(double[] left, double[] right)
        => Enumerable.Range(0, Colors.Length).Any(index => left[index] >= .62 && right[index] < .12
            && Enumerable.Range(0, Colors.Length).Any(other => right[other] >= .62 && left[other] < .12
                && Dot(Palette(Enumerable.Range(0, Colors.Length).Select(i => i == index ? 1d : 0).ToArray()),
                    Palette(Enumerable.Range(0, Colors.Length).Select(i => i == other ? 1d : 0).ToArray())) < .4));
    private static bool TagColorConflict(double[] left, double[] right, Feature[] features)
    {
        var leftColors = new double[Colors.Length]; var rightColors = new double[Colors.Length];
        for (var index = 0; index < features.Length; index++)
            if (features[index] is { Garment: true, Color: { } color })
            {
                var slot = Array.IndexOf(Colors, color);
                leftColors[slot] = Math.Max(leftColors[slot], left[index]); rightColors[slot] = Math.Max(rightColors[slot], right[index]);
            }
        if (leftColors.Max() >= .65 && rightColors.Max() >= .65 && !leftColors.Zip(rightColors, (a, b) => Math.Min(a, b)).Any(value => value >= .3)) return true;
        for (var i = 0; i < features.Length; i++)
            if (features[i] is { Garment: true, ColorPart: { } part, Color: { } color } && left[i] >= .55)
                for (var j = 0; j < features.Length; j++)
                    if (features[j].Garment && features[j].ColorPart == part && features[j].Color != color && right[j] >= .55
                        && left[j] < .25 && right[i] < .25) return true;
        return false;
    }
    private static double Dot(double[] left, double[] right) => Math.Clamp(left.Zip(right, (a, b) => a * b).Sum(), 0, 1);
}
