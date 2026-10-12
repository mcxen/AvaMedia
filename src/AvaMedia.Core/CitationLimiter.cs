namespace AvaMedia.Core;

public static class CitationLimiter
{
    public static string[] Limit(IEnumerable<string> ids, IReadOnlyDictionary<string, double>? times = null, int maximum = 8)
    {
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        var values = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).OrderBy(id => times?.GetValueOrDefault(id) ?? 0).ThenBy(id => id, StringComparer.Ordinal).ToArray();
        if (values.Length <= maximum) return values;
        if (maximum == 1) return [values[0]];
        return Enumerable.Range(0, maximum).Select(i => values[(int)Math.Round(i * (values.Length - 1d) / (maximum - 1))]).ToArray();
    }
}
