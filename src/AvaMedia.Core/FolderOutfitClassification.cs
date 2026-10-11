using System.Security.Cryptography;
using System.Text;

namespace AvaMedia.Core;

/// <summary>Group each shooting context by outfit, with a separate unclothed group and manual corrections.</summary>
public static partial class FolderOutfitClassification
{
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
    private sealed record Feature(string Tag, double Weight, bool Garment, string? ColorPart = null, string? Color = null)
    {
        public string? Family { get; } = OutfitVocabulary.Resolve(ColorPart ?? Tag)?.Family;
        public string? Item { get; } = OutfitVocabulary.Resolve(ColorPart ?? Tag)?.Name;
    }
    private static readonly Lazy<Feature[]> Features = new(() => WordLibraryCatalog.JoyTags.Order(StringComparer.Ordinal).Select(Describe).OfType<Feature>().ToArray());
    private sealed record Sample(double Seconds, double[] Values, OutfitAppearanceFrame Appearance, bool HasClothing, bool Unclothed,
        bool UseAppearance, double[] TagColors, double Nudity)
    {
        public double[] Colors { get; } = TagColors.Any(value => value != 0) ? TagColors : UseAppearance ? Appearance.Colors : new double[12];
        public double[] Embedding { get; } = UseAppearance ? Appearance.Embedding : new double[384];
        public double[] BackgroundPalette { get; } = Palette(Appearance.BackgroundColors);
    }
    private sealed record Profile(FolderClassifiedFile File, Sample[] Samples, Sample[] ContextSamples, Sample? Context,
        double[] Values, double[] Colors, double[] Embedding, bool Unclothed, DateTime? CaptureTime, string? Error)
    {
        public Sample[] Views { get; init; } = [];
    }

    private static Feature? Describe(string tag)
    {
        foreach (var color in Colors.Concat(["aqua", "gray"]))
        {
            if (!tag.StartsWith(color + "_", StringComparison.Ordinal)) continue;
            var part = tag[(color.Length + 1)..];
            if (OutfitVocabulary.Resolve(part) is { } item) return new(tag, item.Primary && item.Name != "legwear" ? 3 : .9, item.Primary && item.Name != "legwear", part,
                color == "aqua" ? "blue" : color == "gray" ? "grey" : color);
            if (OutfitVocabulary.IsAttribute(part)) return new(tag, .9, false, part, color);
        }
        if (OutfitVocabulary.Resolve(tag) is { } garment)
            return new(tag, garment.Primary && garment.Name != "legwear" ? tag is "shirt" or "dress" or "skirt" or "underwear" or "uniform" ? 1.2 : 2 : .65, garment.Primary && garment.Name != "legwear");
        return OutfitVocabulary.IsAttribute(tag) ? new(tag, .65, false) : null;
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
        return WithShootTitles(output);
    }

    public static FolderClassificationCategory[] Categories(FolderClassificationRule rule, IEnumerable<FolderClassifiedFile> files)
        => rule.ByOutfit ? files.SelectMany(file => file.Decisions).Where(decision => decision.RuleId == rule.Id && decision.CategoryId is not null)
            .GroupBy(decision => decision.CategoryId!).Select(group => new FolderClassificationCategory(group.Key, group.First().CategoryName, group.First().Evidence)
                { OutfitShootName = group.First().OutfitShootName })
            .OrderBy(category => category.DisplayName, StringComparer.Ordinal).ToArray() : rule.Categories;

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
        var contextThreshold = Math.Clamp(threshold + .14, .76, .92);
        var profiles = files.Select(file => Build(file, features, includeNsfw)).ToArray();
        var weights = features.Select((feature, index) => feature.Weight * (1 + Math.Log((profiles.Length + 1d)
            / (1 + profiles.Count(profile => profile.Values[index] >= .4))))).ToArray();
        var detailVectors = new Dictionary<double[], double[]>(); var broadVectors = new Dictionary<double[], double[]>();
        var itemVectors = new Dictionary<double[], double[]>(); var visibleParts = new Dictionary<double[], HashSet<string>>();
        var palettes = new Dictionary<double[], double[]>();
        double[] Vector(double[] values)
        {
            if (!detailVectors.TryGetValue(values, out var vector)) detailVectors[values] = vector =
                Normalize(values.Select((value, index) => Math.Pow(Math.Max(0, value - .15), 1.4) * Math.Sqrt(weights[index])).ToArray());
            return vector;
        }
        double[] ColorVector(double[] colors)
        {
            if (!palettes.TryGetValue(colors, out var palette)) palettes[colors] = palette = Palette(colors);
            return palette;
        }
        var families = features.Where(feature => feature.Garment).Select(feature => feature.Family).OfType<string>().Distinct().Order().ToArray();
        var items = features.Where(feature => feature.Garment).Select(feature => feature.Item).OfType<string>().Distinct().Order().ToArray();
        HashSet<string> VisibleParts(double[] values)
        {
            if (!visibleParts.TryGetValue(values, out var parts)) visibleParts[values] = parts = features
                .Where((feature, index) => feature.Garment && values[index] >= .55).Select(feature => feature.Item!).ToHashSet();
            return parts;
        }
        double[] ItemVector(double[] values)
        {
            if (itemVectors.TryGetValue(values, out var cached)) return cached;
            var vector = new double[items.Length];
            for (var index = 0; index < features.Length; index++)
                if (features[index] is { Garment: true, Item: { } item })
                { var target = Array.IndexOf(items, item); vector[target] = Math.Max(vector[target], values[index]); }
            return itemVectors[values] = Normalize(vector.Select(value => Math.Pow(Math.Max(0, value - .15), 1.4)).ToArray());
        }
        double[] BroadVector(double[] values)
        {
            if (broadVectors.TryGetValue(values, out var cached)) return cached;
            var broad = new double[families.Length];
            for (var index = 0; index < features.Length; index++)
                if (features[index] is { Garment: true, Family: { } family })
                {
                    var target = Array.IndexOf(families, family);
                    broad[target] = Math.Max(broad[target], values[index]);
                }
            return broadVectors[values] = Normalize(broad.Select(value => Math.Pow(Math.Max(0, value - .15), 1.4)).ToArray());
        }
        double Clothing(double[] lv, double[] lc, double[] le, double[] rv, double[] rc, double[] re)
        {
            if (TagColorConflict(lv, rv, features)
                || VisibleParts(lv).Count > 0 && VisibleParts(lv).SetEquals(VisibleParts(rv)) && PaletteConflict(lc, rc)) return 0;
            var score = .2 * Dot(Vector(lv), Vector(rv)) + .2 * Dot(ItemVector(lv), ItemVector(rv)) + .15 * Dot(BroadVector(lv), BroadVector(rv));
            var weight = .55;
            if (le.Any(value => value != 0) && re.Any(value => value != 0))
            { score += .25 * Dot(le, re); weight += .25; }
            if (lc.Any(value => value != 0) && rc.Any(value => value != 0))
            { score += .2 * Dot(ColorVector(lc), ColorVector(rc)); weight += .2; }
            return score / weight;
        }
        double Match(Sample left, Sample right) => Clothing(left.Values, left.Colors, left.Embedding, right.Values, right.Colors, right.Embedding);
        double Outfit(Profile left, Profile right) => Clothing(left.Values, left.Colors, left.Embedding, right.Values, right.Colors, right.Embedding);
        var decisions = new Dictionary<string, FolderClassificationDecision>(BatchRename.PathComparer);
        profiles = profiles.Select(profile =>
        {
            if (profile.Samples.Length == 0) return profile;
            // Use a real dominant outfit, rather than averaging a costume change into a fictitious outfit.
            var representative = profile.Samples.OrderByDescending(sample => profile.Samples.Count(other => Match(sample, other) >= threshold - .1))
                .ThenByDescending(sample => sample.Values.Select((value, index) => features[index].Garment ? value : 0).DefaultIfEmpty().Max()).First();
            var consistency = profile.Samples.Count(sample => Match(sample, representative) >= threshold - .1) / (double)profile.Samples.Length;
            return profile with { Values = representative.Values, Colors = representative.Colors, Embedding = representative.Embedding,
                Error = profile.Error ?? (consistency < .8 ? "采样服装有变化或服装信息不足" : null) };
        }).ToArray();
        foreach (var profile in profiles.Where(profile => profile.Error is not null))
            decisions[profile.File.Media.Path] = Review(profile.Error!);

        var contexts = new Dictionary<(Profile, Profile), double>();
        double Context(Profile left, Profile right)
        {
            if (!contexts.TryGetValue((left, right), out var score))
            { score = Scene(left.Views, right.Views); contexts[(left, right)] = score; contexts[(right, left)] = score; }
            return score;
        }
        double Shoot(Profile left, Profile right)
        {
            var gap = CaptureGap(left, right);
            if (gap > TimeSpan.FromHours(6)) return 0;
            var context = Context(left, right);
            // Close camera timestamps support a changed viewpoint, but cannot establish a shoot on their own.
            if (gap <= TimeSpan.FromMinutes(90) && (context >= .55
                || left.Samples.Length > 0 && right.Samples.Length > 0 && Outfit(left, right) >= threshold))
                return Math.Max(contextThreshold, context);
            if (left.Samples.Length > 0 && right.Samples.Length > 0)
            {
                var clothing = Outfit(left, right);
                // A close crop can change the background layout. Strong outfit evidence still needs
                // a compatible visible scene, unless the scene is unavailable altogether.
                if (clothing >= Math.Min(.92, threshold + .12)
                    && (left.Context is null || right.Context is null || context >= threshold - .1))
                    return Math.Max(contextThreshold, context);
            }
            return context;
        }
        var shoots = Cluster(profiles.Where(profile => profile.Error is null).OrderBy(profile => profile.CaptureTime)
            .ThenBy(profile => profile.File.Media.Path, BatchRename.PathComparer), Shoot, contextThreshold, ct);
        foreach (var (shoot, shootIndex) in shoots.Select((shoot, index) => (shoot, index)))
        {
            ct.ThrowIfCancellationRequested();
            var shootName = $"拍摄组{shootIndex + 1:00}";
            var groups = Cluster(shoot.Where(profile => profile.Samples.Length > 0)
                .OrderByDescending(profile => profile.Values.Select((value, index) => features[index].Garment ? value : 0).DefaultIfEmpty().Max())
                .ThenBy(profile => profile.File.Media.Path, BatchRename.PathComparer), Outfit, threshold, ct);
            var unclothed = shoot.Where(profile => profile.Unclothed).ToList();
            if (unclothed.Count > 0) groups.Add(unclothed);
            var metadata = groups.OrderByDescending(group => group.Count)
                .ThenBy(group => group.Min(profile => profile.File.Media.Path), BatchRename.PathComparer).Select((group, number) =>
            {
                var nude = group[0].Unclothed;
                var center = Enumerable.Range(0, features.Length).Select(index => group.Average(profile => profile.Values[index])).ToArray();
                var colors = Enumerable.Range(0, Colors.Length).Select(index => group.Average(profile => profile.Colors[index])).ToArray();
                var embedding = Normalize(Enumerable.Range(0, 384).Select(index => group.Average(profile => profile.Embedding[index])).ToArray());
                var views = ContextViews(group.SelectMany(profile => profile.Views), 8);
                var context = views.FirstOrDefault();
                var descriptions = features.Select((feature, index) => (Feature: feature with { Tag = feature.ColorPart ?? feature.Tag, Color = null, ColorPart = null }, Score: center[index]))
                    .GroupBy(item => item.Feature.Tag).Select(items => items.MaxBy(item => item.Score))
                    .Where(item => item.Score >= (item.Feature.Garment ? .45 : .5))
                    .OrderByDescending(item => item.Score * (item.Feature.Garment ? 1.1 : .6))
                    .DistinctBy(item => item.Feature.Item ?? item.Feature.Tag).ToArray();
                var specific = descriptions.Any(item => item.Feature.Garment && item.Feature.Tag is not ("underwear" or "lingerie"));
                var labels = descriptions.Where(item => !specific || item.Feature.Tag is not ("underwear" or "lingerie"))
                    .Where(item => item.Feature.Garment || item.Feature.Family is null)
                    .Select(item => Label(item.Feature)).Distinct().Take(3).ToArray();
                var dominant = Array.IndexOf(colors, colors.Max());
                var description = string.Join(" · ", colors.Any(value => value != 0) ? labels.Prepend(ColorLabels[dominant]) : labels);
                var name = $"{number + 1:00} " + (nude ? "无服装" : description.Length > 0 ? description : "服装");
                if (name.Length > 60) name = name[..60].TrimEnd();
                var representative = group.OrderBy(profile => profile.File.Media.Path, BatchRename.PathComparer).First();
                var id = "outfit-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(representative.File.Media.Path)))[..16].ToLowerInvariant();
                return (Group: group, Unclothed: nude, Values: center, Colors: colors, Embedding: embedding, Context: context, Views: views, Id: id, Name: name);
            }).ToArray();
            foreach (var profile in shoot)
            {
                var own = Array.FindIndex(metadata, group => group.Group.Contains(profile));
                var contextual = own < 0;
                if (contextual)
                {
                    var hasClothing = profile.ContextSamples.Any(sample => sample.HasClothing);
                    var hasNudity = profile.ContextSamples.Any(sample => sample.Unclothed);
                    double Association(int index)
                    {
                        var group = metadata[index];
                        if (group.Unclothed ? !hasNudity || hasClothing : hasNudity) return 0;
                        var observed = profile.ContextSamples.Where(sample => sample.HasClothing).ToArray();
                        if (!group.Unclothed && observed.Length > 0 && observed.Max(sample => group.Group.Max(member =>
                            Clothing(sample.Values, sample.Colors, sample.Embedding, member.Values, member.Colors, member.Embedding))) < threshold - .1) return 0;
                        return Scene(profile.Views, group.Views);
                    }
                    // A missing outfit is not a changed outfit. Inherit only a scene-supported, compatible state.
                    var candidates = metadata.Select((group, index) => (Index: index, Score: Association(index)))
                        .OrderByDescending(candidate => candidate.Score).Take(2).ToArray();
                    if (candidates.Length == 0 || candidates[0].Score < contextThreshold
                        || candidates.Length > 1 && candidates[0].Score - candidates[1].Score < FolderClassificationRule.DefaultMargin)
                    { decisions[profile.File.Media.Path] = Review("服装不可见，拍摄背景不足以确定归属") with { OutfitShootName = shootName }; continue; }
                    own = candidates[0].Index;
                }
                var winner = metadata[own];
                var samples = winner.Unclothed ? profile.ContextSamples.Where(sample => sample.Unclothed).ToArray()
                    : contextual ? profile.ContextSamples : profile.Samples;
                double Score(Sample sample, int index)
                {
                    var candidate = metadata[index];
                    if (winner.Unclothed) return candidate.Unclothed && sample.Unclothed ? sample.Nudity : 0;
                    if (contextual) return candidate.Unclothed ? 0 : Scene(sample, candidate.Views);
                    return candidate.Unclothed ? 0 : candidate.Group.Max(member => Clothing(sample.Values, sample.Colors, sample.Embedding,
                        member.Values, member.Colors, member.Embedding));
                }
                var minimum = contextual || winner.Unclothed ? contextThreshold - .1 : threshold - .1;
                var frames = samples.Select(sample => new FolderFrameClassification(sample.Seconds, winner.Id, Score(sample, own), null)).ToArray();
                var agreement = winner.Unclothed ? samples.Length > 0 ? 1d : 0
                    : frames.Length == 0 ? 0 : frames.Count(frame => frame.Similarity >= minimum) / (double)frames.Length;
                if (contextual && !winner.Unclothed)
                {
                    // Ignore background-free crops, but keep actual competing-group votes as disagreements.
                    var votes = samples.Select(sample => metadata.Select((group, index) => (Index: index, Score: Score(sample, index)))
                        .OrderByDescending(candidate => candidate.Score).Take(2).ToArray())
                        .Where(candidates => candidates[0].Score >= minimum
                            && (candidates.Length == 1 || candidates[0].Score - candidates[1].Score >= FolderClassificationRule.DefaultMargin)).ToArray();
                    var required = VideoFormats.IsVideo(profile.File.Media.Path) ? 2 : 1;
                    agreement = votes.Count(vote => vote[0].Index == own) < required ? 0
                        : votes.Count(vote => vote[0].Index == own) / (double)votes.Length;
                }
                var scores = metadata.Select((candidate, index) => new FolderCategoryScore(candidate.Id, shootName + " · " + candidate.Name,
                    samples.Length == 0 ? 0 : samples.Average(sample => Score(sample, index)), samples.Count(sample => Score(sample, index) >= minimum)))
                    .OrderByDescending(score => score.Similarity).Take(5).ToArray();
                var evidence = agreement < .8 ? "采样画面与分组有分歧" : contextual ? "拍摄背景关联到服装组"
                    : winner.Unclothed ? winner.Context is null ? "无服装，缺少拍摄背景，单独成组" : "无服装样本按拍摄背景分组"
                    : "同次拍摄内按服装标签、颜色和图像特征分组";
                if (profile.CaptureTime is { } time) evidence += " · 拍摄时间 " + time.ToString("yyyy-MM-dd HH:mm:ss");
                decisions[profile.File.Media.Path] = new(rule.Id, rule.Name, agreement >= .8 ? winner.Id : null,
                    agreement >= .8 ? winner.Name : "待确认", scores, frames, frames.MaxBy(frame => frame.Similarity)?.Seconds, agreement, evidence)
                    { OutfitShootName = shootName };
            }
        }
        return decisions.ToDictionary(item => item.Key, item => item.Value with { OutfitIncludesNsfw = includeNsfw }, BatchRename.PathComparer);
        FolderClassificationDecision Review(string evidence) => new(rule.Id, rule.Name, null, "待确认", [], [], null, 0, evidence);
    }

    private static Profile Build(FolderClassifiedFile file, Feature[] features, bool includeNsfw)
    {
        var media = file.Media;
        var indices = media.Scores.Select((score, index) => (score.Tag, index)).ToDictionary(item => item.Tag, item => item.index, StringComparer.OrdinalIgnoreCase);
        double[] Read(IReadOnlyList<double> values) => features.Select(feature =>
        {
            var presence = Tag(values, feature.Tag);
            var absence = OutfitVocabulary.Absence(feature.Item, feature.Family) is { } tag ? Tag(values, tag) : 0;
            return absence >= .65 && absence >= presence + .15 ? 0 : presence;
        }).ToArray();
        double Tag(IReadOnlyList<double> values, string tag) => indices.TryGetValue(tag, out var index) && index < values.Count ? values[index] : 0;
        var video = VideoFormats.IsVideo(media.Path);
        var frames = video ? media.Frames.Where(frame => frame.Values.Length == media.Scores.Count)
            .Select(frame => (frame.Seconds, Values: frame.Values.Select(value => (double)value).ToArray())).ToArray()
            : [(Seconds: 0d, Values: media.Scores.Select(score => score.Score).ToArray())];
        var all = frames.Select(frame => (frame, Appearance: media.OutfitFrames.FirstOrDefault(appearance => Math.Abs(appearance.Seconds - frame.Seconds) < .001)))
            .Where(item => item.Appearance is { Encoder: OutfitAppearanceService.Encoder, Colors.Length: 12, Embedding.Length: 384,
                BackgroundColors.Length: 12, BackgroundEmbedding.Length: 384, BackgroundRegions.Length: 1536 })
            .Select(item =>
            {
                var appearance = item.Appearance!; var values = Read(item.frame.Values);
                double Garment(bool legwear) => features.Select((feature, index) => feature.Garment
                    && (feature.Family == "legwear") == legwear ? values[index] : 0).DefaultIfEmpty().Max();
                var legwear = Garment(true); var primary = Garment(false);
                var garment = Math.Max(legwear, primary);
                var tagColors = ReadColors(values, features);
                var segmented = appearance.Pixels >= 128 && appearance.Confidence >= .6 && appearance.Embedding.Any(value => value != 0);
                var transparent = features.Select((feature, index) => feature.Tag is "see-through" or "sheer_clothes" or "sheer_legwear" ? values[index] : 0)
                    .DefaultIfEmpty().Max() >= .45;
                var useAppearance = segmented && !(legwear >= .55 && primary < .55) && !transparent
                    && !PaletteConflict(appearance.Colors, tagColors);
                // A strong item tag also supports garments too small, transparent or occluded for the coarse mask.
                var clothing = segmented && garment >= .35 || garment >= .6 || legwear >= .55 && primary < .55;
                var nudity = Math.Max(Tag(item.frame.Values, "nude"), Tag(item.frame.Values, "completely_nude"));
                var nude = includeNsfw && !clothing && appearance.PersonPixels >= 512 && appearance.SkinPixels >= 1024
                    && appearance.SkinPixels >= appearance.PersonPixels * .5 && appearance.Pixels < appearance.PersonPixels * .025
                    && garment < .35 && nudity >= .5;
                return new Sample(item.frame.Seconds, values, appearance, clothing, nude, useAppearance, tagColors, nudity);
            }).ToArray();
        var minimum = video ? Math.Min(2, frames.Length) : 1;
        var clothingSamples = all.Where(sample => sample.HasClothing).ToArray();
        var samples = clothingSamples.Length >= minimum ? clothingSamples : [];
        var contextSamples = all.Where(sample => sample.Appearance.BackgroundEmbedding.Any(value => value != 0)).ToArray();
        var context = contextSamples.OrderByDescending(sample => contextSamples.Sum(other => Scene(sample, other))).FirstOrDefault();
        // Uninformative crops do not contradict visible unclothed frames; clothing frames do.
        var unclothed = clothingSamples.Length == 0 && all.Count(sample => sample.Unclothed) >= minimum;
        var values = Enumerable.Range(0, features.Length).Select(index => samples.Length == 0 ? 0 : samples.Average(sample => sample.Values[index])).ToArray();
        var colors = Enumerable.Range(0, Colors.Length).Select(index => samples.Length == 0 ? 0 : samples.Average(sample => sample.Colors[index])).ToArray();
        var embedding = Normalize(Enumerable.Range(0, 384).Select(index => samples.Length == 0 ? 0 : samples.Average(sample => sample.Embedding[index])).ToArray());
        var error = frames.Length == 0 || video && frames.Length != media.SampledFrames ? "服装采样标签缺失"
            : all.Length != frames.Length ? "服装区域识别缺失" : null;
        return new(file, samples, all, context, values, colors, embedding, unclothed, all.FirstOrDefault()?.Appearance.CaptureTime, error)
            { Views = ContextViews(contextSamples, 4) };
    }

    private static double[] Normalize(double[] values)
    {
        var length = Math.Sqrt(values.Sum(value => value * value));
        return length <= 0 ? values : values.Select(value => value / length).ToArray();
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
        for (var i = 0; i < features.Length; i++)
            if (features[i] is { Garment: true, Item: { } part, Color: { } color } && left[i] >= .55)
                for (var j = 0; j < features.Length; j++)
                    if (features[j].Garment && features[j].Item == part && features[j].Color is { } other && other != color && right[j] >= .55
                        && left[j] < .25 && right[i] < .25) return true;
        return false;
    }
    private static double[] ReadColors(double[] values, Feature[] features)
    {
        var colors = new double[Colors.Length];
        for (var index = 0; index < features.Length; index++)
            if (features[index] is { Garment: true, Color: { } color } && values[index] >= .55)
            { var slot = Array.IndexOf(Colors, color); colors[slot] = Math.Max(colors[slot], values[index]); }
        var total = colors.Sum();
        return total == 0 ? colors : colors.Select(value => value / total).ToArray();
    }
    private static double Dot(double[] left, double[] right) => Math.Clamp(left.Zip(right, (a, b) => a * b).Sum(), 0, 1);
}
