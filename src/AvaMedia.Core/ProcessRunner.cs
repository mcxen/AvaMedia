using System.Diagnostics;
using System.Text;

namespace AvaMedia.Core;
public sealed record ProcessResult(int ExitCode,string Output,string Error)
{ public bool Interrupted { get; init; } }
public static class ProcessRunner
{
    public static Process Start(string executable,IEnumerable<string> arguments,bool input=false)
    {
        var p=new Process {StartInfo=new ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=input,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8}};
        foreach(var arg in arguments) p.StartInfo.ArgumentList.Add(arg);
        p.Start();return p;
    }
    public static async Task<Process> StartAsync(string executable,IEnumerable<string> arguments,CancellationToken ct=default,bool input=false)
    {
        var snapshot=arguments.ToArray();
        await JobExecutionControl.CheckpointAsync(ct).ConfigureAwait(false);
        return await Task.Run(()=>Start(executable,snapshot,input),ct).ConfigureAwait(false);
    }
    public static async Task<ProcessResult> Run(string executable,IEnumerable<string> args,CancellationToken ct=default,Action<string>? line=null,int maximumOutputChars=160000,bool finalizeOnCancel=false)
    {
        var arguments=finalizeOnCancel?args.Where(argument=>argument!="-nostdin").ToArray():args.ToArray();
        using var process=await StartAsync(executable,arguments,ct,input:finalizeOnCancel).ConfigureAwait(false);
        Task cancellation=Task.CompletedTask;
        using var reg=ct.Register(()=>cancellation=StopAsync());
        async Task StopAsync()
        {
            try
            {
                if(finalizeOnCancel)
                {
                    await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                    var exit=process.WaitForExitAsync(CancellationToken.None);
                    if(await Task.WhenAny(exit,Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false)==exit)return;
                }
                if(!process.HasExited)process.Kill(true);
            }
            catch(Exception error) when(error is IOException or ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){} }
        }
        var error=new StringBuilder();var output=new StringBuilder();
        async Task Read(StreamReader reader,StringBuilder buffer,bool callback)
        {
            try
            {
                while(await reader.ReadLineAsync() is {} text) {if(buffer.Length>maximumOutputChars) buffer.Remove(0,buffer.Length/2);buffer.AppendLine(text);if(callback) line?.Invoke(text);}
            }
            catch
            {
                // A failing progress/parser callback must not stop draining stdout while the
                // process is still running: a full pipe would deadlock WaitForExitAsync.
                try{process.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
                throw;
            }
        }
        await Task.WhenAll(Read(process.StandardOutput,output,true),Read(process.StandardError,error,false),process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
        await cancellation.ConfigureAwait(false);
        var interrupted=finalizeOnCancel && ct.IsCancellationRequested && process.ExitCode==0;
        if(!interrupted)ct.ThrowIfCancellationRequested();
        return new(process.ExitCode,output.ToString(),error.ToString()){Interrupted=interrupted};
    }
}
