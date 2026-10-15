using System.Text.Json;

namespace AvaMedia.Core;

public static partial class PersonClipEditing
{
    private static partial async Task<IPersonClipEditSession> OpenCoreAsync(Job job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ct.ThrowIfCancellationRequested();
        var path = job.Options.PersonClip?.AnalysisOnly == true ? job.Output : AiTaskResults.PathFor(job, "people");
        PersonDetectionTaskResult? result;
        try { result = job.PersonDetectionResult ?? await AiTaskResults.LoadAsync<PersonDetectionTaskResult>(path, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { throw new InvalidOperationException("无法读取完整的人物检测结果，请重新检测。", error); }
        if (result is null || result.Result is null || result.Result.IsPartial)
            throw new InvalidOperationException("尚无完整的人物检测结果，请先完成检测。");
        var session = new PersonClipEditSession(job, result, path);
        job.PersonDetectionResult ??= result;
        return session;
    }
}

/// <summary>Atomic editing commands over a completed detection. All mutable options are copied at the boundary.</summary>
public sealed class PersonClipEditSession : IPersonClipEditSession
{
    private readonly PersonDetectionTaskResult _detected;
    private PersonDetectionTaskResult _savedResult;
    private readonly string _savePath;
    private readonly ConversionOptions[] _original;
    private readonly PersonClipRange[] _originalExclusions;
    private readonly PersonClipSample[] _samples;
    private List<PersonClipEditEntry> _entries;
    private List<PersonClipRange> _exclusions;
    private int _nextId;
    private sealed record Snapshot(List<PersonClipEditEntry> Entries, List<PersonClipRange> Exclusions);
    private readonly List<Snapshot> _undo = [], _redo = [];

    internal PersonClipEditSession(Job job, PersonDetectionTaskResult detected, string savePath)
    {
        Job = job; _detected = detected; _savedResult = detected; _savePath = savePath;
        SourcePath = detected.Result.Path; Info = detected.Result.Info;
        if (!double.IsFinite(Duration) || Duration <= 0 || detected.Result.Segments is null)
            throw new InvalidOperationException("人物检测结果无效，请重新检测。");
        var detection = job.Options.PersonClip?.Detection ?? new();
        Threshold = detection.Threshold; MinimumSeconds = detection.MinimumSeconds;
        var edits = detected.Edits;
        _original = (edits?.DetectionSegments ?? detected.Result.Segments).Select(option => option.Clone()).ToArray();
        _originalExclusions = PersonClipExclusions.Normalize(edits?.DetectionExclusions ?? detection.ExcludedRanges ?? []);
        _samples = (detected.Result.Samples ?? []).OrderBy(sample => sample.Seconds).ToArray();
        _entries = edits is null ? DetectionEntries() : edits.Entries.Select(CloneEntry).ToList();
        _exclusions = PersonClipExclusions.Normalize(edits?.Exclusions ?? _originalExclusions).ToList();
        _nextId = Math.Max(edits?.NextId ?? 1, Math.Max(_original.Length, _entries.Select(e => e.Segment.Id).DefaultIfEmpty(0).Max()) + 1);
        Validate(_entries);
    }

    public Job Job { get; }
    public string SourcePath { get; }
    public MediaInfo Info { get; }
    public double Duration => Info.Duration;
    public double Threshold { get; }
    public double MinimumSeconds { get; }
    public IReadOnlyList<PersonClipEditSegment> Segments => _entries.Select(entry => Describe(entry)).ToArray();
    public IReadOnlyList<PersonClipSample> Samples => Array.AsReadOnly(_samples);
    public bool HasScores => _samples.Length > 0;
    public IReadOnlyList<PersonClipRange> Exclusions => _exclusions.ToArray();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public double KeptSeconds => _entries.Where(e => e.Segment.Keep).Sum(e => e.Segment.Duration);
    public int KeptCount => _entries.Count(e => e.Segment.Keep);
    public double DetectedSeconds => _original.Sum(option => option.End - option.Start);
    public bool SourceChanged
    {
        get
        {
            var file = new FileInfo(SourcePath);
            return !file.Exists || file.Length != _detected.SourceLength || file.LastWriteTimeUtc != _detected.SourceWriteUtc;
        }
    }
    public event EventHandler? Changed;

    // Count each changed resulting segment once, even if several of its properties changed. A structural
    // replacement counts its resulting parts, not the vanished ancestors. Order swaps count affected segments.
    // Extending/merging an exclusion counts the changed mask once; a removed baseline mask also counts once.
    public int ModifiedCount
    {
        get
        {
            var orderedIds = _entries.Where(e => !e.Restructured && e.DetectionIds.Length == 1)
                .Select(e => e.DetectionIds[0]).ToArray();
            var sortedIds = orderedIds.Order().ToArray();
            var moved = orderedIds.Where((id, index) => id != sortedIds[index]).ToHashSet();
            var changes = _entries.Count(e => e.Segment.Origin == PersonClipSegmentOrigin.Manual || e.Restructured
                || e.DetectionIds.Length != 1 || !e.Segment.Keep || moved.Contains(e.DetectionIds[0])
                || !SameOptions(e.Segment.Options, _original[e.DetectionIds[0] - 1]));
            return changes + _exclusions.Count(range => !_originalExclusions.Contains(range))
                + _originalExclusions.Count(range => !_exclusions.Any(current => range.Start < current.End && range.End > current.Start));
        }
    }

    private List<PersonClipEditEntry> DetectionEntries() => _original.Select((option, index) =>
        new PersonClipEditEntry(new(index + 1, option.Clone(), true, PersonClipSegmentOrigin.Detected, false), [index + 1])).ToList();
    private static PersonClipEditEntry CloneEntry(PersonClipEditEntry entry) => entry with
    { Segment = entry.Segment with { Options = entry.Segment.Options.Clone() }, DetectionIds = entry.DetectionIds.ToArray() };
    private Snapshot Capture() => new(_entries.Select(CloneEntry).ToList(), _exclusions.ToList());
    private static bool SameOptions(ConversionOptions a, ConversionOptions b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    private PersonClipEditSegment Describe(PersonClipEditEntry entry)
    {
        var segment = entry.Segment;
        var original = entry.DetectionIds.Length > 0 ? _original[entry.DetectionIds[0] - 1] : null;
        var scores = _samples.Where(sample => sample.Analysed && sample.Seconds >= segment.Start && sample.Seconds < segment.End).ToArray();
        double? confidence = scores.Length > 0 ? scores.Average(sample => (double)sample.Score) : null;
        return segment with { Options = segment.Options.Clone(), Adjusted = original is not null && (segment.Start != original.Start || segment.End != original.End),
            Confidence = confidence, LowConfidence = confidence is { } average && average < Threshold + .1 };
    }
    private static int Index(List<PersonClipEditEntry> entries, int id)
    {
        var index = entries.FindIndex(entry => entry.Segment.Id == id);
        return index >= 0 ? index : throw new ArgumentException("找不到该片段。");
    }
    private void Validate(List<PersonClipEditEntry> entries)
    {
        var end = 0d;
        foreach (var entry in entries.OrderBy(entry => entry.Segment.Start))
        {
            var segment = entry.Segment;
            if (!double.IsFinite(segment.Start) || !double.IsFinite(segment.End) || segment.Start < 0 || segment.End > Duration || segment.End <= segment.Start)
                throw new ArgumentException("片段时间必须位于视频时长范围内。");
            if (segment.End - segment.Start < MinimumSeconds - 1e-9)
                throw new ArgumentException($"片段长度不能少于 {MinimumSeconds:0.###} 秒。");
            if (segment.Start < end) throw new ArgumentException("片段不能重叠，已移除片段也占用原时间范围。");
            end = segment.End;
        }
    }
    private void Command(Action<List<PersonClipEditEntry>, List<PersonClipRange>> edit)
    {
        var before = Capture(); var draft = Capture(); var nextId = _nextId;
        try { edit(draft.Entries, draft.Exclusions); Validate(draft.Entries); }
        catch { _nextId = nextId; throw; }
        _undo.Add(before); if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear(); _entries = draft.Entries; _exclusions = draft.Exclusions;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private static ConversionOptions Bounds(ConversionOptions source, double start, double end)
    { var option = source.Clone(); option.Start = start; option.End = end; return option; }
    private static void InsertByTime(List<PersonClipEditEntry> entries, PersonClipEditEntry entry)
    {
        var index = entries.FindIndex(other => other.Segment.Start > entry.Segment.Start);
        entries.Insert(index < 0 ? entries.Count : index, entry);
    }
    private static void ReplaceBounds(List<PersonClipEditEntry> entries, int index, double start, double end)
    {
        var entry = entries[index]; entries[index] = entry with { Segment = entry.Segment with { Options = Bounds(entry.Segment.Options, start, end) } };
    }
    public void SetKeep(int id, bool keep) => Command((entries, _) =>
    {
        var index = Index(entries, id);
        if (!keep && entries[index].Segment.Keep && entries.Count(e => e.Segment.Keep) == 1)
            throw new ArgumentException("请至少保留一个片段。");
        entries[index] = entries[index] with { Segment = entries[index].Segment with { Keep = keep } };
    });
    public void SetBounds(int id, double start, double end) => Command((entries, _) => ReplaceBounds(entries, Index(entries, id), start, end));
    public int Split(int id, double at)
    {
        var segment = _entries[Index(_entries, id)].Segment;
        var next = _nextId;
        SplitMany(id, [Bounds(segment.Options, segment.Start, at), Bounds(segment.Options, at, segment.End)]);
        return next;
    }
    public void SplitMany(int id, IReadOnlyList<ConversionOptions> parts) => Command((entries, _) =>
    {
        var index = Index(entries, id); var entry = entries[index];
        if (parts is null || parts.Count < 2 || parts.Any(part => part is null)) throw new ArgumentException("请至少拆分为两个片段。");
        var sorted = parts.OrderBy(part => part.Start).ToArray();
        if (sorted[0].Start != entry.Segment.Start || sorted[^1].End != entry.Segment.End
            || sorted.Skip(1).Where((part, i) => part.Start != sorted[i].End).Any())
            throw new ArgumentException("拆分片段必须连续覆盖原片段。");
        entries.RemoveAt(index);
        entries.InsertRange(index, parts.Select((part, i) => entry with
        { Segment = entry.Segment with { Id = i == 0 ? id : _nextId++, Options = part.Clone() }, Restructured = true }));
    });
    public void MergeWithNext(int id) => Merge(id, 1);
    public void MergeWithPrevious(int id) => Merge(id, -1);
    private void Merge(int id, int delta) => Command((entries, _) =>
    {
        var index = Index(entries, id); var adjacent = index + delta;
        if (adjacent < 0 || adjacent >= entries.Count) throw new ArgumentException("没有可合并的相邻片段。");
        var entry = entries[index]; var other = entries[adjacent];
        var merged = entry with { Segment = entry.Segment with
            { Options = Bounds(entry.Segment.Options, Math.Min(entry.Segment.Start, other.Segment.Start), Math.Max(entry.Segment.End, other.Segment.End)),
              Keep = entry.Segment.Keep || other.Segment.Keep,
              Origin = entry.DetectionIds.Length + other.DetectionIds.Length > 0 ? PersonClipSegmentOrigin.Detected : PersonClipSegmentOrigin.Manual },
            DetectionIds = entry.DetectionIds.Concat(other.DetectionIds).Distinct().ToArray(), Restructured = true };
        entries.RemoveAt(Math.Max(index, adjacent)); entries.RemoveAt(Math.Min(index, adjacent));
        entries.Insert(Math.Min(index, adjacent), merged);
    });
    public int Add(double start, double end)
    {
        var id = _nextId;
        Command((entries, _) => InsertByTime(entries, new(new(_nextId++, new() { Start = start, End = end }, true, PersonClipSegmentOrigin.Manual, false), [])));
        return id;
    }
    public int Duplicate(int id)
    {
        var next = _nextId;
        Command((entries, _) =>
        {
            var segment = entries[Index(entries, id)].Segment;
            var gaps = PersonClipExclusions.Subtract([new(0, Duration)], entries.Select(e => new PersonClipRange(e.Segment.Start, e.Segment.End)));
            var gap = gaps.Where(gap => gap.End - gap.Start >= segment.Duration - 1e-9)
                .OrderBy(gap => gap.Start < segment.End).ThenBy(gap => gap.Start).FirstOrDefault()
                ?? throw new ArgumentException("没有足够的空白时间复制片段。");
            InsertByTime(entries, new(new(_nextId++, Bounds(segment.Options, gap.Start, gap.Start + segment.Duration), segment.Keep,
                PersonClipSegmentOrigin.Manual, false), []));
        });
        return next;
    }
    public void Move(int id, int delta) => Command((entries, _) =>
    {
        var index = Index(entries, id); var target = index + delta;
        if (delta is not (-1 or 1) || target < 0 || target >= entries.Count) throw new ArgumentException("片段只能向前或向后移动一位。");
        (entries[index], entries[target]) = (entries[target], entries[index]);
    });
    public void SetEffects(int id, ConversionOptions options) => Command((entries, _) =>
    {
        if (options is null) throw new ArgumentException("缺少片段效果参数。");
        var index = Index(entries, id); var segment = entries[index].Segment;
        entries[index] = entries[index] with { Segment = segment with { Options = Bounds(options, segment.Start, segment.End) } };
    });
    public void RestoreSegment(int id) => Command((entries, _) =>
    {
        var entry = entries[Index(entries, id)];
        if (entry.DetectionIds.Length == 0) { entries.Remove(entry); return; }
        // Restore the entire connected lineage, including siblings of splits and ancestors of merges.
        var ids = entry.DetectionIds.ToHashSet();
        bool expanded;
        do
        {
            var count = ids.Count;
            foreach (var related in entries.Where(e => e.DetectionIds.Any(ids.Contains))) ids.UnionWith(related.DetectionIds);
            expanded = ids.Count != count;
        } while (expanded);
        entries.RemoveAll(e => e.DetectionIds.Any(ids.Contains));
        foreach (var original in DetectionEntries().Where(e => ids.Contains(e.Segment.Id))) InsertByTime(entries, original);
    });
    public void RestoreDetection() => Command((entries, exclusions) =>
    { entries.Clear(); entries.AddRange(DetectionEntries()); exclusions.Clear(); exclusions.AddRange(_originalExclusions); });
    public void InvertKeep() => Command((entries, _) =>
    {
        if (entries.Count == 0 || entries.All(entry => entry.Segment.Keep)) throw new ArgumentException("反选后须至少保留一个片段。");
        for (var i = 0; i < entries.Count; i++) entries[i] = entries[i] with { Segment = entries[i].Segment with { Keep = !entries[i].Segment.Keep } };
    });
    public void AddExclusion(double start, double end) => Command((entries, exclusions) =>
    {
        var range = new PersonClipRange(start, end); range.Validate();
        if (end > Duration) throw new ArgumentException("免检测区间超出视频时长。");
        exclusions.Add(range); var normalized = PersonClipExclusions.Normalize(exclusions); exclusions.Clear(); exclusions.AddRange(normalized);
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i]; var segment = entry.Segment;
            if (segment.Start >= end || segment.End <= start) continue;
            var remaining = PersonClipExclusions.Subtract([new(segment.Start, segment.End)], [range])
                .Where(part => part.End - part.Start >= MinimumSeconds - 1e-9).ToArray();
            if (remaining.Length == 0)
                entries[i] = entry with { Segment = segment with { Keep = false } };
            else
            {
                entries.RemoveAt(i);
                entries.InsertRange(i, remaining.Select((part, index) => entry with
                { Segment = segment with { Id = index == 0 ? segment.Id : _nextId++, Options = Bounds(segment.Options, part.Start, part.End) },
                    Restructured = entry.Restructured || remaining.Length > 1 }));
            }
        }
    });
    public void RemoveExclusion(int index) => Command((_, exclusions) =>
    {
        if (index < 0 || index >= exclusions.Count) throw new ArgumentException("找不到该免检测区间。");
        exclusions.RemoveAt(index);
    });
    public void Undo() => Travel(_undo, _redo);
    public void Redo() => Travel(_redo, _undo);
    private void Travel(List<Snapshot> from, List<Snapshot> to)
    {
        if (from.Count == 0) return;
        to.Add(Capture()); var state = from[^1]; from.RemoveAt(from.Count - 1);
        _entries = state.Entries; _exclusions = state.Exclusions; Changed?.Invoke(this, EventArgs.Empty);
    }

    public PersonClipSegmentEvidence? Evidence(int id)
    {
        var segment = _entries[Index(_entries, id)].Segment;
        if (!HasScores) return null;
        var inside = _samples.Where(sample => sample.Seconds >= segment.Start && sample.Seconds < segment.End).ToArray();
        var analysed = inside.Where(sample => sample.Analysed).ToArray();
        return new(inside.Length, inside.Count(sample => sample.Flags.HasFlag(PersonClipSampleFlags.Person)),
            inside.Count(sample => sample.Flags.HasFlag(PersonClipSampleFlags.Uncertain)),
            analysed.Select(sample => (double)sample.Score).DefaultIfEmpty(0).Average(),
            analysed.Select(sample => (double)sample.Score).DefaultIfEmpty(0).Max(),
            inside.Count(sample => sample.Flags.HasFlag(PersonClipSampleFlags.Boundary)),
            _samples.Where(sample => sample.Seconds >= segment.Start - 2 && sample.Seconds <= segment.End + 2).ToArray());
    }
    public PersonClipSnap? Snap(double seconds, double tolerance, int? ignoreId = null)
    {
        if (!double.IsFinite(seconds) || !double.IsFinite(tolerance) || tolerance < 0) throw new ArgumentException("吸附时间与容差无效。");
        var targets = new List<PersonClipSnap>();
        if (HasScores)
        {
            var step = SampleStep;
            for (var i = 0; i < _samples.Length; i++)
            {
                var sample = _samples[i];
                if (!sample.Flags.HasFlag(PersonClipSampleFlags.Person) || !sample.Analysed) continue;
                bool Person(int j) => j >= 0 && j < _samples.Length && _samples[j].Analysed && _samples[j].Flags.HasFlag(PersonClipSampleFlags.Person)
                    && Math.Abs(_samples[j].Seconds - sample.Seconds) <= step + .001;
                if (!Person(i - 1)) targets.Add(new(sample.Seconds, PersonClipSnapKind.FirstPersonFrame, "首个有人帧"));
                if (!Person(i + 1)) targets.Add(new(sample.Seconds, PersonClipSnapKind.LastPersonFrame, "末个有人帧"));
            }
        }
        else foreach (var original in _original)
        { targets.Add(new(original.Start, PersonClipSnapKind.DetectionSegment, "检测片段边界")); targets.Add(new(original.End, PersonClipSnapKind.DetectionSegment, "检测片段边界")); }
        foreach (var entry in _entries.Where(entry => entry.Segment.Id != ignoreId))
        { targets.Add(new(entry.Segment.Start, PersonClipSnapKind.SegmentEdge, "相邻片段")); targets.Add(new(entry.Segment.End, PersonClipSnapKind.SegmentEdge, "相邻片段")); }
        foreach (var range in _exclusions)
        { targets.Add(new(range.Start, PersonClipSnapKind.Exclusion, "排除区间")); targets.Add(new(range.End, PersonClipSnapKind.Exclusion, "排除区间")); }
        return targets.Where(target => Math.Abs(target.Seconds - seconds) <= tolerance).OrderBy(target => Math.Abs(target.Seconds - seconds)).FirstOrDefault();
    }
    private double SampleStep => 1 / Math.Clamp(Job.Options.PersonClip?.Detection.FramesPerSecond ?? 2, .25, 16);
    public IReadOnlyList<PersonClipSpan> Spans => BuildSpans();
    private IReadOnlyList<PersonClipSpan> BuildSpans()
    {
        // Samples cover their forward sampling cell; boundary probes never stretch a cell beyond one cadence.
        // A missing cell is explicitly unscanned. Explicit segment decisions take precedence over evidence:
        // a manually kept dark/unscanned interval must still be shown as kept.
        var cells = _samples.Select((sample, i) => (Sample: sample, End: Math.Min(Duration,
            Math.Min(sample.Seconds + SampleStep, i + 1 < _samples.Length ? _samples[i + 1].Seconds : Duration))))
            .Where(cell => cell.Sample.Seconds >= 0 && cell.End > cell.Sample.Seconds).ToArray();
        var analysedEnd = Math.Clamp(_detected.Result.AnalyzedSeconds, 0, Duration);
        var points = new SortedSet<double> { 0, Duration, analysedEnd };
        foreach (var entry in _entries) { points.Add(entry.Segment.Start); points.Add(entry.Segment.End); }
        foreach (var range in _exclusions) { points.Add(range.Start); points.Add(range.End); }
        if (HasScores) foreach (var cell in cells) { points.Add(cell.Sample.Seconds); points.Add(cell.End); }
        var boundaries = points.Where(value => value >= 0 && value <= Duration).ToArray();
        var result = new List<PersonClipSpan>();
        var chronological = _entries.OrderBy(entry => entry.Segment.Start).ToArray();
        int cellIndex = 0, entryIndex = 0, exclusionIndex = 0;
        for (var i = 1; i < boundaries.Length; i++)
        {
            var start = boundaries[i - 1]; var end = boundaries[i]; var at = start + (end - start) / 2;
            while (cellIndex < cells.Length && cells[cellIndex].End <= at) cellIndex++;
            while (entryIndex < chronological.Length && chronological[entryIndex].Segment.End <= at) entryIndex++;
            while (exclusionIndex < _exclusions.Count && _exclusions[exclusionIndex].End <= at) exclusionIndex++;
            var segment = entryIndex < chronological.Length && chronological[entryIndex].Segment.Start <= at ? chronological[entryIndex].Segment : null;
            var reason = PersonClipUnscannedReason.None;
            if (exclusionIndex < _exclusions.Count && _exclusions[exclusionIndex].Start <= at) reason = PersonClipUnscannedReason.Excluded;
            else if (at >= analysedEnd) reason = PersonClipUnscannedReason.NotAnalysed;
            else if (HasScores)
            {
                if (cellIndex >= cells.Length || cells[cellIndex].Sample.Seconds > at) reason = PersonClipUnscannedReason.NotAnalysed;
                else reason = cells[cellIndex].Sample.Flags switch
                {
                    var f when f.HasFlag(PersonClipSampleFlags.Excluded) => PersonClipUnscannedReason.Excluded,
                    var f when f.HasFlag(PersonClipSampleFlags.Dark) => PersonClipUnscannedReason.Dark,
                    var f when f.HasFlag(PersonClipSampleFlags.Blank) => PersonClipUnscannedReason.Blank,
                    _ => PersonClipUnscannedReason.None
                };
            }
            var kind = segment is not null ? (segment.Keep ? PersonClipSpanKind.Kept : PersonClipSpanKind.RemovedByUser)
                : reason != PersonClipUnscannedReason.None ? PersonClipSpanKind.Unscanned : PersonClipSpanKind.NoPerson;
            if (kind != PersonClipSpanKind.Unscanned) reason = PersonClipUnscannedReason.None;
            var span = new PersonClipSpan(start, end, kind, reason);
            if (result.Count > 0 && result[^1].Kind == kind && result[^1].Reason == reason) result[^1] = result[^1] with { End = end };
            else result.Add(span);
        }
        return result;
    }
    public ClipEditResult ToClipEditResult()
    {
        EnsureCurrentResult();
        if (SourceChanged) throw new ArgumentException("源视频已发生变化，请重新检测后导出。");
        if (KeptCount == 0) throw new ArgumentException("请至少保留一个片段后再导出。");
        Validate(_entries);
        return new(SourcePath, Info, _entries.Where(entry => entry.Segment.Keep).Select(entry => entry.Segment.Options.Clone()).ToArray());
    }
    public PersonClipOptions RedetectOptions() => (Job.Options.PersonClip?.Detection ?? new()) with
    { ExcludedRanges = PersonClipExclusions.Normalize(_exclusions) };
    private void EnsureCurrentResult()
    {
        if (!ReferenceEquals(Job.PersonDetectionResult, _savedResult))
            throw new InvalidOperationException("人物检测结果已更新，请关闭调整窗口后重新打开。");
    }
    public async Task SaveAsync(CancellationToken ct = default)
    {
        EnsureCurrentResult();
        var snapshot = Capture();
        var state = new PersonClipEditState(_original.Select(option => option.Clone()).ToArray(), _originalExclusions.ToArray(),
            snapshot.Entries, snapshot.Exclusions, _nextId);
        var revised = _detected with { Edits = state, Result = _detected.Result with
            { Segments = snapshot.Entries.Where(entry => entry.Segment.Keep).Select(entry => entry.Segment.Options.Clone()).ToArray() } };
        var pending = _savePath + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            await AiTaskResults.SaveAsync(pending, revised, ct).ConfigureAwait(false);
            EnsureCurrentResult();
            ct.ThrowIfCancellationRequested();
            File.Move(pending, _savePath, true);
            _savedResult = revised;
            Job.PersonDetectionResult = revised;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
