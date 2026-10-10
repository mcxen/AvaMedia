namespace AvaMedia.Core;

public sealed class QueueService(IJobExecutor engine, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly List<Job> _pending = [];
    private readonly Dictionary<Job, Execution> _active = [];
    private readonly Dictionary<Guid, string[]> _fileChanges = [];
    private TaskCompletionSource _wake = NewSignal();
    private CancellationTokenSource? _cts;
    private bool _accepting;
    public bool IsRunning { get { lock (_gate) return _cts is not null; } }
    public bool IsStopping { get { lock (_gate) return _cts is not null && !_accepting; } }
    public bool IsExecuting(Job job) { lock (_gate) return _active.ContainsKey(job); }
    public bool IsScheduled(Job job) { lock (_gate) return _active.ContainsKey(job) || _pending.Contains(job); }
    public event Action<Job>? Changed;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void WakeLocked() { _wake.TrySetResult(); _wake = NewSignal(); }

    public Job[] Enqueue(IEnumerable<Job> jobs)
    {
        var candidates = jobs.Distinct().ToArray();
        lock (_gate)
        {
            if (_cts is null || !_accepting) return [];
            var added = candidates.Where(job => job.State == JobState.Waiting && !_active.ContainsKey(job) && !_pending.Contains(job)).ToArray();
            _pending.AddRange(added); WakeLocked(); return added;
        }
    }

    public async Task Run(IEnumerable<Job> jobs, int concurrency)
    {
        var candidates = jobs.Distinct().ToArray();
        CancellationTokenSource session;
        lock (_gate)
        {
            if (_cts is not null) return;
            session = _cts = new(); _accepting = true;
            _pending.AddRange(candidates.Where(job => job.State == JobState.Waiting));
        }
        var executions = new List<Task>();
        try
        {
            while (true)
            {
                List<Execution> starting = [];
                Task wait;
                lock (_gate)
                {
                    _pending.RemoveAll(job => job.State != JobState.Waiting);
                    while (_accepting && _pending.Count > 0)
                    {
                        var job = _pending.FirstOrDefault(candidate => !FilesReserved(candidate) && !MutationConflict(candidate)
                            && _active.Values.Count(execution =>
                            !execution.Control.IsParked && Lane(execution.Job) == Lane(candidate)) < Math.Clamp(concurrency, 1, 8));
                        if (job is null) break;
                        _pending.Remove(job);
                        var execution = new Execution(job, session.Token);
                        execution.Control.Parked += () => { lock (_gate) WakeLocked(); };
                        _active.Add(job, execution); starting.Add(execution);
                    }
                    // Close admission before releasing the lock, without exposing an idle session early.
                    if (_pending.Count == 0 && _active.Count == 0) { _accepting = false; break; }
                    wait = _wake.Task;
                }
                foreach (var execution in starting) executions.Add(Task.Run(() => ExecuteAsync(execution)));
                await wait;
            }
            await Task.WhenAll(executions);
        }
        finally
        {
            lock (_gate) { _cts = null; _accepting = false; _pending.Clear(); }
            session.Dispose();
        }
    }

    private static string Lane(Job job) => job.Options.Orientation is not null ? "orientation" : job.FeatureId;
    private static string[] JobFiles(Job job) => job.Inputs.Append(job.Output)
        .Concat(job.Options.Rename?.Plan.SelectMany(item => new[] { item.Source, item.Target }) ?? []).ToArray();
    private static bool Overlap(IEnumerable<string> left, IEnumerable<string> right) => left.Any(first => right.Any(second =>
    {
        if (!Path.IsPathFullyQualified(first) || !Path.IsPathFullyQualified(second)) return false;
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(a, b, comparison) || a.StartsWith(b + Path.DirectorySeparatorChar, comparison)
            || b.StartsWith(a + Path.DirectorySeparatorChar, comparison);
    }));
    private bool FilesReserved(Job job) => _fileChanges.Values.Any(paths => Overlap(paths, JobFiles(job)));
    private bool MutationConflict(Job job) => _active.Keys.Any(active => (job.FeatureId == "batch-rename" || active.FeatureId == "batch-rename")
        && Overlap(JobFiles(job), JobFiles(active)));

    public IDisposable ReserveFiles(IEnumerable<string> paths)
    {
        var files = paths.Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).Distinct(BatchRename.PathComparer).ToArray();
        var id = Guid.NewGuid();
        lock (_gate)
        {
            if (_active.Keys.Any(job => Overlap(files, JobFiles(job))) || _fileChanges.Values.Any(other => Overlap(files, other)))
                throw new InvalidOperationException("部分文件仍被任务使用，请完成或终止相关任务后再修改文件。");
            _fileChanges.Add(id, files);
        }
        return new FileReservation(() => { lock (_gate) { _fileChanges.Remove(id); WakeLocked(); } });
    }

    private sealed class FileReservation(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private async Task ExecuteAsync(Execution execution)
    {
        var job = execution.Job; var token = execution.Cancellation.Token;
        using var context = execution.Control.Enter();
        var estimator = new ProgressEstimator(); var started = _time.GetTimestamp();
        void Publish(double value)
        {
            lock (execution.Gate)
            {
                if (!execution.Active || execution.Stopping || execution.Control.IsPaused || token.IsCancellationRequested || !double.IsFinite(value)) return;
                job.Progress = Math.Clamp(value, 0, 100);
                if (job.FeatureId != "download") job.Estimate = job.Activity is null ? estimator.Update(job.Progress, _time.GetElapsedTime(started)) : null;
            }
            Changed?.Invoke(job);
        }
        void Finish(JobState state, string error = "")
        {
            lock (execution.Gate)
            {
                if (!job.FileChangesCommitted && (execution.Stopping || token.IsCancellationRequested))
                { if (state != JobState.Cancelled) error = "用户停止了任务。"; state = JobState.Cancelled; }
                execution.Active = false; job.Error = error; job.Estimate = null; job.DownloadSpeed = null;
                if (state == JobState.Completed) job.Progress = 100;
                job.State = state;
                if (job.Activity is { } activity) job.Activity = activity with { UpdatedUtc = DateTime.UtcNow, State = state switch
                { JobState.Completed => AiActivityState.Completed, JobState.Cancelled => AiActivityState.Cancelled, _ => AiActivityState.Failed } };
            }
        }
        async Task<string> PreserveResultsAsync()
        {
            try { await AiPartialResults.SaveAvailableAsync(job).ConfigureAwait(false); return ""; }
            catch (Exception error) { return "保存已有结果失败：" + error.Message; }
        }
        try
        {
            lock (execution.Gate)
            {
                token.ThrowIfCancellationRequested();
                if (execution.Stopping) throw new OperationCanceledException(token);
                job.Activity = null; job.Progress = 0; job.Estimate = null; job.DownloadSpeed = null;
                job.FileChangesCommitted = false; job.OutputIsPartial = false;
                job.State = execution.Control.IsPaused ? JobState.Paused : JobState.Running; job.Error = ""; job.ProgressDetail = ""; job.Log = "";
            }
            Publish(0);
            using var timer = _time.CreateTimer(_ => Publish(job.Progress), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            await execution.Control.WaitAsync(token).ConfigureAwait(false);
            await engine.Execute(job, Publish, token).ConfigureAwait(false);
            while (true)
            {
                // A late native result must not silently resume a paused task.
                await execution.Control.WaitAsync(job.FileChangesCommitted ? CancellationToken.None : token).ConfigureAwait(false);
                lock (execution.Gate)
                {
                    if (execution.Control.IsPaused) continue;
                    if (!job.FileChangesCommitted) token.ThrowIfCancellationRequested();
                    Finish(JobState.Completed); break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            var error = await PreserveResultsAsync();
            Finish(JobState.Cancelled, error.Length == 0 ? "用户停止了任务。" : "用户停止了任务。\n" + error);
        }
        catch (Exception error)
        {
            var message = error.Message;
            var saveError = await PreserveResultsAsync();
            if (saveError.Length > 0) message += "\n" + saveError;
            try { job.AppendLog(error.ToString()); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { message += "\n" + logError.Message; }
            Finish(JobState.Failed, message);
        }
        finally
        {
            lock (_gate) { _active.Remove(job); WakeLocked(); }
            execution.Cancellation.Dispose(); Changed?.Invoke(job);
        }
    }

    public bool Stop(Job job)
    {
        Execution? execution;
        lock (_gate)
        {
            if (!_active.TryGetValue(job, out execution))
            {
                if (job.State is not (JobState.Waiting or JobState.Paused)) return false;
                _pending.Remove(job); job.State = JobState.Cancelled; job.Error = "用户停止了任务。"; WakeLocked();
            }
        }
        if (execution is not null) Cancel(execution);
        else Changed?.Invoke(job);
        return true;
    }

    private void Cancel(Execution execution)
    {
        lock (execution.Gate)
        {
            if (!execution.Active || execution.Stopping) return;
            execution.Stopping = true; execution.Job.State = JobState.Stopping;
            execution.Job.Estimate = null; execution.Job.DownloadSpeed = null;
            execution.Control.Resume();
        }
        _ = CancelAsync(execution.Cancellation);
        Changed?.Invoke(execution.Job);
    }

    private static async Task CancelAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Task cancellation: {0}", error); }
    }

    public void Stop()
    {
        CancellationTokenSource? session;
        Execution[] active;
        lock (_gate)
        {
            // Unstarted tasks remain waiting and can be started again after the session stops.
            _pending.Clear(); _accepting = false; session = _cts;
            active = _active.Values.ToArray(); WakeLocked();
        }
        foreach (var execution in active) Cancel(execution);
        if (session is not null) _ = CancelAsync(session);
    }

    public bool Pause(Job job)
    {
        Execution? execution;
        lock (_gate)
        {
            if (!_active.TryGetValue(job, out execution))
            {
                if (job.State != JobState.Waiting) return false;
                _pending.Remove(job); job.State = JobState.Paused; WakeLocked();
            }
        }
        if (execution is not null)
        {
            lock (execution.Gate)
            {
                if (!execution.Active || execution.Stopping || execution.Control.IsPaused) return false;
                execution.Control.Pause(); job.State = JobState.Paused;
            }
            lock (_gate) WakeLocked();
        }
        Changed?.Invoke(job); return true;
    }

    public bool Resume(Job job)
    {
        Execution? execution;
        lock (_gate)
            if (!_accepting || !_active.TryGetValue(job, out execution)) return false;
        lock (execution.Gate)
        {
            if (!execution.Active || execution.Stopping || !execution.Control.IsPaused) return false;
            execution.Control.Resume(); job.State = JobState.Running;
        }
        lock (_gate) WakeLocked();
        Changed?.Invoke(job); return true;
    }

    public bool Withdraw(Job job)
    {
        lock (_gate)
        {
            if (_active.ContainsKey(job)) return false;
            _pending.Remove(job); WakeLocked(); return true;
        }
    }

    public void ReorderPending(IEnumerable<Job> order)
    {
        var ranks = order.Select((job, index) => (job, index)).ToDictionary(pair => pair.job, pair => pair.index);
        lock (_gate) _pending.Sort((left, right) => ranks.GetValueOrDefault(left, int.MaxValue).CompareTo(ranks.GetValueOrDefault(right, int.MaxValue)));
    }

    private sealed class Execution(Job job, CancellationToken session)
    {
        public Job Job { get; } = job;
        public object Gate { get; } = new();
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(session);
        public JobExecutionControl Control { get; } = new();
        public bool Active = true;
        public bool Stopping;
    }
}
