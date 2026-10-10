namespace AvaMedia.Core;

public sealed class QueueService(IJobExecutor engine, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly List<Job> _pending = [];
    private readonly Dictionary<Job, Execution> _active = [];
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
                    while (_accepting && _pending.Count > 0 && _active.Count < Math.Clamp(concurrency, 1, 8))
                    {
                        var job = _pending[0]; _pending.RemoveAt(0);
                        var execution = new Execution(job, session.Token);
                        _active.Add(job, execution); starting.Add(execution);
                    }
                    // Close admission before releasing the lock, without exposing an idle session early.
                    if (_pending.Count == 0 && _active.Count == 0) { _accepting = false; break; }
                    wait = _wake.Task;
                }
                foreach (var execution in starting) executions.Add(ExecuteAsync(execution));
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

    private async Task ExecuteAsync(Execution execution)
    {
        var job = execution.Job; var token = execution.Cancellation.Token;
        var estimator = new ProgressEstimator(); var started = _time.GetTimestamp();
        void Publish(double value)
        {
            lock (execution.Gate)
            {
                if (!execution.Active || execution.Stopping || token.IsCancellationRequested || !double.IsFinite(value)) return;
                job.Progress = Math.Clamp(value, 0, 100);
                if (job.FeatureId != "download") job.Estimate = job.Activity is null ? estimator.Update(job.Progress, _time.GetElapsedTime(started)) : null;
                Changed?.Invoke(job);
            }
        }
        void Finish(JobState state, string error = "")
        {
            lock (execution.Gate)
            {
                if (!job.FileChangesCommitted && (execution.Stopping || token.IsCancellationRequested)) { state = JobState.Cancelled; error = "用户停止了任务。"; }
                execution.Active = false; job.Error = error; job.Estimate = null; job.DownloadSpeed = null;
                if (state == JobState.Completed) job.Progress = 100;
                job.State = state;
                if (job.Activity is { } activity) job.Activity = activity with { UpdatedUtc = DateTime.UtcNow, State = state switch
                { JobState.Completed => AiActivityState.Completed, JobState.Cancelled => AiActivityState.Cancelled, _ => AiActivityState.Failed } };
            }
        }
        try
        {
            lock (execution.Gate)
            {
                token.ThrowIfCancellationRequested();
                if (execution.Stopping) throw new OperationCanceledException(token);
                job.Activity = null; job.Progress = 0; job.Estimate = null; job.DownloadSpeed = null;
                job.FileChangesCommitted = false;
                job.State = JobState.Running; job.Error = ""; job.ProgressDetail = ""; job.Log = "";
            }
            Publish(0);
            using var timer = _time.CreateTimer(_ => Publish(job.Progress), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            await engine.Execute(job, Publish, token);
            if (!job.FileChangesCommitted) token.ThrowIfCancellationRequested(); Finish(JobState.Completed);
        }
        catch (OperationCanceledException) { Finish(JobState.Cancelled, "用户停止了任务。"); }
        catch (Exception error)
        {
            var message = error.Message;
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
        }
        try { execution.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        Changed?.Invoke(execution.Job);
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
        try { session?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public bool PauseQueued(Job job)
    {
        lock (_gate)
        {
            if (_active.ContainsKey(job) || job.State != JobState.Waiting) return false;
            _pending.Remove(job); job.State = JobState.Paused; WakeLocked();
        }
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
        public bool Active = true;
        public bool Stopping;
    }
}
