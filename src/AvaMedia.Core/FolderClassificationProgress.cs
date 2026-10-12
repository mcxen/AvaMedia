using System.ComponentModel;
using System.Diagnostics;

namespace AvaMedia.Core;

/// <summary>Thread-neutral projection. Supply the owner's dispatcher to UpdateThrottled.</summary>
public sealed class FolderClassificationProgress : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public FolderStageProgress[] Stages { get; private set; } = [];
    public double Percent { get; private set; }
    public string Detail { get; private set; } = "";
    public TimeSpan Elapsed { get; private set; }
    public FolderFileVisual? Current { get; private set; }
    public FolderGroupView[] Groups { get; private set; } = [];
    public FolderHistogram Histogram { get; private set; } = new(.5, .95, .64, new int[20], 0, 0, 0);
    public int Regroup { get; private set; }
    public int Completed { get; private set; }
    public int Pending { get; private set; }
    public int Failed { get; private set; }
    public IReadOnlyList<FolderFileVisual> Files { get; private set; } = [];

    private sealed record Cached(FolderClassificationTaskFile File, FolderFileVisual Visual, bool AppearanceComplete);
    private readonly Dictionary<string, Cached> _cache = new(BatchRename.PathComparer);
    private Guid _jobId;
    private FolderClassificationTaskSnapshot? _snapshot;
    private FolderClassificationRule[] _rules = [];
    private FolderRegroupState? _regroup;
    private double _threshold = double.NaN;
    private string? _currentPath;
    private readonly object _gate = new();
    private Job? _pendingJob;
    private Action<Action>? _post;
    private bool _scheduled;
    private long _lastApplied;

    public void Update(Job job)
    {
        var snapshot = job.ClassificationSnapshot ?? new(job.Inputs.Select(path => new FolderClassificationTaskFile(path)).ToArray());
        var rules = job.Options.FolderClassification?.Rules ?? [];
        var activity = job.Activity;
        if (_jobId != job.Id || !_rules.SequenceEqual(rules))
        { _cache.Clear(); _snapshot = null; _jobId = job.Id; _rules = rules.ToArray(); }
        var changed = !ReferenceEquals(snapshot, _snapshot);
        if (changed)
        {
            var remaining = _cache.Keys.ToHashSet(BatchRename.PathComparer);
            foreach (var file in snapshot.Files)
            {
                remaining.Remove(file.Path);
                if (_cache.TryGetValue(file.Path, out var cached) && ReferenceEquals(file.Result, cached.File.Result)
                    && file.Pending == cached.File.Pending && file.Error == cached.File.Error) continue;
                var complete = file.Result is { } result && (cached is not null && ReferenceEquals(result.Media, cached.File.Result?.Media)
                    ? cached.AppearanceComplete : OutfitAppearanceService.IsComplete(result.Media));
                _cache[file.Path] = new(file, FolderClassificationInsights.Visual(file, rules, false), complete);
            }
            foreach (var path in remaining) _cache.Remove(path);
        }
        // Activity carries the source name, while pending partial results also identify a working file.
        var current = job.State == JobState.Running && activity is { State: AiActivityState.Running }
            ? snapshot.Files.FirstOrDefault(file => file.Error is null && (activity.Detail == file.Path || activity.Detail == Path.GetFileName(file.Path)
                || activity.Detail.StartsWith(Path.GetFileName(file.Path) + " · ", StringComparison.Ordinal)))
                ?? snapshot.Files.FirstOrDefault(file => file.Pending && file.Result is not null) : null;
        Current = current is null ? null : _cache[current.Path].Visual with { State = FolderFileState.Processing };
        if (changed || _currentPath != current?.Path)
            Files = snapshot.Files.Select(file => current?.Path == file.Path ? Current! : _cache[file.Path].Visual).ToArray();
        _currentPath = current?.Path;
        var regroup = job.ClassificationRegroup;
        var threshold = rules.FirstOrDefault(rule => rule.ByOutfit)?.OutfitSimilarity ?? job.Options.FolderClassification?.TagThreshold ?? .5;
        if (changed || !ReferenceEquals(regroup, _regroup))
        {
            Groups = FolderClassificationInsights.Groups(snapshot.Files, null).Select(group => group with
            { Added = regroup?.Added.GetValueOrDefault(group.RuleId + "/" + group.CategoryId) ?? 0 }).ToArray();
        }
        if (changed || threshold != _threshold) Histogram = FolderClassificationInsights.Histogram(snapshot.Files, threshold);
        Regroup = regroup?.Ordinal ?? 0;
        Stages = FolderClassificationInsights.Stages(snapshot, rules, Regroup, activity?.Stage,
            media => _cache.TryGetValue(media.Path, out var cached) && cached.AppearanceComplete);
        Percent = Math.Clamp(job.Progress, 0, 100); Detail = job.ProgressDetail; Elapsed = job.ClassificationElapsed;
        Completed = _cache.Values.Count(value => value.Visual.State == FolderFileState.Done);
        Pending = _cache.Values.Count(value => value.Visual.State == FolderFileState.Pending);
        Failed = snapshot.Failed;
        _snapshot = snapshot; _regroup = regroup; _threshold = threshold;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }

    /// <summary>Coalesces both timer and dispatcher backlog; the newest job is read on the owner thread.
    /// At most one dispatcher callback is queued, and consecutive applications are at least 100 ms apart.</summary>
    public void UpdateThrottled(Job job, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        lock (_gate)
        {
            _pendingJob = job; _post = post;
            if (_scheduled) return;
            _scheduled = true;
        }
        _ = ScheduleAsync();
    }
    private async Task ScheduleAsync()
    {
        var delay = TimeSpan.FromMilliseconds(100) - Stopwatch.GetElapsedTime(_lastApplied);
        if (_lastApplied != 0 && delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
        Action<Action> post;
        lock (_gate) post = _post!;
        post(() =>
        {
            Job job;
            lock (_gate) { job = _pendingJob!; _pendingJob = null; }
            try { Update(job); }
            finally
            {
                bool again;
                lock (_gate)
                {
                    _lastApplied = Stopwatch.GetTimestamp();
                    again = _pendingJob is not null;
                    if (!again) _scheduled = false;
                }
                if (again) _ = ScheduleAsync();
            }
        });
    }
}
