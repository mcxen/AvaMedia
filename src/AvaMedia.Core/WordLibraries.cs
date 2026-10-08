using System.Text.Json;

namespace AvaMedia.Core;

public enum WordLibraryTarget { JoyTag, Semantic }
public sealed record WordCandidate(string Label, string Category, string Description, string[] Tags)
{
    public bool Supports(WordLibraryTarget target) => target == WordLibraryTarget.Semantic
        || Tags.Length > 0 && Tags.All(WordLibraryCatalog.JoyTags.Contains);
}
public sealed record WordLibrary(string Id, string Name, string Source, WordCandidate[] Entries, bool BuiltIn = false)
{
    public override string ToString() => $"{Name} · {Entries.Length}";
}
public sealed record SelectedWord(string LibraryId, string Label);

public static partial class WordLibraryCatalog
{
    public const int MaximumCandidates = 20000;
    public static readonly HashSet<string> JoyTags;
    public static readonly WordLibrary[] BuiltIns;
    private static readonly WordCandidate[] FeatureEntries;
    private static readonly WordCandidate[] NsfwEntries;
    private static readonly Dictionary<string, string> TagCategories;
    private static readonly Dictionary<string, string> TagLabels;

    static WordLibraryCatalog()
    {
        JoyTags = ReadText("joytag.txt").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        FeatureEntries = ReadFeatureWords();
        NsfwEntries = NsfwModeration.Candidates();
        if (NsfwEntries.Any(entry => !entry.Supports(WordLibraryTarget.JoyTag))) throw new InvalidDataException("NSFW 审核词库包含模型不支持的标签。");
        TagCategories = CreateTagCategories();
        TagLabels = CreateTagLabels();
        BuiltIns = new WordLibrary[]
        {
        new("person-features", "人物特征", "AvaMedia · 外观、配饰、动作、神态与体毛特征", FeatureEntries),
        new("nsfw-review", "NSFW 审核标签", "JoyTag / Danbooru · Apache-2.0 · AvaMedia 审核分组", NsfwEntries),
        new("nudenet-review", "NudeNet 审核分类", "notAI-tech/NudeNet · AGPL-3.0 · 18 类语义候选，非 JoyTag 检测输出", ReadNudeNetWords()),
        new("common", "常用分类", "AvaMedia · 中文名称与模型标签映射", Common()),
        new("ratings", "内容分级候选", "AvaMedia · 语义描述，需人工确认", [
            new("非NSFW", "内容分级", "An ordinary safe-for-work scene, fully clothed people, everyday objects or nature, suitable for a general audience.", []),
            new("NSFW", "内容分级", "An adult scene with explicit nudity, exposed genitals or sexual activity, not safe for work.", [])]),
        new("open-images", "Open Images · 通用物体", "Google LLC · CC BY 4.0 · Open Images V7",
            JsonSerializer.Deserialize<WordCandidate[]>(ReadText("open-images.json"))!),
        new("joytag", "JoyTag · 完整标签", "fpgaminer / fancyfeast · Apache-2.0 · 5813 tags",
            JoyTags.Order(StringComparer.Ordinal).Select(tag => new WordCandidate(RenameLabel(tag), TagCategory(tag), "A photo of " + tag.Replace('_', ' ') + ".", [tag])).ToArray())
        }.Select(library => library with { BuiltIn = true }).ToArray();
    }

    private static string RenameLabel(string tag)
    {
        try { BatchRename.ValidateRenameKeyword(tag); return tag; }
        catch (ArgumentException) { return "tag_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tag)))[..16].ToLowerInvariant(); }
    }

    private static string ReadText(string name)
    {
        using var stream = typeof(WordLibraryCatalog).Assembly.GetManifestResourceStream("AvaMedia.Core.AiLexicons." + name)
            ?? throw new InvalidDataException("内置词库缺失：" + name);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private static WordCandidate[] Common()
    {
        const string text = """
动物	猫	cat
动物	狗	dog
动物	鸟	bird
动物	鱼	fish
动物	马	horse
动物	兔子	rabbit
动物	蝴蝶	butterfly
动物	昆虫	insect
动物	狐狸	fox
动物	熊	bear
动物	鹿	deer
动物	牛	cow
动物	蛇	snake
植物	花	flower
植物	树	tree
植物	草	grass
植物	叶子	leaf
植物	樱花	cherry_blossoms
植物	玫瑰	rose
植物	向日葵	sunflower
景色	海边	beach
景色	海洋	ocean
景色	山	mountain
景色	森林	forest
景色	天空	sky
景色	云	cloud
景色	日落	sunset
景色	雪	snow
景色	雨	rain
景色	河流	river
景色	瀑布	waterfall
景色	城市	city
景色	夜景	night
配饰	帽子	hat
服饰	裙子	skirt
服饰	连衣裙	dress
服饰	衬衫	shirt
服饰	西装	suit
服饰	牛仔裤	jeans
服饰	泳装	swimsuit
服饰	鞋	shoes
服饰	围巾	scarf
活动	走路	walking
活动	跑步	running
活动	游泳	swimming
活动	跳舞	dancing
活动	做饭	cooking
活动	骑车	bicycle
物体	汽车	car
物体	自行车	bicycle
物体	手机	cellphone
物体	电脑	computer
物体	书	book
物体	食物	food
物体	建筑	building
物体	室内	indoors
物体	室外	outdoors
成人标签	生殖器	genitals
""";
        return FeatureEntries.Concat(NsfwEntries).Concat(text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var fields = line.Split('\t'); var tags = fields[2].Split('+');
            return new WordCandidate(fields[1], fields[0], "A photo showing " + string.Join(" and ", tags.Select(tag => tag.Replace('_', ' '))) + ".",
                tags.All(JoyTags.Contains) ? tags : []);
        })).DistinctBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static WordCandidate[] ParseText(string text)
    {
        var result = new List<WordCandidate>();
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('\t');
            if (fields.Length > 4) throw new ArgumentException("每行最多四列：类别、名称、语义描述、JoyTag 标签。");
            if (fields.Length == 1)
            {
                var pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
                var normalized = pair[0].Replace(' ', '_').ToLowerInvariant();
                result.Add(new(pair[0], "自定义", pair.Length == 2 ? pair[1] : pair[0], JoyTags.Contains(normalized) ? [normalized] : []));
            }
            else result.Add(new(fields[1].Trim(), fields[0].Trim(), fields.Length > 2 && fields[2].Length > 0 ? fields[2].Trim() : fields[1].Trim(),
                fields.Length > 3 ? fields[3].Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : []));
        }
        Validate(result); return result.ToArray();
    }
    public static void Validate(IReadOnlyList<WordCandidate> entries)
    {
        if (entries.Count is < 1 or > MaximumCandidates) throw new ArgumentException($"词库须包含 1–{MaximumCandidates} 个词。");
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry is null || entry.Label is null || entry.Description is null || entry.Category is null || entry.Tags is null)
                throw new ArgumentException("词库字段缺失。");
            BatchRename.ValidateRenameKeyword(entry.Label);
            if (!labels.Add(entry.Label)) throw new ArgumentException("词库名称重复：" + entry.Label);
            if (string.IsNullOrWhiteSpace(entry.Category) || string.IsNullOrWhiteSpace(entry.Description)
                || entry.Category.Length > 80 || entry.Description.Length > 512
                || entry.Tags.Length > 32 || entry.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || !JoyTags.Contains(tag))
                || new[] { entry.Label, entry.Category, entry.Description }.Any(value => value.Any(character => character is '\t' or '\r' or '\n')))
                throw new ArgumentException("词库描述、类别或 JoyTag 标签无效：" + entry.Label);
        }
    }
    public static string ToText(WordLibrary library) => string.Join(Environment.NewLine,
        library.Entries.Select(entry => $"{entry.Category}\t{entry.Label}\t{entry.Description}\t{string.Join('+', entry.Tags)}"));
}

/// <summary>Independent local settings. Each write reloads the other fields to preserve edits from another dialog.</summary>
public sealed class WordLibraryStore(string? path = null)
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path = Path.GetFullPath(path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia", "word-libraries.json"));
    public sealed class State
    {
        public List<WordLibrary> Libraries { get; set; } = [];
        public Dictionary<WordLibraryTarget, SelectedWord[]> Selections { get; set; } = [];
    }
    private State Read()
    {
        if (!File.Exists(_path)) return new();
        if (new FileInfo(_path).Length > 32 * 1024 * 1024) throw new InvalidDataException("词库文件过大。");
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path), Json) ?? throw new InvalidDataException("词库文件无效。");
        if (state.Libraries is null || state.Selections is null || state.Libraries.Count > 100) throw new InvalidDataException("词库文件无效。");
        foreach (var library in state.Libraries)
        {
            if (library is null || library.Entries is null || string.IsNullOrWhiteSpace(library.Id) || string.IsNullOrWhiteSpace(library.Name)
                || library.Name.Length > 80 || WordLibraryCatalog.BuiltIns.Any(b => b.Id == library.Id))
                throw new InvalidDataException("自定义词库无效。");
            WordLibraryCatalog.Validate(library.Entries);
        }
        if (state.Libraries.Select(library => library.Id).Distinct().Count() != state.Libraries.Count)
            throw new InvalidDataException("自定义词库标识重复。");
        return state;
    }
    public WordLibrary[] Libraries() { lock (Gate) return WordLibraryCatalog.BuiltIns.Concat(Read().Libraries).ToArray(); }
    public SelectedWord[] Selection(WordLibraryTarget target)
    { lock (Gate) return Read().Selections.GetValueOrDefault(target) ?? []; }
    public WordCandidate[] Resolve(WordLibraryTarget target)
    {
        var keys = Selection(target).ToHashSet();
        return Libraries().SelectMany(library => library.Entries.Where(entry => keys.Contains(new(library.Id, entry.Label))))
            .Where(entry => entry.Supports(target)).DistinctBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public void SaveSelection(WordLibraryTarget target, SelectedWord[] selection) => Update(state =>
    {
        if (selection.Length > WordLibraryCatalog.MaximumCandidates) throw new ArgumentException("选择的候选词过多。");
        state.Selections[target] = selection.Distinct().ToArray();
    });
    public void Save(WordLibrary library) => Update(state =>
    {
        if (library.BuiltIn || WordLibraryCatalog.BuiltIns.Any(item => item.Id == library.Id)) throw new ArgumentException("请复制内置词库后编辑。");
        if (string.IsNullOrWhiteSpace(library.Name) || library.Name.Length > 80) throw new ArgumentException("词库名称须为 1–80 字符。");
        WordLibraryCatalog.Validate(library.Entries);
        state.Libraries.RemoveAll(item => item.Id == library.Id); state.Libraries.Add(library);
        if (state.Libraries.Count > 100) throw new ArgumentException("最多保存 100 个自定义词库。");
    });
    public void Delete(string id) => Update(state =>
    {
        state.Libraries.RemoveAll(library => library.Id == id);
        foreach (var target in state.Selections.Keys.ToArray()) state.Selections[target] = state.Selections[target].Where(word => word.LibraryId != id).ToArray();
    });
    private void Update(Action<State> change)
    {
        lock (Gate)
        {
            var state = Read(); change(state);
            var content = JsonSerializer.Serialize(state, Json);
            if (System.Text.Encoding.UTF8.GetByteCount(content) > 32 * 1024 * 1024) throw new ArgumentException("保存的词库总量不能超过 32 MB。");
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, content); File.Move(temporary, _path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
