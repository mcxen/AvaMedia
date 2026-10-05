namespace AvaMedia.Core;
public sealed class QueueService(MediaEngine engine)
{
    private CancellationTokenSource? _cts;
    public bool IsRunning => _cts is not null;
    public event Action<Job>? Changed;
    public async Task Run(IEnumerable<Job> jobs,int concurrency)
    {
        if(IsRunning)return;_cts=new();var token=_cts.Token;
        using var semaphore=new SemaphoreSlim(Math.Clamp(concurrency,1,8));
        var snapshot=jobs.Where(j=>j.State==JobState.Waiting).ToArray();
        try
        {
            await Task.WhenAll(snapshot.Select(async job=>
            {
                try {await semaphore.WaitAsync(token);} catch(OperationCanceledException){return;}
                try
                {
                    job.State=JobState.Running;job.Error="";Changed?.Invoke(job);
                    await engine.Execute(job,p=>{job.Progress=p;Changed?.Invoke(job);},token);
                    job.State=JobState.Completed;
                }
                catch(OperationCanceledException){job.State=JobState.Cancelled;job.Error="用户停止了任务。";}
                catch(Exception e){job.State=JobState.Failed;job.Error=e.Message;}
                finally {Changed?.Invoke(job);semaphore.Release();}
            }));
        }
        finally {_cts.Dispose();_cts=null;}
    }
    public void Stop() => _cts?.Cancel();
}
