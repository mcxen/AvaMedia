using AvaMedia.Core;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
var executor = new ControlledExecutor();
var queue = new QueueService(executor);
var jobs = new[] { new Job(), new Job() };
var observed = new List<JobState>();
queue.Changed += job => { lock (observed) observed.Add(job.State); };
var run = queue.Run(jobs, 2);
await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(queue.IsRunning && jobs.All(j => j.State == JobState.Running), "A substituted executor receives the real queue jobs");
executor.Release.SetResult(); await run;
Check(jobs.All(j => j.State == JobState.Completed && j.Progress == 100), "Completion and progress cross the execution interface");
Check(!queue.IsRunning && observed.Contains(JobState.Running) && observed.Contains(JobState.Completed), "Queue events and lifecycle survive backend substitution");
var failing = new QueueService(new FailingExecutor()); var rejected = new Job();
await failing.Run([rejected], 1);
Check(rejected.State == JobState.Failed && rejected.Error == "codec failure", "Backend errors reach the queue unchanged");
executor = new ControlledExecutor(); queue = new QueueService(executor); var cancelled = new[] { new Job(), new Job() };
run = queue.Run(cancelled, 2); await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); queue.Stop(); await run;
Check(cancelled.All(j => j.State == JobState.Cancelled) && !queue.IsRunning, "Stop propagates cancellation through the execution interface");
Check(typeof(IMediaEngine).IsAssignableFrom(typeof(MediaEngine)) && typeof(IMediaPreview).IsAssignableFrom(typeof(IMediaEngine)), "The production engine implements the shared inspection, preview and execution contracts");
Console.WriteLine($"Verified {checks} interface checks.");

sealed class ControlledExecutor : IJobExecutor
{
    private int _started;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task Execute(Job job, Action<double> progress, CancellationToken ct)
    {
        if (Interlocked.Increment(ref _started) == 2) Started.SetResult();
        await Release.Task.WaitAsync(ct); progress(100);
    }
}
sealed class FailingExecutor : IJobExecutor
{
    public Task Execute(Job job, Action<double> progress, CancellationToken ct) => throw new InvalidOperationException("codec failure");
}
