namespace AvaMedia.Core;

/// <summary>Positive clothing items and attributes from the installed JoyTag vocabulary.</summary>
internal static class OutfitVocabulary
{
    internal sealed record Part(string Name, string Family, bool Primary, bool Private);
    private static readonly Dictionary<string, Part> Parts = CreateParts();
    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "white", "black", "red", "blue", "green", "yellow", "pink", "purple", "brown", "grey", "gray", "orange", "aqua", "multicolored",
        "striped", "vertical-striped", "plaid", "polka_dot", "checkered", "floral", "print", "patterned", "two-tone", "argyle",
        "frilled", "pleated", "layered", "ribbed", "lace", "lace-trimmed", "ribbon-trimmed", "fur-trimmed", "denim", "leather", "sheer",
        "short", "long", "medium", "cropped", "high-waist", "highleg", "lowleg", "micro", "short-sleeved", "long-sleeved",
        "sleeveless", "strapless", "off-shoulder", "collared", "hooded", "halter", "backless", "side-tie", "front-tie", "string",
        "bow", "o-ring", "multi-strapped", "thong", "open", "unbuttoned", "tied", "wet", "torn", "loose", "tight", "baggy",
        "puffy", "oversized", "undersized", "track", "gym", "sports", "competition", "winter", "summer", "military", "sailor",
        "ankle", "knee", "high_heel", "thigh", "toeless", "seamed", "stirrup", "fingerless", "partially_fingerless"
    };
    private static readonly HashSet<string> Attributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "garter_straps", "garter_belt", "boots", "high_heels", "shoes", "gloves", "elbow_gloves", "necktie", "bowtie",
        "ribbon", "hair_ribbon", "hair_bow", "choker", "collar", "maid_headdress", "bunny_ears", "animal_ears",
        "detached_sleeves", "long_sleeves", "short_sleeves", "puffy_sleeves", "sleeveless", "off_shoulder",
        "bare_shoulders", "frills", "lace", "lace_trim", "ribbon_trim", "fur_trim", "see-through", "sheer_clothes",
        "clothing_cutout", "cross-laced_clothes", "lace-up", "halter_top", "halterneck", "strapless", "backless_outfit",
        "polka_dot", "plaid", "stripes", "floral_print", "checkered_clothes", "patterned_clothing",
        "sheer_legwear", "layered_clothing", "lace-trimmed_legwear", "ribbed_legwear", "seamed_legwear", "wet_clothes",
        "shiny_clothes", "leather", "denim", "fur", "sailor_collar", "ribbon-trimmed_clothes"
    };
    internal static Part? Resolve(string tag)
    {
        if (Parts.TryGetValue(tag, out var part)) return part;
        for (var separator = tag.IndexOf('_'); separator >= 0; separator = tag.IndexOf('_', separator + 1))
            if (Modifiers.Contains(tag[..separator]) && Resolve(tag[(separator + 1)..]) is { } modified)
                return modified;
        // Named uniforms are garments, while actions, negation and fictional machines are not.
        if ((tag.EndsWith("_school_uniform", StringComparison.Ordinal) || tag.EndsWith("_military_uniform", StringComparison.Ordinal))
            && !tag.StartsWith("no_", StringComparison.Ordinal)) return Parts["uniform"];
        return null;
    }
    internal static bool IsAttribute(string tag) => Attributes.Contains(tag);
    internal static string? Category(string tag) => Resolve(tag) is { } part ? part.Private ? "NSFW衣着提示" : "服饰"
        : IsAttribute(tag) ? "服饰" : null;
    internal static string? Absence(string? item, string? family) => item switch
    {
        "bra" => "no_bra", "panties" => "no_panties", "shirt" or "t-shirt" or "blouse" => "no_shirt",
        "pants" or "jeans" => "no_pants",
        _ => family switch { "legwear" => "no_legwear", "footwear" => "no_shoes", "handwear" => "no_gloves", "headwear" => "no_hat", _ => null }
    };

    private static Dictionary<string, Part> CreateParts()
    {
        var result = new Dictionary<string, Part>(StringComparer.Ordinal);
        void Add(string family, string words, bool primary = true, bool privateItem = false)
        {
            foreach (var word in words.Split(' ')) result.Add(word, new(word, family, primary, privateItem));
        }
        Add("top", "shirt t-shirt blouse sweater cardigan hoodie vest camisole tank_top crop_top dress_shirt collared_shirt halter_top turtleneck tube_top tunic bustier corset");
        Add("outerwear", "jacket coat blazer cape poncho cloak raincoat trench_coat fur_coat bolero");
        Add("dress", "dress sundress wedding_dress pinafore_dress sweater_dress sailor_dress pencil_dress kimono yukata cheongsam china_dress chinese_clothes japanese_clothes robe bathrobe hakama hanbok apron ao_dai dirndl");
        Add("skirt", "skirt miniskirt long_skirt pencil_skirt pleated_skirt suspender_skirt hakama_skirt bubble_skirt denim_skirt");
        Add("trousers", "pants shorts jeans leggings bike_shorts dolphin_shorts capri_pants overalls dungarees jumpsuit hakama_pants");
        Add("uniform", "uniform school_uniform serafuku gym_uniform military_uniform naval_uniform police_uniform sailor_senshi_uniform kindergarten_uniform employee_uniform baseball_uniform soccer_uniform tennis_uniform volleyball_uniform suit skirt_suit pant_suit business_suit maid maid_apron");
        Add("sleepwear", "pajamas nightgown");
        Add("bodysuit", "bodysuit leotard bodystocking catsuit");
        Add("swimwear", "swimsuit bikini one-piece_swimsuit school_swimsuit competition_swimsuit bikini_top bikini_bottom slingshot_swimsuit sports_bikini eyepatch_bikini");
        Add("underwear", "lingerie underwear bra panties sports_bra g-string thong lace_bra lace_panties crotchless_panties garter_belt pasties nipple_pasties fundoshi sarashi", privateItem: true);
        Add("legwear", "legwear pantyhose thighhighs stockings fishnets fishnet_legwear socks kneehighs thighband_pantyhose single_leg_pantyhose bobby_socks");
        Add("footwear", "shoes boots high_heels sandals sneakers loafers mary_janes pumps slippers uwabaki geta zouri");
        Add("handwear", "gloves elbow_gloves mittens");
        Add("headwear", "hat cap beret bonnet sun_hat straw_hat baseball_cap helmet hood");
        Add("accessory", "garters leg_garter garter_straps belt suspenders necktie bowtie ribbon choker collar scarf shawl stole", primary: false);
        foreach (var (alias, name) in new[] { ("dress_shirt", "shirt"), ("collared_shirt", "shirt"), ("sports_bra", "bra"), ("lace_bra", "bra"), ("lace_panties", "panties") })
            result[alias] = result[name];
        return result;
    }
}
