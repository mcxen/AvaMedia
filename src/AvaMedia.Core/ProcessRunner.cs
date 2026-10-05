using System.Diagnostics;
using System.Text;

namespace AvaMedia.Core;
public sealed record ProcessResult(int ExitCode,string Output,string Error);
public static class ProcessRunner
{
    public static Process Start(string executable,IEnumerable<string> arguments,bool input=false)
    {
        var p=new Process {StartInfo=new ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=input,StandardErrorEncoding=Encoding.UTF8}};
        foreach(var arg in arguments) p.StartInfo.ArgumentList.Add(arg);
        p.Start();return p;
    }
    public static async Task<ProcessResult> Run(string executable,IEnumerable<string> args,CancellationToken ct=default,Action<string>? line=null)
    {
        using var process=Start(executable,args);
        using var reg=ct.Register(()=>{try {process.Kill(true);} catch(InvalidOperationException) {}});
        var error=new StringBuilder();var output=new StringBuilder();
        async Task Read(StreamReader reader,StringBuilder buffer,bool callback)
        {
            while(await reader.ReadLineAsync() is {} text) {if(buffer.Length>160000) buffer.Remove(0,80000);buffer.AppendLine(text);if(callback) line?.Invoke(text);}
        }
        await Task.WhenAll(Read(process.StandardOutput,output,true),Read(process.StandardError,error,false),process.WaitForExitAsync(CancellationToken.None));
        ct.ThrowIfCancellationRequested();return new(process.ExitCode,output.ToString(),error.ToString());
    }
}
