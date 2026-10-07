using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;
public sealed record ConversionRequest(Feature Feature,string[] Files,string OutputFolder,ConversionOptions Options,
    IReadOnlyList<QuickClipInput>? ClipInputs=null,bool OutputToSource=false,string SettingName="",IReadOnlyList<ConversionOptions>? InputOptions=null);
public sealed class ConversionEntry(string path,ConversionOptions? options=null) : Observable
{
    public string Path { get; }=path;
    public ConversionOptions? Options { get; private set; }=options?.Clone();
    public void SetOptions(ConversionOptions options){Options=options.Clone();Raise(nameof(Summary));}
    public string Summary=>Options is {} option?Localization.Join(" · ", new[] { Localization.Format($"区间 {Time(option.Start)} → {(option.End>0?Time(option.End):Localization.Text("结尾"))}"), option.CropWidth>0?Localization.Format($"裁剪 {option.CropX},{option.CropY} {option.CropWidth} × {option.CropHeight}"):"", option.Speed!=1?$"{MediaEngine.Number(option.Speed)}×":"" }.Where(s=>s.Length>0)):"完整源文件";
    private static string Time(double seconds)=>MediaTime.Format(seconds);
    public override string ToString()=>Path;
}
public sealed class ConvertWindow : Window
{
    private readonly ObservableCollection<ConversionEntry> _entries;
    private ConversionOptions _options;
    private readonly CancellationTokenSource _lifetime=new();
    private bool _preparing;
    public ConvertWindow(IMediaEngine engine,Feature feature,string outputFolder,string[] files,ConversionOptions? initialOptions=null,IReadOnlyList<ConversionOptions>? inputOptions=null)
    {
        Title=feature.Label.Replace("\n"," ");Width=830;Height=620;MinWidth=650;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        WindowArtwork.SetKind(this, feature.Icon);
        Closed+=(_,_)=>_lifetime.Cancel();
        _entries=new(files.Select((path,index)=>new ConversionEntry(path,inputOptions?.ElementAtOrDefault(index))));_options=initialOptions?.Clone()??new(){Format=feature.Format};if(initialOptions is null){if(feature.Id=="repair")_options.CopyStreams=true;if(feature.Operation==Operation.SplitVideo)_options.VideoCodec="copy";}
        var panel=new Grid{RowDefinitions=new("Auto,Auto,*,Auto,Auto"),Margin=new(18)};
        var top=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10};var formats=Ui.Combo(GetFormats(feature),_options.Format);formats.Width=130;
        top.Children.Add(Ui.Text("输出格式"));top.Children.Add(formats);
        var kind=feature.Operation==Operation.Frames?MediaOptionsKind.Frames:feature.Operation==Operation.SplitVideo?MediaOptionsKind.VideoOnly:feature.Category=="音频" || feature.Operation==Operation.SplitAudio?MediaOptionsKind.Audio:feature.Category=="图片"?MediaOptionsKind.Image:MediaOptionsKind.Video;
        var media=feature.Category is "视频" or "音频" or "图片" && feature.Operation!=Operation.ImagesPdf || feature.Operation is Operation.Mux or Operation.SplitVideo or Operation.Join || feature.Id=="repair";
        var setting=new Button{Content="输出配置",MinWidth=130,IsVisible=media};setting.Click+=async(_,_)=>
        {
            _options.Format=(string?)formats.SelectedItem??feature.Format;var dialog=new OptionsWindow(_options,kind:kind,allowAllAudioStreams:feature.Operation is not (Operation.Join or Operation.AudioMix),imageQualityDefault:_options.Format=="jpg"?engine.Settings.JpegQuality:engine.Settings.WebpQuality);var changed=await dialog.ShowDialog<ConversionOptions?>(this);
            if(changed is not null){_options=changed;if(feature.Operation is not (Operation.Join or Operation.AudioMix or Operation.Mux))foreach(var entry in _entries.Where(e=>e.Options is not null)){var old=entry.Options!;var next=changed.Clone();next.Start=old.Start;next.End=old.End;next.CropX=old.CropX;next.CropY=old.CropY;next.CropWidth=old.CropWidth;next.CropHeight=old.CropHeight;next.DelogoX=old.DelogoX;next.DelogoY=old.DelogoY;next.DelogoWidth=old.DelogoWidth;next.DelogoHeight=old.DelogoHeight;entry.SetOptions(next);}}
        };top.Children.Add(setting);
        formats.SelectionChanged+=(_,_)=>{var format=(string?)formats.SelectedItem??feature.Format;if(format!=_options.Format){_options.Format=format;_options.VideoCodec=_options.AudioCodec="自动";foreach(var entry in _entries.Where(e=>e.Options is not null)){var option=entry.Options!.Clone();option.Format=format;option.VideoCodec=option.AudioCodec="自动";entry.SetOptions(option);}}};
        var mode=Ui.Combo(["视频合并","混流：视频 + 音频"],"视频合并");if(feature.Id=="join"){mode.Width=200;top.Children.Add(mode);}panel.Children.Add(top);
        var toolbar=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10,Margin=new(0,15,0,10)};
        var list=new ListBox{ItemsSource=_entries,SelectionMode=SelectionMode.Multiple,BorderThickness=new(1)};
        list.ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<ConversionEntry>((entry,_)=>{var row=new StackPanel{Spacing=4,Margin=new(2,4)};var pathText=new TextBlock{Text=entry?.Path,TextTrimming=TextTrimming.CharacterEllipsis};Localization.SetIsUserText(pathText,true);ToolTip.SetTip(pathText,entry?.Path);row.Children.Add(pathText);if(media){var summary=new TextBlock{Classes={"caption"}};summary.Bind(TextBlock.TextProperty,new Avalonia.Data.Binding(nameof(ConversionEntry.Summary)));row.Children.Add(summary);}return row;});
        var add=new Button{Content="添加文件…"};add.Click+=async(_,_)=>{foreach(var path in await Ui.Pick(this,"添加文件"))_entries.Add(new(path));};toolbar.Children.Add(add);
        var folder=new Button{Content="添加文件夹…"};folder.Click+=async(_,_)=>{if(await Ui.Folder(this,"添加文件夹") is {} path){foreach(var file in Directory.EnumerateFiles(path))_entries.Add(new(file));}};toolbar.Children.Add(folder);
        toolbar.Children.Add(Ui.Button("移除",()=>{foreach(var x in list.SelectedItems?.Cast<ConversionEntry>().ToArray()??[])_entries.Remove(x);}));
        toolbar.Children.Add(Ui.Button("上移",()=>{if(list.SelectedIndex>0){var index=list.SelectedIndex;_entries.Move(index,index-1);list.SelectedIndex=index-1;}}));
        toolbar.Children.Add(Ui.Button("下移",()=>{if(list.SelectedIndex>=0 && list.SelectedIndex<_entries.Count-1){var index=list.SelectedIndex;_entries.Move(index,index+1);list.SelectedIndex=index+1;}}));
        var edit=new Button{Content="选项 / 剪辑"};edit.Click+=async(_,_)=>
        {
            if(_entries.Count==0)return;var entry=list.SelectedItem as ConversionEntry??_entries[0];var path=entry.Path;_options.Format=(string?)formats.SelectedItem??feature.Format;
            var inputEdit=feature.Operation is Operation.Join or Operation.AudioMix or Operation.Mux;
            var draft=entry.Options?.Clone()??(inputEdit?new ConversionOptions():_options.Clone());draft.Format=_options.Format;
            var audioInput=(feature.Operation==Operation.Mux || feature.Id=="join"&&mode.SelectedIndex==1)&&_entries.IndexOf(entry)>0;
            var w=new EditorWindow(engine,path,draft,audioInput?"input-audio":inputEdit?"input":feature.Id);var result=await w.ShowDialog<ConversionOptions?>(this);if(result is not null && _entries.Contains(entry))entry.SetOptions(result);
        };
        if(media && feature.Operation!=Operation.ImagesPdf)toolbar.Children.Add(edit);
        Grid.SetRow(toolbar,1);panel.Children.Add(toolbar);Grid.SetRow(list,2);panel.Children.Add(list);
        var special=Ui.Input();
        if(feature.Operation is Operation.Download or Operation.IsoCopy)
        {
            list.IsVisible=false;toolbar.IsVisible=false;setting.IsVisible=false;
            var area=new StackPanel{Spacing=16,Margin=new(0,30,0,0)};
            if(feature.Operation==Operation.Download){area.Children.Add(Ui.Text("视频地址 (HTTP / HTTPS)"));area.Children.Add(special);area.Children.Add(Ui.Text("通过已配置的 yt-dlp 下载，输出 MP4。"));}
            else {area.Children.Add(Ui.Text(OperatingSystem.IsMacOS()?"光驱原始设备路径，例如 /dev/rdisk2":"光驱盘符或原始设备路径，例如 D:"));area.Children.Add(special);area.Children.Add(new TextBlock{Text="逐字节复制可读数据光盘为 ISO。需要本机读取权限，不处理加密。",TextWrapping=TextWrapping.Wrap});}
            Grid.SetRow(area,2);panel.Children.Add(area);
        }
        var output=new Grid{ColumnDefinitions=new("95,*,90"),RowDefinitions=new("Auto,Auto"),RowSpacing=6,Margin=new(0,14,0,6)};var outputBox=Ui.Input(outputFolder);outputBox.Name="ConversionOutputFolder";output.Children.Add(Ui.Text("输出文件夹"));Grid.SetColumn(outputBox,1);output.Children.Add(outputBox);
        var browse=new Button{Content="浏览…",Margin=new(10,0,0,0)};browse.Click+=async(_,_)=>{if(await Ui.Folder(this,"选择输出目录") is {} path)outputBox.Text=path;};Grid.SetColumn(browse,2);output.Children.Add(browse);Grid.SetRow(output,3);panel.Children.Add(output);
        var outputFlags=new StackPanel{Orientation=Orientation.Horizontal,Spacing=18};
        var sourceFolder=new CheckBox{Name="ConversionOutputToSource",Content="输出至源文件目录",IsChecked=initialOptions is null&&engine.Settings.OutputToSource,IsEnabled=feature.Operation is not (Operation.Download or Operation.IsoCopy)};
        var addName=new CheckBox{Name="ConversionAddSettingName",Content="添加设置名称",IsChecked=initialOptions is null&&engine.Settings.AddSettingName};
        sourceFolder.IsCheckedChanged+=(_,_)=>outputBox.IsEnabled=browse.IsEnabled=sourceFolder.IsChecked!=true;
        outputBox.IsEnabled=browse.IsEnabled=sourceFolder.IsChecked!=true;outputFlags.Children.Add(sourceFolder);outputFlags.Children.Add(addName);Grid.SetRow(outputFlags,1);Grid.SetColumnSpan(outputFlags,3);output.Children.Add(outputFlags);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=16,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,12,0,0)};
        buttons.Children.Add(Ui.DialogButton("取消",()=>Close(null)));var ok=new Button{Content="确定",Classes={"dialog-action"}};ok.Click+=async(_,_)=>
        {
            if(_preparing)return;_preparing=true;ok.IsEnabled=false;foreach(var control in new Control[]{top,toolbar,list,output})control.IsEnabled=false;
            try
            {
                _options.Format=(string?)formats.SelectedItem??feature.Format;var selected=feature;string[] inputs=_entries.Select(entry=>entry.Path).ToArray();
                if(feature.Id=="join" && mode.SelectedIndex==1)selected=Catalog.Find("mux");
                if(feature.Operation is Operation.Download or Operation.IsoCopy)inputs=[special.Text??""];
                var folderPath=Path.GetFullPath(outputBox.Text??"");var name=Path.Combine(folderPath,"validation."+(_options.Format.Length>0?_options.Format:"out"));
                var perInput=_entries.Select(entry=>{var option=entry.Options?.Clone()??(selected.Operation is Operation.Join or Operation.AudioMix or Operation.Mux?new ConversionOptions():_options.Clone());option.Format=_options.Format;return option;}).ToArray();
                var edits=_entries.Any(entry=>entry.Options is not null)?perInput:null;
                var candidate=new Job{FeatureId=selected.Id,Inputs=inputs,Output=name,Options=_options.Clone(),InputOptions=ConversionBatch.IsGrouped(selected)?edits?.ToList():null};
                MediaEngine.Validate(candidate);
                if(media && selected.Operation is not (Operation.Download or Operation.IsoCopy or Operation.ImagesPdf))
                {
                    var infos=new List<MediaInfo>();
                    for(int index=0;index<inputs.Length;index++)
                    {
                        var editOptions=ConversionBatch.IsGrouped(selected)?edits?.ElementAtOrDefault(index)??candidate.Options:perInput[index];
                        var vi=selected.Operation==Operation.Mux && index==1?0:editOptions.VideoStreamIndex;var ai=selected.Operation==Operation.Mux && index==0?0:editOptions.AudioStreamIndex;
                        var info=await engine.Probe(inputs[index],_lifetime.Token,vi,editOptions.KeepAllAudioStreams?0:ai);infos.Add(info);
                        if(!ConversionBatch.IsGrouped(selected))MediaEngine.ValidateEdits(new(){FeatureId=selected.Id,Inputs=[inputs[index]],Output=name,Options=editOptions},[info]);
                    }
                    if(ConversionBatch.IsGrouped(selected))MediaEngine.ValidateEdits(candidate,infos);
                }
                _lifetime.Token.ThrowIfCancellationRequested();Close(new ConversionRequest(selected,inputs,folderPath,candidate.Options,OutputToSource:sourceFolder.IsChecked==true&&sourceFolder.IsEnabled,SettingName:addName.IsChecked==true?OutputPreferences.SettingLabel(candidate):"",InputOptions:edits));
            }
            catch(OperationCanceledException){}
            catch(Exception ex){if(IsVisible)await Ui.Message(this,"参数错误",ex.Message);}
            finally{_preparing=false;if(IsVisible){ok.IsEnabled=true;foreach(var control in new Control[]{top,toolbar,list,output})control.IsEnabled=true;}}
        };buttons.Children.Add(ok);Grid.SetRow(buttons,4);panel.Children.Add(buttons);Content=panel;
    }
    private static IEnumerable<string> GetFormats(Feature f)=>f.Id=="mp4"?["mp4","mkv","mov","webm","avi","flv","wmv","mpg","ts","3gp","3g2","gif"]:f.Operation is Operation.Join or Operation.Mux && f.Category=="视频"?["mp4","mkv","webm","avi","mov","3gp","3g2"]:f.Id=="split"?["m4a","mp3","flac","wav","aac","ogg"]:[f.Format];
}
