using System.Diagnostics;
using System.Text;

namespace AvaMedia.Core;
public sealed record ProcessResult(int ExitCode,string Output,string Error);
public static class ProcessRunner
{
    public static Process Start(string executable,IEnumerable<string> arguments,bool input=false)
    {
        var p=new Process {StartInfo=new ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=input,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8}};
        foreach(var arg in arguments) p.StartInfo.ArgumentList.Add(arg);
        p.Start();return p;
    }
    public static Task<Process> StartAsync(string executable,IEnumerable<string> arguments,CancellationToken ct=default,bool input=false)
    {
        var snapshot=arguments.ToArray();
        return Task.Run(()=>Start(executable,snapshot,input),ct);
    }
    public static async Task<ProcessResult> Run(string executable,IEnumerable<string> args,CancellationToken ct=default,Action<string>? line=null,int maximumOutputChars=160000)
    {
        using var process=await StartAsync(executable,args,ct).ConfigureAwait(false);
        using var reg=ct.Register(()=>{try {process.Kill(true);} catch(InvalidOperationException) {} catch(System.ComponentModel.Win32Exception) {}});
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
        ct.ThrowIfCancellationRequested();return new(process.ExitCode,output.ToString(),error.ToString());
    }
}
