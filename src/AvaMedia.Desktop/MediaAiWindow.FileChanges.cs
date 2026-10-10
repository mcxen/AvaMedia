using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private void ApplyFileMappings(IReadOnlyList<RenameItem> mappings, string status)
    {
        var retained = mappings.Select(mapping => (Mapping: mapping, Result: _results.GetValueOrDefault(mapping.Source),
            Tags: _editedTags.GetValueOrDefault(mapping.Source), Position: _positions.GetValueOrDefault(mapping.Source),
            Traces: _traces.GetValueOrDefault(mapping.Source), ReportSource: _reportSources.GetValueOrDefault(mapping.Source),
            Task: _taskJobs.GetValueOrDefault(mapping.Source),
            Entry: _entries.FirstOrDefault(entry => BatchRename.PathComparer.Equals(entry.Path, mapping.Source)))).ToArray();
        foreach (var item in retained)
        {
            _results.Remove(item.Mapping.Source); _editedTags.Remove(item.Mapping.Source); _liveResults.Remove(item.Mapping.Source);
            _positions.Remove(item.Mapping.Source); _traces.Remove(item.Mapping.Source); _reportSources.Remove(item.Mapping.Source);
            _taskJobs.Remove(item.Mapping.Source);
        }
        foreach (var item in retained)
        {
            if (item.Result is { } result) _results[item.Mapping.Target] = result with { Path = item.Mapping.Target };
            _positions[item.Mapping.Target] = item.Position;
            if (item.Traces is { } traces) _traces[item.Mapping.Target] = traces;
            if (item.ReportSource is { } reportSource) _reportSources[item.Mapping.Target] = reportSource;
            if (item.Tags is { } tags) _editedTags[item.Mapping.Target] = tags;
            if (item.Task is { } task) _taskJobs[item.Mapping.Target] = task;
            if (item.Entry is { } entry) { entry.Renamed(item.Mapping.Target); entry.Status = Localization.Text(status); entry.Keyword = ""; }
        }
        Renamed?.Invoke(mappings);
    }
}
