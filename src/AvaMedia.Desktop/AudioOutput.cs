using System.Runtime.InteropServices;
using NAudio.Wave;

namespace AvaMedia.Desktop;

internal interface IAudioOutput : IDisposable
{
    float Volume { set; }
    void Play();
    void Stop();
}

internal static class AudioOutput
{
    public static IAudioOutput Create(WaveFileReader reader, Action<string> error) =>
        OperatingSystem.IsMacOS() ? new MacAudioOutput(reader,error) :
        OperatingSystem.IsWindows() ? new WindowsAudioOutput(reader) :
        throw new PlatformNotSupportedException("声音预览支持 Windows 和 macOS。");

    private sealed class WindowsAudioOutput : IAudioOutput
    {
        private readonly WaveOutEvent _output=new();
        public WindowsAudioOutput(WaveFileReader reader){try{_output.Init(reader);}catch{_output.Dispose();throw;}}
        public float Volume { set=>_output.Volume=value; }
        public void Play()=>_output.Play();
        public void Stop()=>_output.Stop();
        public void Dispose()=>_output.Dispose();
    }
}

// AudioQueue owns its buffers; the caller owns the reader and disposes it after this output.
internal sealed class MacAudioOutput : IAudioOutput
{
    private const string Library="/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private readonly WaveFileReader _reader;
    private readonly Action<string> _error;
    private readonly QueueCallback _callback;
    private readonly object _sync=new();
    private readonly byte[] _samples;
    private IntPtr _queue;
    private bool _stopped;
    public MacAudioOutput(WaveFileReader reader,Action<string> error)
    {
        _reader=reader;_error=error;_callback=Fill;
        var wave=reader.WaveFormat;
        if(wave.Encoding!=WaveFormatEncoding.Pcm || wave.BitsPerSample!=16)throw new ArgumentException("macOS 预览需要交错的 16 位 PCM。");
        _samples=new byte[wave.AverageBytesPerSecond/10/wave.BlockAlign*wave.BlockAlign];
        var format=new StreamDescription{SampleRate=wave.SampleRate,FormatId=0x6c70636d,FormatFlags=12,BytesPerPacket=(uint)wave.BlockAlign,FramesPerPacket=1,BytesPerFrame=(uint)wave.BlockAlign,ChannelsPerFrame=(uint)wave.Channels,BitsPerChannel=16};
        Check(AudioQueueNewOutput(ref format,_callback,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,0,out _queue),"创建音频队列");
        try
        {
            for(var i=0;i<3;i++){Check(AudioQueueAllocateBuffer(_queue,(uint)_samples.Length,out var buffer),"分配声音缓冲");Enqueue(_queue,buffer);}
        }
        catch{Dispose();throw;}
    }
    public float Volume { set { if(_queue!=IntPtr.Zero)Check(AudioQueueSetParameter(_queue,1,Math.Clamp(value,0,1)),"设置音量"); } }
    public void Play(){if(_queue!=IntPtr.Zero)Check(AudioQueueStart(_queue,IntPtr.Zero),"播放声音");}
    public void Stop()
    {
        lock(_sync)_stopped=true;
        if(_queue!=IntPtr.Zero)Check(AudioQueueStop(_queue,true),"停止声音");
    }
    public void Dispose()
    {
        lock(_sync)_stopped=true;
        var queue=_queue;_queue=IntPtr.Zero;
        // Synchronous disposal completes outstanding callbacks before the reader is released.
        if(queue!=IntPtr.Zero)AudioQueueDispose(queue,true);
        GC.KeepAlive(_callback);
    }
    private void Fill(IntPtr user,IntPtr queue,IntPtr buffer)
    {
        try
        {
            Enqueue(queue,buffer);
        }
        catch(Exception ex){lock(_sync)_stopped=true;_error(ex.Message);}
    }
    private void Enqueue(IntPtr queue,IntPtr buffer)
    {
        lock(_sync)
        {
            if(_stopped)return;
            var bytes=_reader.Read(_samples,0,_samples.Length);if(bytes==0)return;
            var native=Marshal.PtrToStructure<QueueBuffer>(buffer);
            Marshal.Copy(_samples,0,native.Data,bytes);
            Marshal.WriteInt32(buffer,(int)Marshal.OffsetOf<QueueBuffer>(nameof(QueueBuffer.Size)),bytes);
            Check(AudioQueueEnqueueBuffer(queue,buffer,0,IntPtr.Zero),"提交声音缓冲");
        }
    }
    private static void Check(int status,string action){if(status!=0)throw new IOException($"macOS {action}失败 (Core Audio {status})。");}
    [StructLayout(LayoutKind.Sequential)]private struct StreamDescription
    {
        public double SampleRate;
        public uint FormatId,FormatFlags,BytesPerPacket,FramesPerPacket,BytesPerFrame,ChannelsPerFrame,BitsPerChannel,Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]private struct QueueBuffer
    {
        public uint Capacity;
        public IntPtr Data;
        public uint Size;
        public IntPtr UserData;
        public uint PacketCapacity;
        public IntPtr Packets;
        public uint PacketCount;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]private delegate void QueueCallback(IntPtr user,IntPtr queue,IntPtr buffer);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]private static extern int AudioQueueNewOutput(ref StreamDescription format,QueueCallback callback,IntPtr user,IntPtr runLoop,IntPtr runLoopMode,uint flags,out IntPtr queue);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]private static extern int AudioQueueAllocateBuffer(IntPtr queue,uint size,out IntPtr buffer);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]private static extern int AudioQueueEnqueueBuffer(IntPtr queue,IntPtr buffer,uint descriptions,IntPtr packets);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]private static extern int AudioQueueSetParameter(IntPtr queue,uint parameter,float value);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]private static extern int AudioQueueStart(IntPtr queue,IntPtr time);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]private static extern int AudioQueueStop(IntPtr queue,[MarshalAs(UnmanagedType.I1)]bool immediate);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]private static extern int AudioQueueDispose(IntPtr queue,[MarshalAs(UnmanagedType.I1)]bool immediate);
}
