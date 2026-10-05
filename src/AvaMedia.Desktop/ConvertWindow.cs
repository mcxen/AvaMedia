using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Core;

namespace AvaMedia.Desktop;
public sealed record ConversionRequest(Feature Feature,string[] Files,string OutputFolder,ConversionOptions Options,
    IReadOnlyList<QuickClipInput>? ClipInputs=null,bool OutputToSource=false,string SettingName="",IReadOnlyList<ConversionOptions>? InputOptions=null);
public sealed class ConvertWindow : Window
{
    private readonly ObservableCollection<string> _files;
    private ConversionOptions _options;
    private readonly Dictionary<string,ConversionOptions> _fileOptions=new();
    public ConvertWindow(MediaEngine engine,Feature feature,string outputFolder,string[] files,ConversionOptions? initialOptions=null,IReadOnlyList<ConversionOptions>? inputOptions=null)
    {
        Title=feature.Label.Replace("\n"," ");Width=830;Height=620;MinWidth=650;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        _files=new(files);_options=initialOptions?.Clone()??new(){Format=feature.Format};if(initialOptions is null){if(feature.Id=="repair")_options.CopyStreams=true;if(feature.Operation==Operation.SplitVideo)_options.VideoCodec="copy";if(feature.Operation==Operation.Optimize)_options.Quality=32;}if(inputOptions is not null)for(int i=0;i<Math.Min(files.Length,inputOptions.Count);i++)_fileOptions[files[i]]=inputOptions[i].Clone();
        var panel=new Grid{RowDefinitions=new("Auto,Auto,*,Auto,Auto,Auto"),Margin=new(18)};
        var top=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};var formats=Ui.Combo(GetFormats(feature),_options.Format);formats.Width=130;
        top.Children.Add(Ui.Text("输出格式"));top.Children.Add(formats);
        var kind=feature.Operation==Operation.Frames?MediaOptionsKind.Frames:feature.Operation is Operation.Record or Operation.SplitVideo?MediaOptionsKind.VideoOnly:feature.Category=="音频" || feature.Operation==Operation.SplitAudio?MediaOptionsKind.Audio:feature.Category=="图片"?MediaOptionsKind.Image:MediaOptionsKind.Video;
        var media=feature.Category is "视频" or "音频" or "图片" && feature.Operation!=Operation.ImagesPdf || feature.Operation is Operation.Mux or Operation.SplitVideo or Operation.Join || feature.Id=="repair";
        var setting=new Button{Content="输出配置",MinWidth=130,IsVisible=media};setting.Click+=async(_,_)=>
        {
            _options.Format=(string?)formats.SelectedItem??feature.Format;var dialog=new OptionsWindow(_options,kind:kind,allowAllAudioStreams:feature.Operation is not (Operation.Join or Operation.AudioMix or Operation.Record));var changed=await dialog.ShowDialog<ConversionOptions?>(this);
            if(changed is not null){_options=changed;if(feature.Operation is not (Operation.Join or Operation.AudioMix))foreach(var path in _fileOptions.Keys.ToArray()){var old=_fileOptions[path];var next=changed.Clone();next.Start=old.Start;next.End=old.End;next.CropX=old.CropX;next.CropY=old.CropY;next.CropWidth=old.CropWidth;next.CropHeight=old.CropHeight;next.DelogoX=old.DelogoX;next.DelogoY=old.DelogoY;next.DelogoWidth=old.DelogoWidth;next.DelogoHeight=old.DelogoHeight;_fileOptions[path]=next;}}
        };top.Children.Add(setting);
        formats.SelectionChanged+=(_,_)=>{var format=(string?)formats.SelectedItem??feature.Format;if(format!=_options.Format){_options.Format=format;_options.VideoCodec=_options.AudioCodec="自动";foreach(var option in _fileOptions.Values){option.Format=format;option.VideoCodec=option.AudioCodec="自动";}}};
        var mode=Ui.Combo(["视频合并","混流：视频 + 音频"],"视频合并");if(feature.Id=="join"){mode.Width=200;top.Children.Add(mode);}panel.Children.Add(top);
        var toolbar=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10,Margin=new(0,15,0,10)};
        var list=new ListBox{ItemsSource=_files,SelectionMode=SelectionMode.Multiple,Background=Brushes.White,BorderBrush=Brush.Parse("#BBBBBB"),BorderThickness=new(1)};
        var add=new Button{Content="添加文件…"};add.Click+=async(_,_)=>{foreach(var path in await Ui.Pick(this,"添加文件"))_files.Add(path);};toolbar.Children.Add(add);
        var folder=new Button{Content="添加文件夹…"};folder.Click+=async(_,_)=>{if(await Ui.Folder(this,"添加文件夹") is {} path){foreach(var file in Directory.EnumerateFiles(path))_files.Add(file);}};toolbar.Children.Add(folder);
        toolbar.Children.Add(Ui.Button("移除",()=>{foreach(var x in list.SelectedItems?.Cast<string>().ToArray()??[])_files.Remove(x);}));
        toolbar.Children.Add(Ui.Button("上移",()=>{if(list.SelectedIndex>0){var index=list.SelectedIndex;_files.Move(index,index-1);list.SelectedIndex=index-1;}}));
        toolbar.Children.Add(Ui.Button("下移",()=>{if(list.SelectedIndex>=0 && list.SelectedIndex<_files.Count-1){var index=list.SelectedIndex;_files.Move(index,index+1);list.SelectedIndex=index+1;}}));
        var edit=new Button{Content="选项 / 剪辑"};edit.Click+=async(_,_)=>
        {
            if(list.SelectedItem is not string path && _files.Count==0)return;path=list.SelectedItem as string??_files[0];_options.Format=(string?)formats.SelectedItem??feature.Format;
            var inputEdit=feature.Operation is Operation.Join or Operation.AudioMix or Operation.Mux;
            var draft=_fileOptions.TryGetValue(path,out var saved)?saved.Clone():inputEdit?new ConversionOptions():_options.Clone();draft.Format=_options.Format;
            var audioInput=(feature.Operation==Operation.Mux || feature.Id=="join"&&mode.SelectedIndex==1)&&_files.IndexOf(path)>0;
            var w=new EditorWindow(engine,path,draft,audioInput?"input-audio":inputEdit?"input":feature.Id);var result=await w.ShowDialog<ConversionOptions?>(this);if(result is not null)_fileOptions[path]=result;
        };
        if(media && feature.Operation!=Operation.ImagesPdf)toolbar.Children.Add(edit);
        Grid.SetRow(toolbar,1);panel.Children.Add(toolbar);Grid.SetRow(list,2);panel.Children.Add(list);
        var special=Ui.Input();var recordDuration=Ui.Input("30");var recordScreen=Ui.Combo(["自动屏幕"],"自动屏幕");
        if(feature.Operation is Operation.Download or Operation.Record or Operation.IsoCopy)
        {
            list.IsVisible=false;toolbar.IsVisible=false;setting.IsVisible=feature.Operation==Operation.Record;
            var area=new StackPanel{Spacing=16,Margin=new(0,30,0,0)};
            if(feature.Operation==Operation.Download){area.Children.Add(Ui.Text("视频地址 (HTTP / HTTPS)"));area.Children.Add(special);area.Children.Add(Ui.Text("通过已配置的 yt-dlp 下载，输出 MP4。"));}
            else if(feature.Operation==Operation.Record)
            {
                area.Children.Add(Ui.Text(OperatingSystem.IsMacOS()?"屏幕录像 · macOS 屏幕":"屏幕录像 · Windows 桌面"));area.Children.Add(Ui.Text("录制时长 (秒)"));area.Children.Add(recordDuration);
                if(OperatingSystem.IsMacOS())
                {
                    area.Children.Add(recordScreen);var refresh=new Button{Content="刷新可录制屏幕"};area.Children.Add(refresh);refresh.Click+=async(_,_)=>
                    {
                        try{var devices=await ProcessRunner.Run(engine.FFmpeg,["-hide_banner","-f","avfoundation","-list_devices","true","-i",""]);var screens=ScreenCapture.MacScreens(devices.Error);recordScreen.ItemsSource=new[]{"自动屏幕"}.Concat(screens.Select(s=>$"{s.Index}:none | {s.Name}")).ToArray();recordScreen.SelectedIndex=0;if(screens.Count==0)await Ui.Message(this,"未找到屏幕",ScreenCapture.MacPermissionMessage(devices.Error));}catch(Exception ex){await Ui.Message(this,"屏幕列表读取失败",ex.Message);}
                    };
                }
                area.Children.Add(new TextBlock{Text="确定后加入队列，点击主窗口“开始”录制屏幕。\n当前录制画面，不包含系统声音。停止会保留已生成的片段。"+(OperatingSystem.IsMacOS()?"\n首次录制需授予屏幕录制权限，授权后重启应用。":""),TextWrapping=TextWrapping.Wrap});
            }
            else {area.Children.Add(Ui.Text(OperatingSystem.IsMacOS()?"光驱原始设备路径，例如 /dev/rdisk2":"光驱盘符或原始设备路径，例如 D:"));area.Children.Add(special);area.Children.Add(new TextBlock{Text="逐字节复制可读数据光盘为 ISO。需要本机读取权限，不处理加密。",TextWrapping=TextWrapping.Wrap});}
            Grid.SetRow(area,2);panel.Children.Add(area);
        }
        var output=new Grid{ColumnDefinitions=new("95,*,90"),Margin=new(0,14,0,6)};var outputBox=Ui.Input(outputFolder);output.Children.Add(Ui.Text("输出文件夹"));Grid.SetColumn(outputBox,1);output.Children.Add(outputBox);
        var browse=new Button{Content="浏览…",Margin=new(10,0,0,0)};browse.Click+=async(_,_)=>{if(await Ui.Folder(this,"选择输出目录") is {} path)outputBox.Text=path;};Grid.SetColumn(browse,2);output.Children.Add(browse);Grid.SetRow(output,3);panel.Children.Add(output);
        var note=new TextBlock{FontSize=12,Foreground=Brush.Parse("#666666"),TextWrapping=TextWrapping.Wrap,Margin=new(0,8)};
        note.Text=feature.Operation switch{Operation.Join=>"按列表顺序合并。每个文件可独立剪辑。",Operation.SplitAudio=>"提取音轨。",Operation.Frames=>"按设置的间隔导出 PNG 帧。",_=>"确定后加入主窗口队列。"};Grid.SetRow(note,4);panel.Children.Add(note);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=16,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,12,0,0)};
        buttons.Children.Add(Ui.Button("取消",()=>Close(null),120));var ok=new Button{Content="✓ 确定",Width=140};ok.Click+=async(_,_)=>
        {
            try
            {
                _options.Format=(string?)formats.SelectedItem??feature.Format;var selected=feature;string[] inputs=_files.ToArray();
                if(feature.Id=="join" && mode.SelectedIndex==1)selected=Catalog.Find("mux");
                if(feature.Operation is Operation.Download or Operation.IsoCopy)inputs=[special.Text??""];
                if(feature.Operation==Operation.Record){inputs=[];_options.RecordSeconds=double.Parse(recordDuration.Text??"",CultureInfo.InvariantCulture);if(OperatingSystem.IsMacOS())_options.RecordSource=recordScreen.SelectedIndex<=0?"desktop":((string)recordScreen.SelectedItem!).Split('|')[0].Trim();}
                var folderPath=Path.GetFullPath(outputBox.Text??"");var name=Path.Combine(folderPath,"validation."+(_options.Format.Length>0?_options.Format:"out"));
                var perInput=_files.Select(path=>{var option=_fileOptions.TryGetValue(path,out var item)?item.Clone():feature.Operation is Operation.Join or Operation.AudioMix?new ConversionOptions():_options.Clone();option.Format=_options.Format;return option;}).ToArray();
                MediaEngine.Validate(new(){FeatureId=selected.Id,Inputs=inputs,Output=name,Options=_options});Close(new ConversionRequest(selected,inputs,folderPath,_options,InputOptions:_fileOptions.Count>0?perInput:null));
            }
            catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}
        };buttons.Children.Add(ok);Grid.SetRow(buttons,5);panel.Children.Add(buttons);Content=panel;
    }
    private static IEnumerable<string> GetFormats(Feature f)=>f.Id=="other"?["avi","flv","mov","wmv","mpg","ts","mkv","mp4"]:f.Operation is Operation.Join or Operation.Mux or Operation.Optimize && f.Category=="视频"?["mp4","mkv","webm","avi","mov"]:f.Id=="split"?["m4a","mp3","flac","wav","aac","ogg"]:[f.Format];
}
