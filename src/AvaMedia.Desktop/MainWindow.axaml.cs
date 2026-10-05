using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;
public partial class MainWindow : Window
{
    private readonly Storage _storage;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<Job> _jobs;
    private readonly QueueService _queue;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _elapsed=new();
    private string _category="视频";
    private Feature _last=Catalog.Find("mp4");
    private DateTime _lastSave;
    private bool _closing;
    private Task _running=Task.CompletedTask;
    public IMediaEngine Engine {get;}
    internal event Action? JobDisplayChanged;
    public MainWindow() : this(new Storage()) { }
    public MainWindow(Storage storage, IMediaEngine? engine=null)
    {
        _storage=storage??new();InitializeComponent();_settings=_storage.LoadSettings();Skin.Apply(_settings.Theme);Motion.SetReducedMotion(_settings.ReduceMotion);Engine=engine??new MediaEngine(_settings);_queue=new(Engine);
        _jobs=new(_storage.LoadJobs());JobList.ItemsSource=_jobs;
        OutputPath.Text="📂 "+_settings.OutputFolder;Multithread.IsChecked=_settings.MultiThread;Notify.IsChecked=_settings.NotifyComplete;
        _queue.Changed+=job=>Dispatcher.UIThread.Post(()=>{Refresh();if(DateTime.UtcNow-_lastSave>TimeSpan.FromSeconds(1)){Save();_lastSave=DateTime.UtcNow;}});
        _timer=new(){Interval=TimeSpan.FromSeconds(1)};_timer.Tick+=(_,_)=>ElapsedText.Text="耗时: "+_elapsed.Elapsed.ToString(@"hh\:mm\:ss");_timer.Start();
        ShowCategory(_category);Refresh();
        DragDrop.SetAllowDrop(this,true);AddHandler(DragDrop.DropEvent,Drop);AddHandler(DragDrop.DragOverEvent,DragOver);
        Closing+=async (_,e)=>
        {
            if(_closing)return;
            if(_queue.IsRunning){e.Cancel=true;_closing=true;_queue.Stop();await _running;Save();_timer.Stop();Close();}
            else {Save();_timer.Stop();}
        };
    }
    private void ShowCategory(string category)
    {
        _category=category;CategoryTitle.Text=category;CategoryGlyph.Text=category switch{"视频"=>"▣","音频"=>"♫","图片"=>"▧","文档"=>"▤",_=>"◉"};
        FeatureGrid.Children.Clear();FeatureGrid.RowDefinitions.Clear();int col=0,row=0;
        foreach(var f in Catalog.All.Where(f=>f.Category==category))
        {
            if(col+f.Span>4){col=0;row++;}while(FeatureGrid.RowDefinitions.Count<=row)FeatureGrid.RowDefinitions.Add(new RowDefinition(91,GridUnitType.Pixel));
            var content=new Grid{RowDefinitions=new("*,Auto")};content.Children.Add(new FeatureIcon{Kind=f.Icon,Label=f.Format.ToUpperInvariant(),Height=64});
            var text=new TextBlock{Text=f.Label,TextWrapping=TextWrapping.Wrap,Margin=new(1,0),VerticalAlignment=VerticalAlignment.Bottom};Grid.SetRow(text,1);content.Children.Add(text);
            var tile=new Button{Content=content,Margin=new(3),Classes={"tile"}};ToolTip.SetTip(tile,f.Label);
            tile.Click+=async (_,_)=>await Configure(f);Grid.SetColumn(tile,col);Grid.SetRow(tile,row);Grid.SetColumnSpan(tile,f.Span);FeatureGrid.Children.Add(tile);col+=f.Span;if(col==4){col=0;row++;}
        }
        Categories.Children.Clear();
        foreach(var cat in Catalog.Categories.Where(c=>c!=category))
        {
            var b=new Button{Classes={"category"}};var g=new Grid{ColumnDefinitions=new("24,*")};g.Children.Add(new TextBlock{Text=cat switch{"音频"=>"♫","图片"=>"▧","文档"=>"▤","视频"=>"▣",_=>"◉"},Classes={"muted-icon"}});var t=Ui.Text(cat);t.HorizontalAlignment=HorizontalAlignment.Center;Grid.SetColumn(t,1);g.Children.Add(t);b.Content=g;b.Click+=(_,_)=>ShowCategory(cat);Categories.Children.Add(b);
        }
        Motion.Reveal(FeatureGrid);
    }
    private async Task Configure(Feature feature,string[]? files=null)
    {
        if(_queue.IsRunning && feature.Operation==Operation.Record){await Ui.Message(this,"任务正在运行","请先停止当前任务后再配置录屏。");return;}
        _last=feature;
        if(feature.Operation==Operation.Download){await ConfigureDownloadAsync(files);return;}
        if(feature.Id=="clip")
        {
            if(files is null)await PickQuickClipVideos();else await EditQuickClipAsync(files);
            return;
        }
        if(feature.Id=="rotate")
        {
            var request=await new BatchRotateWindow(Engine,_settings.OutputFolder,files).ShowDialog<BatchRotateRequest?>(this);
            if(request is null)return;
            try{var jobs=BatchRotate.CreateJobs(request,_jobs.Select(j=>j.Output));foreach(var job in jobs)_jobs.Add(job);Save();Refresh();}
            catch(Exception ex){await Ui.Message(this,"批量旋转参数错误",ex.Message);}
            return;
        }
        if(feature.Id=="crop")
        {
            var request=await new BatchCropWindow(Engine,_settings.OutputFolder,files).ShowDialog<BatchCropRequest?>(this);
            if(request is null)return;
            try{var jobs=BatchCrop.CreateJobs(request,_jobs.Select(j=>j.Output));foreach(var job in jobs)_jobs.Add(job);Save();Refresh();}
            catch(Exception ex){await Ui.Message(this,"批量裁剪参数错误",ex.Message);}
            return;
        }
        if(feature.Operation==Operation.Player)
        {
            files??=await Ui.Pick(this,"打开媒体文件",true);if(files.Length>0)new PlayerWindow(Engine,files).Show(this);return;
        }
        Window dialog=new ConvertWindow(Engine,feature,_settings.OutputFolder,files??[]);
        var result=await dialog.ShowDialog<ConversionRequest?>(this);if(result is null)return;
        if(result.ClipInputs is not null)
        {
            try{foreach(var job in QuickClipBatch.CreateJobs(result.ClipInputs,result.OutputFolder,result.OutputToSource,result.SettingName,_jobs.Select(j=>j.Output)))_jobs.Add(job);}
            catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}
            Save();Refresh();return;
        }
        try{foreach(var job in ConversionBatch.CreateJobs(result.Feature,result.Files,result.OutputFolder,result.Options,result.InputOptions,_jobs.Select(j=>j.Output)))_jobs.Add(job);}
        catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}
        Save();Refresh();
    }
    private void Save(){_settings.MultiThread=Multithread.IsChecked==true;_settings.NotifyComplete=Notify.IsChecked==true;_storage.SaveSettings(_settings);_storage.SaveJobs(_jobs);}
    private void Refresh()
    {
        StartButton.IsEnabled=!_queue.IsRunning && _jobs.Any(j=>j.State==JobState.Waiting);StopButton.IsEnabled=_queue.IsRunning;ClearButton.IsEnabled=_jobs.Count>0&&!_queue.IsRunning;RemoveButton.IsEnabled=JobList.SelectedItems?.Count>0&&!_queue.IsRunning;
        SummaryText.Text=_jobs.Count==0?"":$"{_jobs.Count} 个任务  ·  完成 {_jobs.Count(j=>j.State==JobState.Completed)}  ·  失败 {_jobs.Count(j=>j.State==JobState.Failed)}";
        JobDisplayChanged?.Invoke();
    }
    private async void StartClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        if(_queue.IsRunning)return;Save();_elapsed.Restart();_running=_queue.Run(_jobs,_settings.MultiThread?_settings.ParallelJobs:1);Refresh();await _running;_elapsed.Stop();Save();Refresh();
        if(!_closing && _settings.NotifyComplete && _jobs.Count>0 && _jobs.All(j=>j.State is JobState.Completed or JobState.Failed)) await Ui.Message(this,"转换完成",$"成功 {_jobs.Count(j=>j.State==JobState.Completed)} 个，失败 {_jobs.Count(j=>j.State==JobState.Failed)} 个。\n\n输出目录：{_settings.OutputFolder}");
    }
    private void StopClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>_queue.Stop();
    private async void AddClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>await Configure(_last);
    private void RemoveClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){if(_queue.IsRunning)return;foreach(var j in JobList.SelectedItems?.Cast<Job>().ToArray()??[])_jobs.Remove(j);Save();Refresh();}
    private void ClearClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){if(_queue.IsRunning)return;_jobs.Clear();Save();Refresh();}
    private void RetryClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {foreach(var j in JobList.SelectedItems?.Cast<Job>().Where(j=>j.CanRetry)??[]){bool directory=Catalog.Find(j.FeatureId).Operation is Operation.Frames or Operation.PdfSplit or Operation.Unzip;j.Output=MediaEngine.UniqueOutput(Path.GetDirectoryName(j.Output)!,Path.GetFileNameWithoutExtension(j.Output),j.Options.Format,_jobs.Select(x=>x.Output),directory);j.State=JobState.Waiting;j.Progress=0;}Save();Refresh();}
    private async void SettingsClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        var w=new SettingsWindow(_settings);
        w.Applied+=(_,_)=>{_storage.SaveSettings(_settings);OutputPath.Text="📂 "+_settings.OutputFolder;Multithread.IsChecked=_settings.MultiThread;Notify.IsChecked=_settings.NotifyComplete;Motion.SetReducedMotion(_settings.ReduceMotion);};
        await w.ShowDialog<bool>(this);
    }
    private async void OutputClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){try{Directory.CreateDirectory(_settings.OutputFolder);Open(_settings.OutputFolder);}catch(Exception ex){await Ui.Message(this,"打开目录失败",ex.Message);}}
    private static void Open(string path){if(Directory.Exists(path))PlatformServices.OpenFolder(path);else Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
    private async void OpenSelectedClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){if(JobList.SelectedItem is Job j){try{if(File.Exists(j.Output)||Directory.Exists(j.Output))Open(j.Output);else await Ui.Message(this,"输出文件","任务尚未生成输出。");}catch(Exception ex){await Ui.Message(this,"打开失败",ex.Message);}}}
    private async void RevealClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){if(JobList.SelectedItem is Job j){try{var path=Directory.Exists(j.Output)?j.Output:Path.GetDirectoryName(j.Output)!;Directory.CreateDirectory(path);Open(path);}catch(Exception ex){await Ui.Message(this,"打开失败",ex.Message);}}}
    private async void LogClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){if(JobList.SelectedItem is Job j)await Ui.Message(this,"任务日志",j.Status+"\n\n"+j.Error+"\n\n"+j.Log);}
    private async Task Edit(string path,ConversionOptions options,Job? job=null)
    {
        try
        {
            var w=new EditorWindow(Engine,path,options);var result=await w.ShowDialog<ConversionOptions?>(this);
            if(result is not null && job is not null && job.State!=JobState.Running){job.Options=result;job.State=JobState.Waiting;job.Progress=0;job.Output=MediaEngine.UniqueOutput(Path.GetDirectoryName(job.Output)!,Path.GetFileNameWithoutExtension(path),result.Format,_jobs.Select(j=>j.Output));Save();Refresh();}
        }
        catch(Exception ex){await Ui.Message(this,"媒体打开失败",ex.Message);}
    }
    private async void EditSelectedClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){if(JobList.SelectedItem is Job j)await EditJob(j);}
    private async void PreviewClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){e.Handled=true;if(sender is Control {DataContext:Job job})await EditJob(job);}
    internal async Task EditJob(Job j)
    {
        var feature=Catalog.Find(j.FeatureId);
        if(j.State==JobState.Running)return;
        if(j.State==JobState.Failed || j.Inputs.Length==0 || !File.Exists(j.Inputs[0]) || feature.Category is "文档" or "光驱设备\\DVD\\CD\\ISO" || feature.Operation is Operation.Info or Operation.Hash or Operation.Download or Operation.IsoCopy)
        {await Ui.Message(this,"任务详情",j.Source+"\n\n"+j.Output+"\n\n"+j.Error+"\n"+j.Log);return;}
        if(feature.Operation is Operation.Join or Operation.AudioMix or Operation.Mux)
        {
            var result=await new ConvertWindow(Engine,feature,Path.GetDirectoryName(j.Output)!,j.Inputs,j.Options,j.InputOptions).ShowDialog<ConversionRequest?>(this);
            if(result is null)return;j.FeatureId=result.Feature.Id;j.Inputs=result.Files;j.Options=result.Options;j.InputOptions=result.InputOptions?.Select(o=>o.Clone()).ToList();j.Output=MediaEngine.UniqueOutput(result.OutputFolder,Path.GetFileNameWithoutExtension(j.Inputs[0]),j.Options.Format,_jobs.Select(x=>x.Output));j.State=JobState.Waiting;j.Progress=0;Save();Refresh();return;
        }
        await Edit(j.Inputs[0],j.Options,j);
    }
    private async void JobDoubleClick(object? sender,TappedEventArgs e)
    {
        if(JobList.SelectedItem is not Job j)return;
        await EditJob(j);
    }
    private void JobSelectionChanged(object? sender,SelectionChangedEventArgs e)=>Refresh();
    private async void ExportQueueClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {var file=await StorageProvider.SaveFilePickerAsync(new(){Title="保存任务列表",SuggestedFileName="AvaMedia-queue.json",DefaultExtension="json"});if(file?.TryGetLocalPath() is {} path)await File.WriteAllTextAsync(path,JsonSerializer.Serialize(_jobs,new JsonSerializerOptions{WriteIndented=true}));}
    private async void ImportQueueClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files=await Ui.Pick(this,"载入任务列表",false);if(files.Length==0)return;
        try{var jobs=JsonSerializer.Deserialize<List<Job>>(await File.ReadAllTextAsync(files[0]))??[];foreach(var j in jobs){Catalog.Find(j.FeatureId);if(j.Inputs is null || j.Options is null || string.IsNullOrWhiteSpace(j.Output))throw new InvalidDataException("任务列表格式无效。");j.Id=Guid.NewGuid();if(j.State==JobState.Running)j.State=JobState.Cancelled;}foreach(var j in jobs)_jobs.Add(j);Save();Refresh();}catch(Exception ex){await Ui.Message(this,"载入失败",ex.Message);}
    }
    private void ExitClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>Close();
    private void SetSkin(string theme){Skin.Apply(theme);_settings.Theme=theme;Save();}
    private void LightClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>SetSkin("Light");
    private void DarkClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>SetSkin("Dark");
    private void MacOS9Click(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>SetSkin("MacOS9");
    private void ChineseClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){if(sender is MenuItem item)item.IsChecked=true;}
    private async void HelpClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>await Ui.Message(this,"使用说明","1. 点击左侧格式或工具，添加文件，设置参数并确定。\n2. 点击“开始”执行队列。右键任务可编辑、重试、查看日志和打开输出目录。\n3. 快速剪辑：先选视频直接编辑，可添加多个片段、裁剪画面、旋转或识别人脸方向，再选择导出选项并加入队列。每个片段分别导出，返回编辑保留草稿。\n4. 使用选项指定 FFmpeg / FFprobe 路径。下载需要 yt-dlp。录屏支持 Windows 桌面画面，时长可设置。\n5. PDF → DOCX/XLSX 提取文本，扫描 PDF 需要另行 OCR；不保留原始排版。\n6. ISO 复制需要光驱读取权限。DVD 转换请选择未加密 VOB 文件。\n\n更完整的能力与限制见工程 docs/FEATURES.md。");
    private async void AboutClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>await Ui.Message(this,"关于 AvaMedia",$"AvaMedia {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\nAvalonia + C# 多媒体工具\n\n独立实现的客户端，界面布局参考 FormatFactory 5.10.0。\n版权所有 © 2026 AvaMedia contributors。\n原创代码和矢量图标采用 AGPL-3.0-only 许可证。\n本程序不提供担保，可按该许可证修改和再分发。\n许可全文见 LICENSE；源码见 https://github.com/mcxen/AvaMedia。\n\nAvalonia: MIT · NAudio: MIT · PDFsharp: MIT · PdfPig: Apache-2.0\nFFmpeg 通过独立进程调用，许可证取决于用户配置的构建。\nyt-dlp 为可选外部工具，其打包版本还包含第三方依赖。\n\n完整版权声明见 THIRD-PARTY-NOTICES.md 和 licenses/。\nFormatFactory 名称及原产品资源归各权利人所有。");
    private void DragOver(object? sender,DragEventArgs e)=>e.DragEffects=DragDropEffects.Copy;
    private async void Drop(object? sender,DragEventArgs e){var files=e.DataTransfer.TryGetFiles()?.Select(f=>f.TryGetLocalPath()).OfType<string>().Where(File.Exists).ToArray()??[];if(files.Length>0)await Configure(_last,files);}
}
