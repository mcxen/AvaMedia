using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    public string Summary
    {
        get
        {
            if(Options is not {} option)return Localization.Text("完整源文件");
            var parts=new List<string>();
            if(option.Start>0||option.End>0)parts.Add(Localization.Format($"区间 {Time(option.Start)} → {(option.End>0?Time(option.End):Localization.Text("结尾"))}"));
            if(option.CropWidth>0)parts.Add(Localization.Format($"裁剪 {option.CropX},{option.CropY} {option.CropWidth} × {option.CropHeight}"));
            if(option.LosslessRotation is {} angle)parts.Add(Localization.Format($"方向标记 {angle}°"));
            else if(option.Rotation!=0)parts.Add(Localization.Format($"旋转 {option.Rotation}°"));
            if(option.Flip)parts.Add("水平镜像");
            if(option.Speed!=1)parts.Add($"{MediaEngine.Number(option.Speed)}×");
            return parts.Count>0?Localization.Join(" · ",parts):Localization.Text("完整源文件");
        }
    }
    private static string Time(double seconds)=>MediaTime.Format(seconds);
    public override string ToString()=>Path;
}
public sealed class ConvertWindow : Window
{
    private readonly ObservableCollection<ConversionEntry> _entries;
    private ConversionOptions _options;
    private readonly CancellationTokenSource _lifetime=new();
    private bool _preparing;
    public ConvertWindow(IMediaEngine engine,Feature feature,string outputFolder,string[] files,ConversionOptions? initialOptions=null,IReadOnlyList<ConversionOptions>? inputOptions=null,bool editing=false)
    {
        Title=feature.Label.Replace("\n"," ");Width=830;Height=620;MinWidth=650;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        WindowArtwork.SetKind(this, feature.Icon);
        Closed+=(_,_)=>_lifetime.Cancel();
        _options=initialOptions?.Clone()??new(){Format=feature.Format};
        _entries=new(files.Select((path,index)=>new ConversionEntry(path,inputOptions?.ElementAtOrDefault(index)??(editing&&!ConversionBatch.IsGrouped(feature)?_options:null))));
        if(initialOptions is null){if(feature.Id=="repair")_options.CopyStreams=true;if(feature.Operation==Operation.SplitVideo)_options.VideoCodec="copy";}
        if(editing)Title=Localization.Format($"编辑任务 · {Localization.Key(Title)}");
        if(feature.Operation is not (Operation.Download or Operation.IsoCopy))
        {
            DragDrop.SetAllowDrop(this,true);
            AddHandler(DragDrop.DragOverEvent,(_,e)=>{e.DragEffects=!_preparing && e.DataTransfer.TryGetFiles() is not null?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;},RoutingStrategies.Bubble,handledEventsToo:true);
            AddHandler(DragDrop.DropEvent,(_,e)=>
            {
                e.Handled=true;if(_preparing)return;
                var known=_entries.Select(entry=>Path.GetFullPath(entry.Path)).ToHashSet(VideoFolderScanner.PathComparer);
                foreach(var path in e.DataTransfer.TryGetFiles()?.Select(file=>file.TryGetLocalPath()).OfType<string>().Where(File.Exists).Select(Path.GetFullPath).Where(known.Add)??[])
                    _entries.Add(new(path));
            },RoutingStrategies.Bubble,handledEventsToo:true);
        }
        var panel=new Grid{RowDefinitions=new("Auto,Auto,*,Auto,Auto"),Margin=new(20)};
        var sourceOutput=feature.Id is "crop" or "rotate";
        var initialFormat=sourceOutput?SourceVideoExport.Selection(_options):_options.Format;
        var top=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10,IsVisible=feature.Operation!=Operation.Unzip};
        var formatChoices=GetFormats(feature).Append(_options.Format).Distinct().ToArray();
        var formats=Ui.Combo(formatChoices,initialFormat);formats.MinWidth=sourceOutput?240:130;
        if(formatChoices.Length>1){top.Children.Add(Ui.Text("输出格式"));top.Children.Add(formats);}
        var kind=feature.Operation==Operation.Frames?MediaOptionsKind.Frames:feature.Operation==Operation.SplitVideo?MediaOptionsKind.VideoOnly:feature.Category=="音频" || feature.Operation==Operation.SplitAudio?MediaOptionsKind.Audio:feature.Category=="图片"?MediaOptionsKind.Image:MediaOptionsKind.Video;
        var media=feature.Operation!=Operation.Info && (feature.Category is "视频" or "音频" or "图片" && feature.Operation!=Operation.ImagesPdf || feature.Operation is Operation.Mux or Operation.SplitVideo or Operation.Join || feature.Id is "repair" or "crop" or "rotate");
        var setting=new Button{Content="输出配置…",IsVisible=media};setting.Click+=async(_,_)=>
        {
            try
            {
                var selection=(string?)formats.SelectedItem??feature.Format;
                var single=!ConversionBatch.IsGrouped(feature)&&_entries.Count==1;
                var current=single?_entries[0].Options??_options:_options;
                var draft=SelectOutput(current,feature,selection,_entries.FirstOrDefault()?.Path??"");
                var dialog=new OptionsWindow(draft,kind:kind,allowAllAudioStreams:feature.Operation is not (Operation.Join or Operation.AudioMix),imageQualityDefault:draft.Format=="jpg"?engine.Settings.JpegQuality:engine.Settings.WebpQuality,previewEngine:engine,previewSource:_entries.FirstOrDefault()?.Path);
                var changed=await dialog.ShowDialog<ConversionOptions?>(this);
                if(changed is null)return;
                _options=SelectOutput(changed,feature,selection,_entries.FirstOrDefault()?.Path??"");
                if(single)_entries[0].SetOptions(_options);
                else if(!ConversionBatch.IsGrouped(feature))foreach(var entry in _entries.Where(e=>e.Options is not null))
                {
                    var old=entry.Options!;var next=changed.Clone();next.Start=old.Start;next.End=old.End;next.CropX=old.CropX;next.CropY=old.CropY;next.CropWidth=old.CropWidth;next.CropHeight=old.CropHeight;next.DelogoX=old.DelogoX;next.DelogoY=old.DelogoY;next.DelogoWidth=old.DelogoWidth;next.DelogoHeight=old.DelogoHeight;
                    entry.SetOptions(SelectOutput(next,feature,selection,entry.Path));
                }
            }
            catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}
        };top.Children.Add(setting);
        var changingFormat=false;
        formats.SelectionChanged+=async(_,_)=>
        {
            if(changingFormat)return;changingFormat=true;
            var previous=sourceOutput?SourceVideoExport.Selection(_options):_options.Format;
            try
            {
                var selection=(string?)formats.SelectedItem??feature.Format;
                var changed=SelectOutput(_options,feature,selection,_entries.FirstOrDefault()?.Path??"");
                var entries=_entries.Where(entry=>entry.Options is not null)
                    .Select(entry=>(Entry:entry,Options:SelectOutput(entry.Options!,feature,selection,entry.Path))).ToArray();
                _options=changed;foreach(var entry in entries)entry.Entry.SetOptions(entry.Options);
            }
            catch(Exception ex){formats.SelectedItem=previous;await Ui.Message(this,"参数错误",ex.Message);}
            finally{changingFormat=false;}
        };
        var mode=Ui.Combo(["视频合并","混流：视频 + 音频"],"视频合并");if(feature.Id=="join"){mode.Width=200;top.Children.Add(mode);}panel.Children.Add(top);
        var toolbar=new WrapPanel{Orientation=Orientation.Horizontal,Margin=new(0,15,0,2)};
        var list=new ListBox{ItemsSource=_entries,SelectionMode=SelectionMode.Multiple,BorderThickness=new(1)};
        list.ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<ConversionEntry>((entry,_)=>{var row=new StackPanel{Spacing=4,Margin=new(2,4)};var pathText=new TextBlock{Text=entry?.Path,TextTrimming=TextTrimming.CharacterEllipsis};Localization.SetIsUserText(pathText,true);ToolTip.SetTip(pathText,entry?.Path);row.Children.Add(pathText);if(media){var summary=new TextBlock{Classes={"caption"}};summary.Bind(TextBlock.TextProperty,new Avalonia.Data.Binding(nameof(ConversionEntry.Summary)));row.Children.Add(summary);}return row;});
        var add=new Button{Content="添加文件…"};add.Click+=async(_,_)=>AddPaths(await Ui.Pick(this,"添加文件"));toolbar.Children.Add(add);
        var folder=new Button{Content="添加文件夹…"};folder.Click+=async(_,_)=>{if(await Ui.Folder(this,"添加文件夹") is {} path)AddPaths(Directory.EnumerateFiles(path));};toolbar.Children.Add(folder);
        var remove=Ui.Button("移除选中",()=>{foreach(var x in list.SelectedItems?.Cast<ConversionEntry>().ToArray()??[])_entries.Remove(x);});toolbar.Children.Add(remove);
        var ordered=feature.Operation is Operation.Join or Operation.Mux or Operation.AudioMix;
        var up=Ui.Button("上移",()=>{if(list.SelectedIndex>0){var index=list.SelectedIndex;_entries.Move(index,index-1);list.SelectedIndex=index-1;}});up.IsVisible=ordered;toolbar.Children.Add(up);
        var down=Ui.Button("下移",()=>{if(list.SelectedIndex>=0 && list.SelectedIndex<_entries.Count-1){var index=list.SelectedIndex;_entries.Move(index,index+1);list.SelectedIndex=index+1;}});down.IsVisible=ordered;toolbar.Children.Add(down);
        var edit=new Button{Content="编辑选中…"};edit.Click+=async(_,_)=>
        {
            if(list.SelectedItem is not ConversionEntry entry)return;var path=entry.Path;
            var inputEdit=feature.Operation is Operation.Join or Operation.AudioMix or Operation.Mux;
            try
            {
                var draft=SelectOutput(entry.Options??(inputEdit?new ConversionOptions():_options),feature,(string?)formats.SelectedItem??feature.Format,path);
                var audioInput=(feature.Operation==Operation.Mux || feature.Id=="join"&&mode.SelectedIndex==1)&&_entries.IndexOf(entry)>0;
                var w=new EditorWindow(engine,path,draft,audioInput?"input-audio":inputEdit?"input":feature.Id);var result=await w.ShowDialog<ConversionOptions?>(this);
                if(result is not null && _entries.Contains(entry))entry.SetOptions(result);
            }
            catch(Exception ex){await Ui.Message(this,"媒体打开失败",ex.Message);}
        };
        if(media && feature.Operation!=Operation.ImagesPdf)toolbar.Children.Add(edit);
        foreach(var control in toolbar.Children)control.Margin=new(0,0,10,8);
        Grid.SetRow(toolbar,1);panel.Children.Add(toolbar);Grid.SetRow(list,2);panel.Children.Add(list);
        var special=Ui.Input(files.FirstOrDefault()??"");
        if(feature.Operation is Operation.Download or Operation.IsoCopy)
        {
            list.IsVisible=false;toolbar.IsVisible=false;setting.IsVisible=false;
            var area=new StackPanel{Spacing=16,Margin=new(0,30,0,0)};
            if(feature.Operation==Operation.Download){area.Children.Add(Ui.Text("视频地址 (HTTP / HTTPS)"));area.Children.Add(special);area.Children.Add(Ui.Text("通过已配置的 yt-dlp 下载，输出 MP4。"));}
            else {area.Children.Add(Ui.Text(OperatingSystem.IsMacOS()?"光驱原始设备路径，例如 /dev/rdisk2":"光驱盘符或原始设备路径，例如 D:"));area.Children.Add(special);area.Children.Add(new TextBlock{Text="逐字节复制可读数据光盘为 ISO。需要本机读取权限，不处理加密。",TextWrapping=TextWrapping.Wrap});}
            Grid.SetRow(area,2);panel.Children.Add(area);
        }
        var output=new Grid{ColumnDefinitions=new("Auto,*,Auto"),RowDefinitions=new("Auto,Auto"),RowSpacing=6,Margin=new(0,14,0,6)};var outputBox=Ui.Input(outputFolder);outputBox.Name="ConversionOutputFolder";output.Children.Add(Ui.Text("保存位置"));Grid.SetColumn(outputBox,1);output.Children.Add(outputBox);
        var browse=new Button{Content="浏览…",Classes={"field-action"},Margin=new(10,0,0,0)};browse.Click+=async(_,_)=>{if(await Ui.Folder(this,"选择输出目录") is {} path)outputBox.Text=path;};Grid.SetColumn(browse,2);output.Children.Add(browse);Grid.SetRow(output,3);panel.Children.Add(output);
        var outputFlags=new StackPanel{Orientation=Orientation.Horizontal,Spacing=18};
        var sourceFolder=new CheckBox{Name="ConversionOutputToSource",Content="输出至源文件目录",IsChecked=initialOptions is null&&engine.Settings.OutputToSource,IsEnabled=feature.Operation is not (Operation.Download or Operation.IsoCopy)};
        var addName=new CheckBox{Name="ConversionAddSettingName",Content="添加设置名称",IsChecked=initialOptions is null&&engine.Settings.AddSettingName};
        sourceFolder.IsCheckedChanged+=(_,_)=>outputBox.IsEnabled=browse.IsEnabled=sourceFolder.IsChecked!=true;
        outputBox.IsEnabled=browse.IsEnabled=sourceFolder.IsChecked!=true;outputFlags.Children.Add(sourceFolder);outputFlags.Children.Add(addName);Grid.SetRow(outputFlags,1);Grid.SetColumnSpan(outputFlags,3);output.Children.Add(outputFlags);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=16,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,12,0,0)};
        buttons.Children.Add(Ui.DialogButton("取消",()=>Close(null)));var ok=new Button{Content=editing?"保存修改":"加入队列",IsDefault=true,Classes={"primary","dialog-action"}};ok.Click+=async(_,_)=>
        {
            if(_preparing)return;_preparing=true;ok.IsEnabled=false;foreach(var control in new Control[]{top,toolbar,list,output})control.IsEnabled=false;
            try
            {
                var selection=(string?)formats.SelectedItem??feature.Format;var selected=feature;string[] inputs=_entries.Select(entry=>entry.Path).ToArray();
                if(feature.Id=="join" && mode.SelectedIndex==1)selected=Catalog.Find("mux");
                if(feature.Operation is Operation.Download or Operation.IsoCopy)inputs=[special.Text??""];
                _options=SelectOutput(_options,feature,selection,inputs.FirstOrDefault()??"");
                var folderPath=Path.GetFullPath(outputBox.Text??"");var name=Path.Combine(folderPath,"validation."+(_options.Format.Length>0?_options.Format:"out"));
                var perInput=_entries.Select(entry=>SelectOutput(entry.Options??(selected.Operation is Operation.Join or Operation.AudioMix or Operation.Mux?new ConversionOptions():_options),feature,selection,entry.Path)).ToArray();
                var edits=sourceOutput||_entries.Any(entry=>entry.Options is not null)?perInput:null;
                var candidate=new Job{FeatureId=selected.Id,Inputs=inputs,Output=name,Options=_options.Clone(),InputOptions=ConversionBatch.IsGrouped(selected)?edits?.ToList():null};
                if(ConversionBatch.IsGrouped(selected))MediaEngine.Validate(candidate);
                else
                {
                    if(inputs.Length==0)throw new ArgumentException("请添加文件。");
                    for(var index=0;index<inputs.Length;index++)MediaEngine.Validate(new(){FeatureId=selected.Id,Inputs=[inputs[index]],Options=perInput[index],Output=Path.Combine(folderPath,"validation."+perInput[index].Format)});
                }
                if(media && selected.Operation is not (Operation.Download or Operation.IsoCopy or Operation.ImagesPdf))
                {
                    var infos=new List<MediaInfo>();
                    for(int index=0;index<inputs.Length;index++)
                    {
                        var editOptions=ConversionBatch.IsGrouped(selected)?edits?.ElementAtOrDefault(index)??candidate.Options:perInput[index];
                        var vi=selected.Operation==Operation.Mux && index==1?0:editOptions.VideoStreamIndex;var ai=selected.Operation==Operation.Mux && index==0?0:editOptions.AudioStreamIndex;
                        var info=await engine.Probe(inputs[index],_lifetime.Token,vi,editOptions.KeepAllAudioStreams?0:ai);infos.Add(info);
                        if(!ConversionBatch.IsGrouped(selected))MediaEngine.ValidateEdits(new(){FeatureId=selected.Id,Inputs=[inputs[index]],Output=Path.Combine(folderPath,"validation."+editOptions.Format),Options=editOptions},[info]);
                    }
                    if(ConversionBatch.IsGrouped(selected))MediaEngine.ValidateEdits(candidate,infos);
                }
                _lifetime.Token.ThrowIfCancellationRequested();Close(new ConversionRequest(selected,inputs,folderPath,candidate.Options,OutputToSource:sourceFolder.IsChecked==true&&sourceFolder.IsEnabled,SettingName:addName.IsChecked==true?OutputPreferences.SettingLabel(candidate):"",InputOptions:edits));
            }
            catch(OperationCanceledException){}
            catch(Exception ex){if(IsVisible)await Ui.Message(this,"参数错误",ex.Message);}
            finally{_preparing=false;if(IsVisible){foreach(var control in new Control[]{top,toolbar,list,output})control.IsEnabled=true;RefreshActions();}}
        };buttons.Children.Add(ok);Grid.SetRow(buttons,4);panel.Children.Add(buttons);Content=panel;
        _entries.CollectionChanged+=(_,_)=>RefreshActions();list.SelectionChanged+=(_,_)=>RefreshActions();special.TextChanged+=(_,_)=>RefreshActions();
        top.IsVisible=top.Children.Any(control=>control.IsVisible);RefreshActions();
        void AddPaths(IEnumerable<string> paths)
        {
            var known=_entries.Select(entry=>Path.GetFullPath(entry.Path)).ToHashSet(VideoFolderScanner.PathComparer);
            foreach(var path in paths.Select(Path.GetFullPath).Where(File.Exists).Where(known.Add))_entries.Add(new(path));
        }
        void RefreshActions()
        {
            var selected=list.SelectedItems?.Count??0;
            remove.IsEnabled=selected>0;edit.IsEnabled=selected==1;
            up.IsEnabled=selected==1&&list.SelectedIndex>0;down.IsEnabled=selected==1&&list.SelectedIndex>=0&&list.SelectedIndex<_entries.Count-1;
            ok.IsEnabled=!_preparing&&(feature.Operation is Operation.Download or Operation.IsoCopy?!string.IsNullOrWhiteSpace(special.Text):_entries.Count>0);
        }
    }
    private static ConversionOptions SelectOutput(ConversionOptions options,Feature feature,string selection,string path)
    {
        if(feature.Id is "crop" or "rotate")return SourceVideoExport.WithOutput(options,selection,path);
        var result=options.Clone();
        if(result.Format!=selection){result.Format=selection;result.VideoCodec=result.AudioCodec="自动";}
        return result;
    }

    private static IEnumerable<string> GetFormats(Feature f)=>f.Id switch
    {
        "mp4" or "clip"=>["mp4","mkv","mov","webm","avi","flv","wmv","mpg","ts","3gp","3g2","gif"],
        "crop"=>[SourceVideoExport.Original,"mp4","mkv","webm","mov","avi","ts"],
        "rotate"=>[SourceVideoExport.Original,SourceVideoExport.FastRotation,"mp4","mkv","webm","mov","avi","ts"],
        "repair"=>["mkv","mp4","mov","ts"],
        "delogo"=>["mp4","mkv","mov","ts"],
        _ when f.Category=="音频"||f.Operation==Operation.SplitAudio=>["mp3","flac","wav","m4a","ogg","aac","ac3","wma","opus","aiff"],
        _ when f.Category=="图片"=>["jpg","png","webp","bmp","tiff","gif","ico","avif"],
        _ when f.Operation==Operation.SplitVideo=>["mkv","mp4","mov","ts"],
        _ when f.Operation is Operation.Join or Operation.Mux=>["mp4","mkv","webm","avi","mov","ts","3gp","3g2"],
        _=>[f.Format]
    };
}
