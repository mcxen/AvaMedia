using AvaMedia.Core;

namespace AvaMedia.Mcp;

public static class McpTaskCounts
{
    /// <summary>Every state appears (zero when absent) so filter badges stay stable; "All" is the total.</summary>
    public static IReadOnlyDictionary<string, int> From(IEnumerable<JobState> states)
    {
        var counts = Enum.GetValues<JobState>().ToDictionary(state => state.ToString(), _ => 0);
        var total = 0;
        foreach (var state in states) { counts[state.ToString()]++; total++; }
        counts["All"] = total;
        return counts;
    }
}
