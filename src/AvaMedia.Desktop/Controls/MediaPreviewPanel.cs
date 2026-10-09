using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaMedia.Core;

namespace AvaMedia.Desktop.Controls;

public sealed class MediaPreviewPanel : UserControl, IDisposable
{
    private readonly IMediaEngine _engine;
    private readonly ImageCompareView _view=new(){MinHeight=80,SideBySide=true};
    private readonly TextBlock _status=Ui.Text("选择文件以预览","caption");
    private readonly TextBlock _position=Ui.Text("","caption");
    private readonly Slider _seek=new(){Minimum=0,Maximum=1,IsVisible=false};
    private readonly AudioWaveform _waveform=new(){IsVisible=false};
    private string? _sourceKey;
    private readonly ListBox _archive=new(){IsVisible=false,MinHeight=150};
    private readonly Button _open;
    private readonly Button _listen;
    private readonly CancellationTokenSource _lifetime=new();
    private CancellationTokenSource? _work;
    private Bitmap? _sourceBitmap,_resultBitmap;
    private MediaInfo? _info;
    private string? _path;
    private ConversionOptions? _options;
    private bool _updating,_closed;
    private double _requestedPosition;
    private sealed record ArchiveItem(string Name,string Details);

    public MediaPreviewPanel(IMediaEngine engine)
    {
        _engine=engine;
        var root=new Grid{RowDefinitions=new("Auto,*,Auto,Auto"),RowSpacing=8};root.Children.Add(_status);
        var surface=new Grid();surface.Bind(Panel.BackgroundProperty,new DynamicResourceExtension("UiMediaSurface"));
        surface.Children.Add(_waveform);surface.Children.Add(_view);surface.Children.Add(_archive);Grid.SetRow(surface,1);root.Children.Add(surface);
        Grid.SetRow(_seek,2);root.Children.Add(_seek);
        var actions=new Grid{RowDefinitions=new("Auto,Auto"),RowSpacing=4};actions.Children.Add(_position);
        var buttons=new WrapPanel();Grid.SetRow(buttons,1);actions.Children.Add(buttons);
        _open=Ui.Button("打开预览",()=>{if(_path is null)return;var owner=TopLevel.GetTopLevel(this) as Window;if(owner is null)return;new PlayerWindow(engine,[_path]).ShowForPlayback(owner);});
        _open.IsEnabled=false;_open.Margin=new(0,0,6,0);buttons.Children.Add(_open);
        var listen=Ui.Button("试听处理效果",()=>{if(_path is not null&&_options is not null&&TopLevel.GetTopLevel(this) is Window owner)new VoicePreviewWindow(engine,_path,_options.Clone()).Show(owner);});
        listen.IsVisible=false;listen.Margin=new(0,0,6,0);buttons.Children.Add(listen);
        _listen=listen;
        var fit=Ui.Button("适应窗口",_view.ResetView);buttons.Children.Add(fit);
        Grid.SetRow(actions,3);root.Children.Add(actions);Content=root;
        Avalonia.Automation.AutomationProperties.SetName(_seek,"预览位置");
        _seek.PropertyChanged+=(_,change)=>{if(change.Property==Slider.ValueProperty&&!_updating&&_path is not null){_requestedPosition=_seek.Value;_=LoadAsync();}};
        _archive.ItemTemplate=new FuncDataTemplate<ArchiveItem>((item,_)=>
        {
            var row=new Grid{ColumnDefinitions=new("28,*,Auto"),ColumnSpacing=8,Margin=new(4)};
            row.Children.Add(new FeatureIcon{Kind="document",Width=22,Height=22});
            var name=Ui.Text(item?.Name??"");Localization.SetIsUserText(name,true);Grid.SetColumn(name,1);row.Children.Add(name);
            var details=Ui.Text(item?.Details??"","caption");Grid.SetColumn(details,2);row.Children.Add(details);return row;
        });
    }

    public void SetSource(string? path,ConversionOptions? options=null)
    {
        if(_closed)return;
        var stamp=path is not null&&File.Exists(path)?new FileInfo(path):null;
        var key=path+"|"+stamp?.Length+"|"+stamp?.LastWriteTimeUtc.Ticks+"|"+System.Text.Json.JsonSerializer.Serialize(options);
        if(key==_sourceKey)return;_sourceKey=key;
        var changed=_path!=path||_options?.VideoStreamIndex!=options?.VideoStreamIndex||_options?.AudioStreamIndex!=options?.AudioStreamIndex;_path=path;_options=options?.Clone();
        if(changed){_info=null;_requestedPosition=options?.Start??0;ClearBitmaps();_updating=true;_seek.Value=0;_updating=false;}
        if(path is null){_work?.Cancel();_status.Text=Localization.Text("选择文件以预览");_seek.IsVisible=_archive.IsVisible=false;_open.IsEnabled=_listen.IsVisible=_waveform.IsVisible=false;return;}
        _=LoadAsync();
    }

    public void SetPosition(double seconds)
    {
        if(_closed||_path is null||!double.IsFinite(seconds))return;
        _requestedPosition=Math.Max(0,seconds);_updating=true;_seek.Value=Math.Clamp(_requestedPosition,0,_seek.Maximum);_updating=false;_=LoadAsync();
    }

    private async Task LoadAsync()
    {
        _work?.Cancel();_work?.Dispose();_work=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token=_work.Token;var path=_path!;var options=_options?.Clone();var seconds=_requestedPosition;
        try
        {
            await Task.Delay(180,token);
            if(Path.GetExtension(path).Equals(".zip",StringComparison.OrdinalIgnoreCase)||Directory.Exists(path))
            {
                var items=await Task.Run(()=>ReadArchive(path,token),token);token.ThrowIfCancellationRequested();
                _view.IsVisible=_waveform.IsVisible=_listen.IsVisible=false;_archive.IsVisible=true;_archive.ItemsSource=items;_seek.IsVisible=false;_open.IsEnabled=false;
                Localization.SetText(_status,$"文件预览 · 显示 {items.Length} 项");return;
            }
            var info=_info??await _engine.Probe(path,token,options?.VideoStreamIndex??0,options?.AudioStreamIndex??0);token.ThrowIfCancellationRequested();_info=info;
            _archive.IsVisible=false;_waveform.IsVisible=!info.HasVideo&&info.HasAudio;_view.IsVisible=info.HasVideo;_listen.IsVisible=info.HasAudio&&options is not null;_open.IsEnabled=info.HasVideo||info.HasAudio;
            _updating=true;_seek.Maximum=Math.Max(0,info.Duration-.04);seconds=Math.Clamp(seconds,0,_seek.Maximum);_requestedPosition=seconds;_seek.Value=seconds;_seek.IsVisible=info.Duration>0;_updating=false;
            if(!info.HasVideo)
            {
                var scratch=Path.Combine(Path.GetTempPath(),"AvaMedia-waveform-"+Guid.NewGuid().ToString("N")+".pcm");
                try
                {
                    var audio=await ProcessRunner.Run(_engine.FFmpeg,["-v","error","-y","-ss",MediaEngine.Number(seconds),"-i",path,"-map","0:a:"+info.AudioStreamIndex,"-t","15","-ac","1","-ar","8000","-f","s16le",scratch],token);
                    if(audio.ExitCode!=0)throw new InvalidDataException(audio.Error);
                    var data=await File.ReadAllBytesAsync(scratch,token);token.ThrowIfCancellationRequested();_waveform.SetSamples(data);
                }
                finally{try{File.Delete(scratch);}catch(IOException){}catch(UnauthorizedAccessException){}}
                _status.Text=Localization.Text("音频波形 · 15 秒片段");Localization.SetText(_position,$"{MediaTime.Format(seconds)} / {MediaTime.Format(info.Duration)}");return;
            }
            var source=await _engine.Thumbnail(path,seconds,1200,900,token,pad:false,videoStreamIndex:info.VideoStreamIndex);
            var result=options is null?source:await MediaVisualPreview.RenderAsync(_engine,path,info,options,seconds,token);
            token.ThrowIfCancellationRequested();if(_closed||path!=_path)return;
            ClearBitmaps();_sourceBitmap=Decode(source);_resultBitmap=Decode(result);
            _view.Source=_sourceBitmap;_view.Result=_resultBitmap;_view.SideBySide=options is not null;
            _status.Text=Localization.Text(options is null?"原文件预览":"原画面 / 编辑后画面");
            Localization.SetText(_position,$"{info.Width} × {info.Height} · {MediaTime.Format(seconds)}");
        }
        catch(OperationCanceledException){}
        catch(Exception error){if(!token.IsCancellationRequested&&!_closed){Localization.SetText(_status,$"预览读取失败：{error.Message}");}}
    }

    private static ArchiveItem[] ReadArchive(string path,CancellationToken token)
    {
        if(Directory.Exists(path))return Directory.EnumerateFileSystemEntries(path).Take(1000).Select(file=>{token.ThrowIfCancellationRequested();return new ArchiveItem(Path.GetFileName(file),Directory.Exists(file)?Localization.Text("文件夹"):FormatSize(new FileInfo(file).Length));}).ToArray();
        using var archive=ZipFile.OpenRead(path);
        return archive.Entries.Take(1000).Select(entry=>{token.ThrowIfCancellationRequested();return new ArchiveItem(entry.FullName,FormatSize(entry.Length));}).ToArray();
    }
    private static string FormatSize(long size)=>$"{size/1024d:0.#} KB";
    private static Bitmap Decode(byte[] data){using var stream=new MemoryStream(data);return new Bitmap(stream);}
    private void ClearBitmaps(){_view.Source=_view.Result=null;_sourceBitmap?.Dispose();_resultBitmap?.Dispose();_sourceBitmap=_resultBitmap=null;}
    public void Dispose(){if(_closed)return;_closed=true;_lifetime.Cancel();_work?.Cancel();_work?.Dispose();ClearBitmaps();_lifetime.Dispose();}
}
