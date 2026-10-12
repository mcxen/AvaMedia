using System.Text;

namespace AvaMedia.Core;

public interface ITokenCounter { int Count(string text); }
public sealed class ConservativeTokenCounter : ITokenCounter
{
    public int Count(string text)
    {
        long count = 0, bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is >= 0x2E80 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF
                or >= 0x3040 and <= 0x30FF or >= 0xAC00 and <= 0xD7AF)
            { count += (bytes + 2) / 3 + 1; bytes = 0; }
            else bytes += rune.Utf8SequenceLength;
        }
        return checked((int)(count + (bytes + 2) / 3));
    }
}
public sealed record TokenBudget(int Context, int MaxOutput, int SystemPrompt, int Safety, int FrameLimit)
{
    public int Input => Context - MaxOutput - SystemPrompt - Safety;
    public static TokenBudget Local8K => new(8192, 1024, 1024, 820, 4);
    public static TokenBudget For(OnlineAiModelInfo info)
    {
        var context = info.ContextTokens ?? 8192;
        var output = Math.Min(info.MaxOutputTokens ?? 2048, context >= 32768 ? 2048 : 1024);
        var result = new TokenBudget(context, output, 1024, (int)Math.Ceiling(context * .1), context >= 32768 ? 8 : 4);
        if (result.Input < 512) throw new ArgumentException("模型上下文不足以总结视频。");
        return result;
    }
    public void Assert(string prompt, ITokenCounter counter)
    {
        if (counter.Count(prompt) > Input) throw new InvalidDataException("总结请求超出输入预算。");
    }
}
