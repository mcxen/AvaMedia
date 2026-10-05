namespace AvaMedia.Core;
public sealed class QueueService(IJobExecutor engine, TimeProvider? timeProvider=null)
{
    private readonly TimeProvider _time=timeProvider??TimeProvider.System;
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
                var progressGate=new object();var active=true;
                var estimator=new ProgressEstimator();var started=_time.GetTimestamp();
                void Publish(double value)
                {
                    lock(progressGate)
                    {
                        if(!active || token.IsCancellationRequested || !double.IsFinite(value))return;
                        job.Progress=Math.Clamp(value,0,100);
                        if(job.FeatureId!="download")job.Estimate=estimator.Update(job.Progress,_time.GetElapsedTime(started));
                        Changed?.Invoke(job);
                    }
                }
                void Finish(JobState state,string error="")
                {lock(progressGate){active=false;job.Error=error;job.Estimate=null;if(state==JobState.Completed)job.Progress=100;job.State=state;}}
                try
                {
                    job.Progress=0;job.Estimate=null;job.State=JobState.Running;job.Error="";job.ProgressDetail="";Publish(0);
                    using var timer=_time.CreateTimer(_=>{lock(progressGate)Publish(job.Progress);},null,TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(1));
                    await engine.Execute(job,Publish,token);
                    token.ThrowIfCancellationRequested();Finish(JobState.Completed);
                }
                catch(OperationCanceledException){Finish(JobState.Cancelled,"用户停止了任务。");}
                catch(Exception e){Finish(JobState.Failed,e.Message);}
                finally {Changed?.Invoke(job);semaphore.Release();}
            }));
        }
        finally {_cts.Dispose();_cts=null;}
    }
    public void Stop() => _cts?.Cancel();
}
