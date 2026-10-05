using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AvaMedia.Core;
using NAudio.Wave;

namespace AvaMedia.Desktop;
internal sealed class Playback : IDisposable
{
    private readonly IMediaEngine _engine;
    private readonly string _path;
    private CancellationTokenSource? _cts;
    private Task? _decode;
    private IAudioOutput? _audio;
    private WaveFileReader? _reader;
    private string? _wave;
    private bool _disposed;
    private readonly SemaphoreSlim _gate=new(1,1);
    private bool _muted;
    private int _videoStreamIndex, _audioStreamIndex;
    public WriteableBitmap Frame {get;private set;}=new(new PixelSize(960,540),new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Opaque);
    public event Action<double>? Updated;
    public event Action? Finished;
    public event Action<string>? Error;
    public bool IsPlaying=>_cts is not null;
    public int DecodedFrames{get;private set;}
    public bool Muted {get=>_muted;set{_muted=value;if(_audio is not null)_audio.Volume=value?0:1;}}
    public Playback(IMediaEngine engine,string path){_engine=engine;_path=path;}
    public void SetStreams(int video,int audio){_videoStreamIndex=video;_audioStreamIndex=audio;}
    public void SetVideoSize(int width,int height)
    {
        var scale=Math.Min(960d/Math.Max(1,width),540d/Math.Max(1,height));var size=new PixelSize(Math.Max(2,(int)(width*scale)/2*2),Math.Max(2,(int)(height*scale)/2*2));
        var old=Frame;Frame=new(size,new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Opaque);old.Dispose();
    }
    public async Task PrepareAudio(CancellationToken ct)
    {
        if(_wave is not null)try{File.Delete(_wave);}catch(IOException){}
        var cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AvaMedia","Preview");Directory.CreateDirectory(cache);_wave=Path.Combine(cache,Guid.NewGuid()+".wav");
        try{var result=await ProcessRunner.Run(_engine.FFmpeg,["-v","error","-n","-i",_path,"-map",$"0:a:{_audioStreamIndex}","-vn","-c:a","pcm_s16le","-ac","2","-ar","44100",_wave],ct);if(result.ExitCode!=0)throw new IOException(result.Error);}
        finally{if(_disposed && _wave is not null)try{File.Delete(_wave);}catch(IOException){}}
    }
    public async Task Play(double seconds,bool video,double end)
    {
        if(!double.IsFinite(seconds) || !double.IsFinite(end) || seconds<0 || end<=seconds)throw new ArgumentException("播放区间无效。");
        await _gate.WaitAsync();try{await StopCore();if(_disposed)return;_cts=new();var token=_cts.Token;
        var clock=new Stopwatch();var streamIndex=_videoStreamIndex;
        if(_wave is not null && File.Exists(_wave))
        {
            try{_reader=new(_wave);_reader.CurrentTime=TimeSpan.FromSeconds(Math.Min(seconds,_reader.TotalTime.TotalSeconds));_audio=AudioOutput.Create(_reader,message=>Dispatcher.UIThread.Post(()=>{if(!_disposed&&!token.IsCancellationRequested)Error?.Invoke(message);}));_audio.Volume=Muted?0:1;}catch(Exception ex){_audio?.Dispose();_audio=null;_reader?.Dispose();_reader=null;Error?.Invoke("声音预览不可用："+ex.Message);}
        }
        void StartClock(){if(clock.IsRunning)return;clock.Start();try{_audio?.Play();}catch(Exception ex){Error?.Invoke("声音预览不可用："+ex.Message);}}
        _decode=Task.Run(async()=>
        {
            try
            {
                if(video)
                {
                    var width=Frame.PixelSize.Width;var height=Frame.PixelSize.Height;
                    using var p=ProcessRunner.Start(_engine.FFmpeg,["-v","error","-ss",MediaEngine.Number(seconds),"-i",_path,"-map",$"0:v:{streamIndex}","-an","-vf",$"fps=25:start_time=0,scale={width}:{height}","-pix_fmt","bgra","-t",MediaEngine.Number(end-seconds),"-f","rawvideo","pipe:1"]);
                    using var registration=token.Register(()=>{try{p.Kill(true);}catch(InvalidOperationException){}});var errors=p.StandardError.ReadToEndAsync();var data=new byte[width*height*4];
                    var frameIndex=0L;
                    while(!token.IsCancellationRequested)
                    {
                        int offset=0;while(offset<data.Length){var read=await p.StandardOutput.BaseStream.ReadAsync(data.AsMemory(offset),token);if(read==0)break;offset+=read;}if(offset<data.Length)break;
                        var position=seconds+frameIndex++/25d;if(position>=end-.0000001)continue;
                        var wait=position-seconds-clock.Elapsed.TotalSeconds;if(clock.IsRunning&&wait>0)await Task.Delay(TimeSpan.FromSeconds(wait),token);
                        await Dispatcher.UIThread.InvokeAsync(()=>{if(_disposed || token.IsCancellationRequested)return;StartClock();using(var buffer=Frame.Lock())for(int row=0;row<height;row++)Marshal.Copy(data,row*width*4,buffer.Address+row*buffer.RowBytes,width*4);DecodedFrames++;Updated?.Invoke(position);});
                    }
                    await p.WaitForExitAsync(token);var error=await errors;if(p.ExitCode!=0)throw new IOException(error);
                }
                // Preserve the last video frame when audio/container duration extends beyond video EOF.
                await Dispatcher.UIThread.InvokeAsync(()=>{if(!_disposed&&!token.IsCancellationRequested)StartClock();});
                while(!token.IsCancellationRequested)
                {
                    var remaining=end-seconds-clock.Elapsed.TotalSeconds;if(remaining<=0)break;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(.04,remaining)),token);
                    await Dispatcher.UIThread.InvokeAsync(()=>{if(!_disposed&&!token.IsCancellationRequested)Updated?.Invoke(Math.Min(end,seconds+clock.Elapsed.TotalSeconds));});
                }
                await Dispatcher.UIThread.InvokeAsync(()=>{if(!_disposed&&!token.IsCancellationRequested)Updated?.Invoke(end);});
                if(!token.IsCancellationRequested)Dispatcher.UIThread.Post(()=>{if(!_disposed&&!token.IsCancellationRequested)Finished?.Invoke();});
            }
            catch(OperationCanceledException){}
            catch(Exception ex){if(!token.IsCancellationRequested)Dispatcher.UIThread.Post(()=>{if(_disposed||token.IsCancellationRequested)return;Error?.Invoke(ex.Message);Finished?.Invoke();});}
        });
        }finally{_gate.Release();}
    }
    public async Task Stop()
    {
        await _gate.WaitAsync();try{await StopCore();}finally{_gate.Release();}
    }
    private async Task StopCore(){var cts=_cts;_cts=null;var decode=_decode;_decode=null;cts?.Cancel();_audio?.Stop();_audio?.Dispose();_audio=null;_reader?.Dispose();_reader=null;if(decode is not null)try{await decode;}catch(OperationCanceledException){}cts?.Dispose();}
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_cts?.Cancel();_audio?.Stop();_audio?.Dispose();_audio=null;_reader?.Dispose();_reader=null;
        _=Stop();
        // The decoder checks _disposed on the UI thread before accessing the bitmap.
        Frame.Dispose();if(_wave is not null)try{File.Delete(_wave);}catch(IOException){}
    }
}
