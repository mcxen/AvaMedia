using System.Text;
using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>
/// Minimal reader for the EmbeddingGemma 2 Hugging Face tokenizer.json: added-token split, " " → "▁" normalization,
/// byte-fallback BPE with ranked merges, and the &lt;bos&gt; … &lt;eos&gt; template. The pre-tokenizer splits on " ",
/// which no longer occurs after normalization, so each segment between added tokens is one BPE word.
/// </summary>
internal sealed class GemmaTokenizer
{
    public const int PadId = 0, EosId = 1, BosId = 2;
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<long, (int Rank, int Id)> _merges;
    private readonly (string Content, int Id)[] _added;
    private readonly int[] _bytes = new int[256];
    private readonly int _unk;

    private GemmaTokenizer(Dictionary<string, int> vocab, Dictionary<long, (int, int)> merges, (string, int)[] added, int unk)
    {
        _vocab = vocab; _merges = merges; _added = added.OrderByDescending(token => token.Item1.Length).ToArray(); _unk = unk;
        for (var value = 0; value < 256; value++) _bytes[value] = vocab.GetValueOrDefault($"<0x{value:X2}>", unk);
    }

    public int TokenId(string token) => _added.FirstOrDefault(added => added.Content == token) is { Content: not null } match ? match.Id
        : _vocab.TryGetValue(token, out var id) ? id : throw new InvalidDataException("分词器缺少标记：" + token);

    public static GemmaTokenizer Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 256 });
        var root = json.RootElement;
        var model = root.GetProperty("model");
        if (model.GetProperty("type").GetString() != "BPE" || !model.GetProperty("byte_fallback").GetBoolean())
            throw new InvalidDataException("不支持的分词器格式。");
        var normalizer = root.GetProperty("normalizer");
        if (normalizer.GetProperty("type").GetString() != "Replace" || normalizer.GetProperty("content").GetString() != "▁")
            throw new InvalidDataException("不支持的分词器规范化方式。");
        var vocab = new Dictionary<string, int>(300_000, StringComparer.Ordinal);
        foreach (var entry in model.GetProperty("vocab").EnumerateObject()) vocab[entry.Name] = entry.Value.GetInt32();
        var merges = new Dictionary<long, (int, int)>(530_000);
        var rank = 0;
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            string left, right;
            if (merge.ValueKind == JsonValueKind.Array) { left = merge[0].GetString()!; right = merge[1].GetString()!; }
            else { var text = merge.GetString()!; var split = text.IndexOf(' '); left = text[..split]; right = text[(split + 1)..]; }
            if (vocab.TryGetValue(left, out var a) && vocab.TryGetValue(right, out var b) && vocab.TryGetValue(left + right, out var merged))
                merges.TryAdd(Key(a, b), (rank, merged));
            rank++;
        }
        var added = root.GetProperty("added_tokens").EnumerateArray()
            .Select(token => (token.GetProperty("content").GetString()!, token.GetProperty("id").GetInt32())).ToArray();
        return new(vocab, merges, added, vocab.GetValueOrDefault(model.GetProperty("unk_token").GetString() ?? "<unk>", 3));
    }

    private static long Key(int left, int right) => ((long)left << 32) | (uint)right;

    /// <summary>Token ids including &lt;bos&gt; and &lt;eos&gt;, like tokenizer(text) with add_special_tokens.</summary>
    public List<int> Encode(string text)
    {
        var ids = new List<int> { BosId };
        var start = 0;
        for (var index = 0; index < text.Length;)
        {
            var special = _added.FirstOrDefault(token => string.CompareOrdinal(text, index, token.Content, 0, token.Content.Length) == 0);
            if (special.Content is null) { index++; continue; }
            EncodeWord(text[start..index], ids); ids.Add(special.Id);
            index += special.Content.Length; start = index;
        }
        EncodeWord(text[start..], ids);
        ids.Add(EosId);
        return ids;
    }

    private void EncodeWord(string segment, List<int> output)
    {
        if (segment.Length == 0) return;
        var normalized = segment.Replace(' ', '▁');
        var symbols = new List<int>(normalized.Length);
        var runes = normalized.EnumerateRunes();
        Span<byte> buffer = stackalloc byte[4];
        foreach (var rune in runes)
        {
            if (_vocab.TryGetValue(rune.ToString(), out var id)) { symbols.Add(id); continue; }
            var count = rune.EncodeToUtf8(buffer);
            for (var index = 0; index < count; index++) symbols.Add(_bytes[buffer[index]]);
        }
        // Repeatedly apply the lowest-ranked merge, leftmost first, as tokenizers' BPE word merge does.
        while (symbols.Count > 1)
        {
            var best = -1; var bestRank = int.MaxValue; var bestId = 0;
            for (var index = 0; index < symbols.Count - 1; index++)
                if (_merges.TryGetValue(Key(symbols[index], symbols[index + 1]), out var merge) && merge.Rank < bestRank)
                { best = index; bestRank = merge.Rank; bestId = merge.Id; }
            if (best < 0) break;
            symbols[best] = bestId; symbols.RemoveAt(best + 1);
        }
        output.AddRange(symbols);
    }
}
