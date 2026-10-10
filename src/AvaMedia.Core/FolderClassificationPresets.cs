namespace AvaMedia.Core;

/// <summary>Ready-made groups use supported model labels or source metadata.</summary>
public static class FolderClassificationPresets
{
    public static readonly string[] DurationCategoryIds = ["image", "under-one", "one-five", "five-fifteen", "fifteen-thirty", "over-thirty"];
    private static FolderClassificationCategory C(string id, string name, params string[] alternatives)
        => new(id, name, name) { Tags = alternatives.Select(value => value.Split('+')).ToArray() };
    private static FolderClassificationRule T(string id, string name, bool nsfw, params FolderClassificationCategory[] categories)
        => new(id, name, categories.Append(C("other", "其他／未检出")).ToArray())
        { IsNsfw = nsfw, UsePeakEvidence = nsfw, FallbackCategoryId = "other" };

    public static IReadOnlyList<FolderClassificationRule> Additional { get; } = [
        new("similar-outfits", "相似服装", []) { ByOutfit = true },
        new("video-duration", "视频长短", [
            C("image", "图片"), C("under-one", "不到1分钟"), C("one-five", "1–5分钟"),
            C("five-fifteen", "5–15分钟"), C("fifteen-thirty", "15–30分钟"), C("over-thirty", "30分钟及以上")]) { ByDuration = true },
        T("body-pose", "人物姿势", false, C("standing", "站着", "standing"), C("sitting", "坐着", "sitting"),
            C("lying", "躺着", "lying"), C("kneeling", "跪着", "kneeling"), C("squatting", "蹲着", "squatting")),
        T("lying-pose", "怎么躺着", false, C("back", "平躺", "lying+on_back"), C("side", "侧躺", "lying+on_side"), C("stomach", "趴着", "lying+on_stomach")),
        T("people-count", "几个人", false, C("solo", "单人", "solo"), C("multiple", "多人", "multiple_girls", "multiple_boys", "1girl+1boy")),
        T("framing", "拍到哪里", false, C("full", "全身", "full_body") with { SupersededBy = ["close"] }, C("upper", "上半身", "upper_body") with { SupersededBy = ["close"] }, C("close", "特写", "close-up")),
        T("viewpoint", "拍摄角度", false, C("behind", "背面", "from_behind"), C("side", "侧面", "from_side", "profile"), C("above", "从上往下", "from_above"), C("below", "从下往上", "from_below")),
        T("everyday-clothes", "日常穿着", false, C("dress", "连衣裙", "dress"), C("skirt", "裙子", "skirt"), C("shirt", "T恤／衬衫", "t-shirt", "shirt"),
            C("pants", "长裤", "pants", "jeans"), C("shorts", "短裤", "shorts"), C("suit", "西装", "suit"), C("sleep", "睡衣", "pajamas")),
        T("contact", "人物互动", false, C("hug", "拥抱", "hug"), C("kiss", "接吻", "kiss"), C("hands", "牵手", "holding_hands")),
        T("adult-position", "体位", true, C("front", "正面躺着（传教士）", "missionary"), C("behind", "后入／狗爬式", "sex_from_behind", "doggystyle"),
            C("top", "骑乘（女上位）", "cowgirl_position"), C("reverse", "反向骑乘", "reverse_cowgirl_position"), C("prone", "趴着后入", "prone_bone"),
            C("standing", "站立式", "standing_sex"), C("sixtynine", "69式", "69"), C("press", "屈腿压身位", "mating_press")),
        T("adult-activity", "亲密行为", true, C("sex", "性交", "vaginal", "anal"), C("oral", "口交", "oral", "fellatio", "cunnilingus"),
            C("solo", "自慰", "masturbation", "female_masturbation", "male_masturbation"), C("hands", "手部刺激", "fingering", "handjob"),
            C("chest", "胸部接触", "paizuri", "breast_sucking")),
        T("adult-clothing", "私密穿着", true, C("nude", "全裸", "completely_nude", "nude"), C("upper", "露上身", "topless") with { SupersededBy = ["nude"] }, C("lower", "露下身", "bottomless") with { SupersededBy = ["nude"] },
            C("lingerie", "情趣内衣", "lingerie"), C("underwear", "内衣／内裤", "underwear_only", "underwear", "panties", "bra") with { SupersededBy = ["nude", "upper", "lower", "lingerie", "sheer"] },
            C("sheer", "透视装", "see-through", "sheer_clothes")),
        T("adult-exposure", "裸露部位", true, C("chest", "胸部露出", "breasts_outside", "one_breast_out", "topless"),
            C("lower", "下面露出", "pussy", "penis", "testicles"), C("anus", "肛门露出", "anus")),
        T("adult-hair", "私密体毛", true, C("visible", "阴毛可见", "pubic_hair", "female_pubic_hair", "male_pubic_hair"), C("peek", "露出一点阴毛", "pubic_hair_peek")),
        T("adult-censor", "打码情况", true, C("mosaic", "马赛克", "mosaic_censoring"), C("bar", "遮挡条", "bar_censor"),
            C("censored", "有打码", "censored") with { SupersededBy = ["mosaic", "bar"] }, C("clear", "模型标记未打码", "uncensored")),
        T("adult-props", "私密道具", true, C("vibrator", "震动棒／跳蛋", "vibrator", "egg_vibrator"), C("dildo", "假阳具", "dildo"),
            C("bondage", "绳缚／捆绑", "bondage", "shibari", "bound"), C("gag", "口枷／口球", "gag", "ball_gag"), C("condom", "避孕套", "condom")),
        T("adult-participants", "亲密画面人数", true, C("solo", "单人自慰", "masturbation+solo"), C("two", "双人亲密", "sex+1girl+1boy"),
            C("three", "三人", "threesome"), C("group", "多人", "group_sex", "orgy"))
    ];

    internal static FolderClassificationDecision DecideDuration(MediaTagResult media, FolderClassificationRule rule)
    {
        if (VideoFormats.IsVideo(media.Path) && (!double.IsFinite(media.DurationSeconds) || media.DurationSeconds <= 0))
            return new(rule.Id, rule.Name, null, "待确认", [], [], null, 0, "视频时长缺失");
        var seconds = media.DurationSeconds;
        var id = !VideoFormats.IsVideo(media.Path) ? "image" : seconds < 60 ? "under-one" : seconds < 300 ? "one-five"
            : seconds < 900 ? "five-fifteen" : seconds < 1800 ? "fifteen-thirty" : "over-thirty";
        var category = rule.Categories.Single(category => category.Id == id);
        return new(rule.Id, rule.Name, id, category.Name, [], [], null, 1, "源文件时长");
    }
}
