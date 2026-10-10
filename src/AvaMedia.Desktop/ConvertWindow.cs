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
    IReadOnlyList<QuickClipInput>? ClipInputs=null,bool OutputToSource=false,string SettingName="",IReadOnlyList<ConversionOptions>? InputOptions=null,bool StartImmediately=false);
public sealed class ConversionEntry(string path,ConversionOptions? options=null) : Observable
{
    public string Path { get; }=path;
    public ConversionOptions? Options { get; private set; }=options?.Clone();
    private bool _include=true;
    public bool Include { get=>_include;set=>Set(ref _include,value); }
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
    private readonly ImageCropEditor? _imageCrop;
    private readonly MediaPreviewPanel? _preview;
    public ConvertWindow(IMediaEngine engine,Feature feature,string outputFolder,string[] files,ConversionOptions? initialOptions=null,IReadOnlyList<ConversionOptions>? inputOptions=null,bool editing=false)
    {
        Title=feature.Label.Replace("\n"," ");Width=1100;Height=780;MinWidth=900;MinHeight=640;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var visualImages=feature.Category=="图片"&&feature.Operation==Operation.Convert;
        WindowArtwork.SetKind(this, feature.Icon);
        Closed+=(_,_)=>_lifetime.Cancel();
        _options=initialOptions?.Clone()??new Storage().LoadToolOptions<ConversionOptions>(feature.Id)??new(){Format=feature.Format};
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
                foreach(var path in e.DataTransfer.TryGetFiles()?.Select(file=>file.TryGetLocalPath()).OfType<string>().Where(File.Exists).Where(AcceptPath).Select(Path.GetFullPath).Where(known.Add)??[])
                    _entries.Add(new(path));
            },RoutingStrategies.Bubble,handledEventsToo:true);
        }
        var panel=new Grid{RowDefinitions=new("Auto,Auto,*,Auto,Auto"),Margin=new(20)};
        var sourceOutput=feature.Id is "crop" or "rotate";
        var initialFormat=sourceOutput?SourceVideoExport.Selection(_options):_options.Format;
        var top=new WrapPanel{Orientation=Orientation.Horizontal,IsVisible=feature.Operation!=Operation.Unzip};
        var formatChoices=GetFormats(feature).Append(_options.Format).Distinct().ToArray();
        var formats=Ui.Combo(formatChoices,initialFormat);formats.MinWidth=sourceOutput?240:130;
        if(formatChoices.Length>1){top.Children.Add(Ui.Text("输出格式"));top.Children.Add(formats);}
        var kind=feature.Operation==Operation.Frames?MediaOptionsKind.Frames:feature.Operation==Operation.SplitVideo?MediaOptionsKind.VideoOnly:feature.Category=="音频" || feature.Operation==Operation.SplitAudio?MediaOptionsKind.Audio:feature.Category=="图片"?MediaOptionsKind.Image:MediaOptionsKind.Video;
        var media=feature.Operation!=Operation.Info && (feature.Category is "视频" or "音频" or "图片" && feature.Operation!=Operation.ImagesPdf || feature.Operation is Operation.Mux or Operation.SplitVideo or Operation.Join || feature.Id is "repair" or "crop" or "rotate");
        var list=new ListBox{ItemsSource=_entries,SelectionMode=visualImages?SelectionMode.Single:SelectionMode.Multiple,BorderThickness=new(1)};
        var setting=new Button{Content="输出配置…",IsVisible=media};setting.Click+=async(_,_)=>
        {
            try
            {
                var selection=(string?)formats.SelectedItem??feature.Format;
                var single=!ConversionBatch.IsGrouped(feature)&&_entries.Count==1;
                var current=single?_entries[0].Options??_options:_options;
                var draft=SelectOutput(current,feature,selection,_entries.FirstOrDefault()?.Path??"");
                var dialog=new OptionsWindow(draft,copyStreamsMode:feature.Id=="repair"?true:null,kind:kind,allowAllAudioStreams:feature.Operation is not (Operation.Join or Operation.AudioMix),imageQualityDefault:draft.Format=="jpg"?engine.Settings.JpegQuality:engine.Settings.WebpQuality,previewEngine:engine,previewSource:_entries.FirstOrDefault()?.Path);
                var changed=await dialog.ShowDialog<ConversionOptions?>(this);
                if(changed is null)return;
                _options=SelectOutput(changed,feature,selection,_entries.FirstOrDefault()?.Path??"");
                if(single)_entries[0].SetOptions(_options);
                else if(!ConversionBatch.IsGrouped(feature))foreach(var entry in _entries.Where(e=>e.Options is not null))
                {
                    var old=entry.Options!;var next=changed.Clone();next.Start=old.Start;next.End=old.End;next.CropX=old.CropX;next.CropY=old.CropY;next.CropWidth=old.CropWidth;next.CropHeight=old.CropHeight;next.DelogoX=old.DelogoX;next.DelogoY=old.DelogoY;next.DelogoWidth=old.DelogoWidth;next.DelogoHeight=old.DelogoHeight;
                    entry.SetOptions(SelectOutput(next,feature,selection,entry.Path));
                }
                _imageCrop?.Refresh();RefreshPreview();
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
                _options=changed;foreach(var entry in entries)entry.Entry.SetOptions(entry.Options);_imageCrop?.Refresh();RefreshPreview();
            }
            catch(Exception ex){formats.SelectedItem=previous;await Ui.Message(this,"参数错误",ex.Message);}
            finally{changingFormat=false;}
        };
        panel.Children.Add(top);
        var toolbar=new WrapPanel{Orientation=Orientation.Horizontal,Margin=new(0,15,0,2)};
        list.ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<ConversionEntry>((entry,_)=>{if(entry is null)return new Border();var row=new StackPanel{Spacing=4,Margin=new(2,4)};if(feature.Operation==Operation.Mux)row.Children.Add(Ui.Text(_entries.IndexOf(entry!)==0?"视频来源":"音频来源","heading"));if(feature.Operation!=Operation.Mux){var include=new CheckBox { Content="处理此文件",IsChecked=entry!.Include };include.Bind(CheckBox.IsCheckedProperty,new Avalonia.Data.Binding(nameof(ConversionEntry.Include)) { Mode=Avalonia.Data.BindingMode.TwoWay });row.Children.Add(include);}
            var pathText=new TextBlock{Text=System.IO.Path.GetFileName(entry?.Path),TextTrimming=TextTrimming.CharacterEllipsis};Localization.SetIsUserText(pathText,true);ToolTip.SetTip(pathText,entry?.Path);row.Children.Add(pathText);if(media){var summary=new TextBlock{Classes={"caption"}};summary.Bind(TextBlock.TextProperty,new Avalonia.Data.Binding(nameof(ConversionEntry.Summary)));row.Children.Add(summary);}return row;});
        var add=new Button{Content=feature.Operation==Operation.Mux?"选择视频来源…":"添加文件…"};
        add.Click+=async(_,_)=>
        {
            var picked=await Ui.Pick(this,feature.Operation==Operation.Mux?"选择视频来源":"添加文件",feature.Operation!=Operation.Mux);
            if(feature.Operation==Operation.Mux){if(picked.Length==0)return;if(_entries.Count>0)_entries[0]=new(picked[0]);else _entries.Add(new(picked[0]));}else AddPaths(picked);
        };toolbar.Children.Add(add);
        if(feature.Operation==Operation.Mux)toolbar.Children.Add(Ui.Button("选择音频来源…",async()=>
        {var picked=await Ui.Pick(this,"选择音频来源",false);if(picked.Length==0)return;if(_entries.Count==0){await Ui.Message(this,"混流","请先选择视频来源。");return;}if(_entries.Count>1)_entries[1]=new(picked[0]);else _entries.Add(new(picked[0]));}));
        var folder=new Button{Content="添加文件夹…"};folder.Click+=async(_,_)=>{if(await Ui.Folder(this,"添加文件夹") is {} path)AddPaths(Directory.EnumerateFiles(path));};folder.IsVisible=feature.Operation!=Operation.Mux;toolbar.Children.Add(folder);
        var remove=Ui.Button("移除选中",()=>{foreach(var x in list.SelectedItems?.Cast<ConversionEntry>().ToArray()??[])_entries.Remove(x);});toolbar.Children.Add(remove);
        var ordered=feature.Operation is Operation.Join or Operation.AudioMix;
        var up=Ui.Button("上移",()=>{if(list.SelectedIndex>0){var index=list.SelectedIndex;_entries.Move(index,index-1);list.SelectedIndex=index-1;}});up.IsVisible=ordered;toolbar.Children.Add(up);
        var down=Ui.Button("下移",()=>{if(list.SelectedIndex>=0 && list.SelectedIndex<_entries.Count-1){var index=list.SelectedIndex;_entries.Move(index,index+1);list.SelectedIndex=index+1;}});down.IsVisible=ordered;toolbar.Children.Add(down);
        var edit=new Button{Content=feature.Id=="delogo"?"框选水印区域…":"编辑选中…"};edit.Click+=async(_,_)=>
        {
            if(list.SelectedItem is not ConversionEntry entry)return;var path=entry.Path;
            var inputEdit=feature.Operation is Operation.Join or Operation.AudioMix or Operation.Mux;
            try
            {
                var draft=SelectOutput(entry.Options??(inputEdit?new ConversionOptions():_options),feature,(string?)formats.SelectedItem??feature.Format,path);
                var audioInput=feature.Operation==Operation.Mux&&_entries.IndexOf(entry)>0;
                var w=new EditorWindow(engine,path,draft,audioInput?"input-audio":inputEdit?"input":feature.Id);var result=await w.ShowDialog<ConversionOptions?>(this);
                if(result is not null && _entries.Contains(entry)){entry.SetOptions(result);RefreshPreview();}
            }
            catch(Exception ex){await Ui.Message(this,"媒体打开失败",ex.Message);}
        };
        if(media && feature.Operation!=Operation.ImagesPdf && !visualImages)toolbar.Children.Add(edit);
        foreach(var control in toolbar.Children)control.Margin=new(0,0,10,8);
        Grid.SetRow(toolbar,1);panel.Children.Add(toolbar);
        Control inputArea=list;
        if(visualImages)
        {
            Width=1120;Height=800;MinWidth=900;MinHeight=650;
            _imageCrop=new ImageCropEditor(engine,entry=>entry.Options??_options,_lifetime.Token);
            var workspace=new Grid{ColumnDefinitions=new("250,*"),ColumnSpacing=12};
            workspace.Children.Add(list);Grid.SetColumn(_imageCrop,1);workspace.Children.Add(_imageCrop);inputArea=workspace;
            list.SelectionChanged+=(_,_)=>_imageCrop.Select(list.SelectedItem as ConversionEntry);
            _entries.CollectionChanged+=(_,_)=>{if(list.SelectedItem is null && _entries.Count>0)list.SelectedIndex=0;};
            Closed+=(_,_)=>_imageCrop.Dispose();
            Opened+=(_,_)=>{if(_entries.Count>0)list.SelectedIndex=0;};
        }
        else if(media || feature.Operation is Operation.Zip or Operation.Unzip)
        {
            _preview=new MediaPreviewPanel(engine);
            var workspace=new Grid{ColumnDefinitions=new("320,*"),ColumnSpacing=12};workspace.Children.Add(list);
            Grid.SetColumn(_preview,1);workspace.Children.Add(_preview);inputArea=workspace;
            list.SelectionChanged+=(_,_)=>RefreshPreview();
            _entries.CollectionChanged+=(_,_)=>{if(list.SelectedItem is null&&_entries.Count>0)list.SelectedIndex=0;RefreshPreview();};
            Opened+=(_,_)=>{if(_entries.Count>0)list.SelectedIndex=0;};Closed+=(_,_)=>_preview.Dispose();
        }
        Grid.SetRow(inputArea,2);panel.Children.Add(inputArea);
        if(feature.Operation==Operation.Frames)
        {
            var durations=new Dictionary<string,double>(VideoFolderScanner.PathComparer);var expected=Ui.Text("","caption");var generation=0;
            void CountFrames()
            {
                if(durations.Count!=_entries.Count){expected.Text="";return;}
                var selected=_entries.Where(entry=>entry.Include).ToArray();double frames=0;
                foreach(var entry in selected)
                {
                    var options=entry.Options??_options;var end=options.End>0?Math.Min(options.End,durations[entry.Path]):durations[entry.Path];
                    frames+=Math.Max(0,Math.Ceiling((end-options.Start)/options.FrameInterval));
                }
                expected.Text=selected.Length>0?Localization.Format($"预计 {frames} 帧"):"";
            }
            void WatchFrames(ConversionEntry entry)=>entry.PropertyChanged+=(_,change)=>{if(change.PropertyName is nameof(ConversionEntry.Include) or nameof(ConversionEntry.Summary))CountFrames();};
            foreach(var entry in _entries)WatchFrames(entry);
            _entries.CollectionChanged+=(_,change)=>{foreach(var entry in change.NewItems?.OfType<ConversionEntry>()??[])WatchFrames(entry);};
            async Task ReadDurations()
            {
                var revision=++generation;
                try{foreach(var entry in _entries.ToArray()){if(!durations.ContainsKey(entry.Path)){var info=await engine.Probe(entry.Path,_lifetime.Token);if(revision!=generation)return;durations[entry.Path]=info.Duration;}}if(revision==generation)CountFrames();}
                catch(Exception){if(revision==generation)expected.Text="";}
            }
            _entries.CollectionChanged+=(_,_)=>{durations.Clear();_=ReadDurations();};Opened+=(_,_)=>_=ReadDurations();

            var frameInterval=new NumericUpDown { Minimum=.1m,Maximum=86400,Value=(decimal)_options.FrameInterval,Text=MediaEngine.Number(_options.FrameInterval),Increment=1,Width=120 };
            top.Children.Add(Ui.Text("每隔几秒导出一帧"));top.Children.Add(Ui.Adjust(frameInterval,60));
            frameInterval.ValueChanged+=(_,_)=>{if(frameInterval.Value is {} value){_options.FrameInterval=(double)value;foreach(var entry in _entries.Where(entry=>entry.Options is not null)){var options=entry.Options!.Clone();options.FrameInterval=(double)value;entry.SetOptions(options);}CountFrames();}};
            top.Children.Add(expected);
        }
        if(feature.Id=="mp4")
        {
            var preset=Ui.Combo(["保持原尺寸","通用 MP4 · 1080p","手机视频 · 720p","无损转封装","自定义设置"],_options.CopyStreams?"无损转封装":_options.Height==1080?"通用 MP4 · 1080p":_options.Height==720?"手机视频 · 720p":_options.Width==0&&_options.Height==0?"保持原尺寸":"自定义设置");
            preset.SelectionChanged+=(_,_)=>
            {
                if(preset.SelectedIndex==4)return;
                var option=_options.Clone();option.Width=option.Height=0;option.CopyStreams=preset.SelectedIndex==3;
                if(preset.SelectedIndex is 1 or 2){option.Format="mp4";option.VideoCodec="h264";option.AudioCodec="aac";option.Height=preset.SelectedIndex==1?1080:720;formats.SelectedItem="mp4";}
                else if(preset.SelectedIndex==3){option.VideoCodec=option.AudioCodec="copy";}
                else{option.VideoCodec=option.AudioCodec="自动";}
                _options=option;foreach(var entry in _entries.Where(entry=>entry.Options is not null)){var old=entry.Options!;var edit=option.Clone();edit.Start=old.Start;edit.End=old.End;entry.SetOptions(edit);}
            };top.Children.Add(preset);
        }
        var special=Ui.Input(files.FirstOrDefault()??"");
        var discSource=feature.Operation==Operation.IsoCopy?new DiscSourcePicker(files.FirstOrDefault()):null;
        if(feature.Operation is Operation.Download or Operation.IsoCopy)
        {
            list.IsVisible=false;toolbar.IsVisible=false;setting.IsVisible=false;
            var area=new StackPanel{Spacing=16,Margin=new(0,30,0,0)};
            if(feature.Operation==Operation.Download){area.Children.Add(Ui.Text("视频地址 (HTTP / HTTPS)"));area.Children.Add(special);area.Children.Add(Ui.Text("粘贴视频链接，选择保存位置即可。"));}
            else {area.Children.Add(discSource!);area.Children.Add(Ui.Text("复制可读数据光盘为 ISO。","caption"));}
            Grid.SetRow(area,2);panel.Children.Add(area);
        }
        var output=new Grid{ColumnDefinitions=new("Auto,*,Auto"),RowDefinitions=new("Auto,Auto"),RowSpacing=6,Margin=new(0,14,0,6)};var outputBox=Ui.Input(outputFolder);outputBox.IsReadOnly=true;outputBox.Name="ConversionOutputFolder";output.Children.Add(Ui.Text("保存位置"));Grid.SetColumn(outputBox,1);output.Children.Add(outputBox);
        var browse=new Button{Content="浏览…",Classes={"field-action"},Margin=new(10,0,0,0)};browse.Click+=async(_,_)=>{if(await Ui.Folder(this,"选择输出目录") is {} path)outputBox.Text=path;};Grid.SetColumn(browse,2);output.Children.Add(browse);Grid.SetRow(output,3);panel.Children.Add(output);
        var outputFlags=new StackPanel{Orientation=Orientation.Horizontal,Spacing=18};
        var sourceFolder=new CheckBox{Name="ConversionOutputToSource",Content="输出至源文件目录",IsChecked=initialOptions is null&&engine.Settings.OutputToSource,IsEnabled=feature.Operation is not (Operation.Download or Operation.IsoCopy)};
        var addName=new CheckBox{Name="ConversionAddSettingName",Content="添加设置名称",IsChecked=initialOptions is null&&engine.Settings.AddSettingName};
        sourceFolder.IsCheckedChanged+=(_,_)=>outputBox.IsEnabled=browse.IsEnabled=sourceFolder.IsChecked!=true;
        outputBox.IsEnabled=browse.IsEnabled=sourceFolder.IsChecked!=true;outputFlags.Children.Add(sourceFolder);outputFlags.Children.Add(addName);Grid.SetRow(outputFlags,1);Grid.SetColumnSpan(outputFlags,3);output.Children.Add(outputFlags);
        var validation=Ui.Text("","error");validation.TextWrapping=TextWrapping.Wrap;
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=16,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,12,0,0)};
        buttons.Children.Add(Ui.DialogButton("取消",()=>Close(null)));var ok=new Button{Content=editing?"保存修改":"加入队列",IsDefault=true,Classes={"primary","dialog-action"}};ok.Click+=async(_,_)=>
        {
            if(_preparing)return;validation.Text="";_preparing=true;ok.IsEnabled=false;foreach(var control in new Control[]{top,toolbar,inputArea,output})control.IsEnabled=false;
            try
            {
                _imageCrop?.ValidateSelection();
                var selection=(string?)formats.SelectedItem??feature.Format;var selected=feature;var selectedEntries=_entries.Where(entry=>entry.Include||feature.Operation==Operation.Mux).ToArray();string[] inputs=selectedEntries.Select(entry=>entry.Path).ToArray();
                if(feature.Operation is Operation.Download or Operation.IsoCopy)inputs=[discSource?.Path??special.Text??""];
                _options=SelectOutput(_options,feature,selection,inputs.FirstOrDefault()??"");
                var folderPath=Path.GetFullPath(outputBox.Text??"");var name=Path.Combine(folderPath,"validation."+(_options.Format.Length>0?_options.Format:"out"));
                var perInput=selectedEntries.Select(entry=>SelectOutput(entry.Options??(selected.Operation is Operation.Join or Operation.AudioMix or Operation.Mux?new ConversionOptions():_options),feature,selection,entry.Path)).ToArray();
                var edits=sourceOutput||selectedEntries.Any(entry=>entry.Options is not null)?perInput:null;
                var candidate=new Job{FeatureId=selected.Id,Inputs=inputs,Output=name,Options=_options.Clone(),InputOptions=ConversionBatch.IsGrouped(selected)?edits?.ToList():null};
                if(feature.Id=="delogo"&&perInput.Any(options=>options.DelogoWidth<=0||options.DelogoHeight<=0))throw new ArgumentException("请为每个勾选视频框选水印区域。");
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
                var remembered=candidate.Options.Clone();remembered.Start=remembered.End=0;remembered.Subtitle="";remembered.CropX=remembered.CropY=remembered.CropWidth=remembered.CropHeight=0;remembered.DelogoX=remembered.DelogoY=remembered.DelogoWidth=remembered.DelogoHeight=0;
                new Storage().SaveToolOptions(feature.Id,remembered);
                _lifetime.Token.ThrowIfCancellationRequested();ToolExecution.Complete(this, new ConversionRequest(selected,inputs,folderPath,candidate.Options,OutputToSource:sourceFolder.IsChecked==true&&sourceFolder.IsEnabled,SettingName:addName.IsChecked==true?OutputPreferences.SettingLabel(candidate):"",InputOptions:edits,StartImmediately:ToolExecution.StartImmediately(this)));
            }
            catch(OperationCanceledException){}
            catch(Exception ex){if(IsVisible)validation.Text=ex.Message;}
            finally{_preparing=false;if(IsVisible){foreach(var control in new Control[]{top,toolbar,inputArea,output})control.IsEnabled=true;RefreshActions();}}
        };buttons.Children.Add(ok);var footer=new Grid { ColumnDefinitions=new("*,Auto"),ColumnSpacing=12 };footer.Children.Add(validation);Grid.SetColumn(buttons,1);footer.Children.Add(buttons);Grid.SetRow(footer,4);panel.Children.Add(footer);Content=panel;if(feature.Category=="视频")ToolExecution.Configure(this,ok,"开始处理",editing);
        foreach(var entry in _entries)entry.PropertyChanged+=(_,_)=>RefreshActions();
        _entries.CollectionChanged+=(_,change)=>{foreach(var entry in change.NewItems?.OfType<ConversionEntry>()??[])entry.PropertyChanged+=(_,_)=>RefreshActions();RefreshActions();};list.SelectionChanged+=(_,_)=>RefreshActions();special.TextChanged+=(_,_)=>RefreshActions();if(discSource is not null)discSource.Changed+=RefreshActions;
        if(_imageCrop is not null)_imageCrop.Changed+=RefreshActions;
        foreach(var child in top.Children)child.Margin=new(0,0,10,8);
        top.IsVisible=top.Children.Any(control=>control.IsVisible);RefreshActions();
        void AddPaths(IEnumerable<string> paths)
        {
            var known=_entries.Select(entry=>Path.GetFullPath(entry.Path)).ToHashSet(VideoFolderScanner.PathComparer);
            foreach(var path in paths.Select(Path.GetFullPath).Where(File.Exists).Where(AcceptPath).Where(known.Add))_entries.Add(new(path));
        }
        bool AcceptPath(string path)=>!visualImages || MediaEngine.IsImage(Path.GetExtension(path).TrimStart('.').ToLowerInvariant()) || Path.GetExtension(path).Equals(".gif",StringComparison.OrdinalIgnoreCase);
        void RefreshPreview(){if(_preview is null)return;var entry=list.SelectedItem as ConversionEntry;_preview.SetSource(entry?.Path,media?entry?.Options??_options:null);}
        void RefreshActions()
        {
            var selected=list.SelectedItems?.Count??0;
            remove.IsEnabled=selected>0;edit.IsEnabled=selected==1;
            up.IsEnabled=selected==1&&list.SelectedIndex>0;down.IsEnabled=selected==1&&list.SelectedIndex>=0&&list.SelectedIndex<_entries.Count-1;
            ok.IsEnabled=!_preparing&&(_imageCrop?.CanConfirm??true)&&(feature.Operation is Operation.Download or Operation.IsoCopy?!string.IsNullOrWhiteSpace(discSource?.Path??special.Text):_entries.Any(entry=>entry.Include)||feature.Operation==Operation.Mux&&_entries.Count>0);
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
