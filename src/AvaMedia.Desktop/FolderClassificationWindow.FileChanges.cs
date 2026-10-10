using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class FolderClassificationWindow
{
    private void ApplyViewerMoves(IReadOnlyList<RenameItem> mappings)
    {
        Moved?.Invoke(mappings);
        _syncing = true;
        try
        {
            foreach (var mapping in mappings)
            {
                if (_results.Remove(mapping.Source, out var result))
                    _results[mapping.Target] = result with { Media = result.Media with { Path = mapping.Target } };
                if (_analysisPending.Remove(mapping.Source)) _analysisPending.Add(mapping.Target);
                for (var index = 0; index < _inputs.Count; index++)
                    if (BatchRename.PathComparer.Equals(_inputs[index], mapping.Source)) _inputs[index] = mapping.Target;
                var entry = _entries.FirstOrDefault(item => BatchRename.PathComparer.Equals(item.Path, mapping.Source));
                if (entry is not null) { entry.Renamed(mapping.Target); entry.Status = Localization.Text("已移动"); }
            }
        }
        finally { _syncing = false; InvalidatePlan(); RenderBoard(); }
    }
}
