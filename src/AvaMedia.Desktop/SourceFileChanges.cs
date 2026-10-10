using System.Text.Json;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal static class SourceFileChanges
{
    public static async Task<string[]> RenamePathsAsync(string journal, bool undo, RenameItem[]? plan)
    {
        if (undo)
        {
            var record = JsonSerializer.Deserialize<BatchRename.RenameJournal>(await File.ReadAllTextAsync(journal))
                ?? throw new InvalidDataException("恢复记录无效。");
            plan = record.Stages.Select(stage => stage.Item).ToArray();
        }
        return (plan ?? []).SelectMany(item => new[] { item.Source, item.Target }).Append(journal).ToArray();
    }
}
