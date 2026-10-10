using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private HelpWindow? _helpWindow;
    private Task _running=Task.CompletedTask;
    private Task _queueSave=Task.CompletedTask;
    public Task PersistenceReady => _queueSave;
    public IMediaEngine Engine {get;}
    internal event Action? JobDisplayChanged;
    internal event Action<bool>? JobPresentationChanged;
    internal bool IsQueuePresentationVisible => IsVisible && WindowState != WindowState.Minimized && !_closing;
    public MainWindow() : this(new Storage()) { }
    public MainWindow(Storage storage, IMediaEngine? engine=null, IAppOptionsServices? optionServices=null)
    {
        _optionServices=optionServices??new AppOptionsServices();
        _storage=storage??new();InitializeComponent();_settings=_storage.LoadSettings();Localization.Apply(_settings.Language);Skin.Apply(_settings.Theme);Motion.SetReducedMotion(_settings.ReduceMotion);Engine=engine??new MediaEngine(_settings);_queue=new(Engine);
        UpdateLanguageMenu();Localization.Changed+=LanguageChanged;
        Closed+=(_,_)=>Localization.Changed-=LanguageChanged;
        _jobs=new(_storage.LoadJobs());
        InitializeMcp();
        InitializeTaskManagement();
        InitializeOptions();
        RefreshOutputPath();Multithread.IsChecked=_settings.MultiThread;Notify.IsChecked=_settings.NotifyComplete;
        _queue.Changed+=QueueJobChanged;
        _timer=new(){Interval=TimeSpan.FromSeconds(1)};_timer.Tick+=(_,_)=>BackgroundTick();
        InitializeSystemResourceMonitor();
        InitializeModelActivity();
        InitializeNotifications();
        ShowCategory(_category);Refresh();
        InitializePlatinumPresentation();
        DragDrop.SetAllowDrop(this,true);
        AddHandler(DragDrop.DropEvent,Drop,Avalonia.Interactivity.RoutingStrategies.Bubble,handledEventsToo:true);
        AddHandler(DragDrop.DragOverEvent,DragOver,Avalonia.Interactivity.RoutingStrategies.Bubble,handledEventsToo:true);
    }
    private Task Configure(Feature feature, string[]? files = null) => StartToolWorkflow(() => ConfigureCoreAsync(feature, files));

    private async Task ConfigureCoreAsync(Feature feature,string[]? files=null)
    {
        if(feature.Operation==Operation.ImageView)
        {
            var viewer = new ImageViewerWindow(files); viewer.Show(this); return;
        }
        if(feature.Id=="folder-classification")
        {
            await ConfigureFolderClassificationAsync(files);
            return;
        }
        if(feature.Id=="media-ai")
        {
            await ConfigureMediaAiAsync(files);
            return;
        }
        if(feature.Id=="person-clip")
        {
            if(_settings.EnableBetaFeatures)await ConfigurePersonClipAsync(files);
            return;
        }
        _last=feature;
        if(feature.Operation==Operation.BatchTools){await ConfigureBatchToolsAsync(files,feature.Id=="contact-sheet");return;}
        if(feature.Operation==Operation.Download){await ConfigureDownloadAsync(files);return;}
        if(feature.Operation==Operation.VideoCompress){await ConfigureVideoCompressionAsync(files);return;}
        if(feature.Operation==Operation.VideoSlim){await ConfigureVideoSlimmingAsync(files);return;}
        if(feature.Operation==Operation.VideoSummary){await ConfigureVideoSummaryAsync(files);return;}
        if(feature.Operation==Operation.ImageCompress){await ConfigureImageCompressionAsync(files);return;}
        if(feature.Id=="clip")
        {
            if(files is null)await PickQuickClipVideos();else await EditQuickClipAsync(files);
            return;
        }
        if(feature.Id=="rotate")
        {
            await ConfigureRotateAsync(files); return;
        }
        if(feature.Id=="crop")
        {
            var window=new BatchCropWindow(Engine,_settings.OutputFolder,files);
            var request=await ToolExecution.ShowAsync<BatchCropRequest>(this, window);
            if(request is null)return;
            try{var jobs=BatchCrop.CreateJobs(request,_jobs.Select(j=>j.Output));OutputPreferences.Apply(jobs,_settings,_jobs.Select(j=>j.Output),request.OutputToSource,request.SettingName);AddToolJobs(jobs,ToolExecution.StartImmediately(window));}
            catch(Exception ex){await Ui.Message(this,"批量裁剪参数错误",ex.Message);}
            return;
        }
        if(PdfTools.Supports(feature.Operation))
        {
            var pdfWindow = new PdfWorkspaceWindow(feature,_settings.OutputFolder,files,engine:Engine);
            var pdfResult=await ToolExecution.ShowAsync<PdfWorkspaceRequest>(this, pdfWindow);
            if(pdfResult is null)return;
            try
            {
                var jobs=ConversionBatch.CreateJobs(feature,pdfResult.Files,pdfResult.OutputFolder,pdfResult.Options,reserved:_jobs.Select(j=>j.Output));
                OutputPreferences.Apply(jobs,_settings,_jobs.Select(j=>j.Output),outputToSource:false,settingName:"");
                AddToolJobs(jobs, ToolExecution.StartImmediately(pdfWindow));
            }
            catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}
            return;
        }
        if(feature.Operation==Operation.Info)
        {
            files??=await Ui.Pick(this,"选择媒体文件",true);new MediaInfoWindow(Engine,files).Show(this);return;
        }
        if(feature.Operation==Operation.Player)
        {
            files??=await Ui.Pick(this,"打开媒体文件",true);if(files.Length>0)new PlayerWindow(Engine,files).ShowForPlayback(this);return;
        }
        if(feature.Operation==Operation.Transcribe || feature.Id is "voice-enhance" or "audio-enhance")
        { await ConfigureSpeechAsync(feature,files); return; }
        if(IsAiFeature(feature)) { await ConfigureAiConversionAsync(feature,files); return; }
        Window dialog=new ConvertWindow(Engine,feature,_settings.OutputFolder,files??[]);
        var result=await ToolExecution.ShowAsync<ConversionRequest>(this, dialog);if(result is null)return;
        if(result.ClipInputs is not null)
        {
            try{AddToolJobs(QuickClipBatch.CreateJobs(result.ClipInputs,result.OutputFolder,result.OutputToSource,result.SettingName,_jobs.Select(j=>j.Output)),result.StartImmediately);}
            catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}
            Save();Refresh();return;
        }
        try{var jobs=ConversionBatch.CreateJobs(result.Feature,result.Files,result.OutputFolder,result.Options,result.InputOptions,_jobs.Select(j=>j.Output));OutputPreferences.Apply(jobs,_settings,_jobs.Select(j=>j.Output),result.OutputToSource,result.SettingName);AddToolJobs(jobs,result.StartImmediately);}
        catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}
        Save();Refresh();
    }
    private void Save()
    {
        _settings.MultiThread=Multithread.IsChecked==true;_settings.NotifyComplete=Notify.IsChecked==true;
        _storage.SaveSettings(_settings); _queueSave=_storage.SaveJobsAsync(_jobs); _=ObserveQueueSaveAsync(_queueSave);
    }
    private async Task ObserveQueueSaveAsync(Task save)
    {
        try { await save; }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Queue persistence: " + ex); if (!_closing) SummaryText.Text=Localization.Format($"任务列表保存失败：{ex.Message}"); }
    }
    private void Refresh(bool refreshRows=true)
    {
        RefreshTaskState();
        if (_startupOptionsInitialized && !_backgroundWindowVisible) return;
        RefreshTaskList();
        UpdateElapsed();
        PresentDownloadSpeedMonitor();
        StartButton.IsEnabled=CanManageTasks && _jobs.Any(CanStartTask);StopButton.IsEnabled=_queue.IsRunning&&!_queue.IsStopping;RemoveButton.IsEnabled=SelectedJobs().Any(CanRemoveTask);
        UpdateTaskEditingActions();
        SummaryText.Text=_jobs.Count==0?"":Localization.Format($"{_jobs.Count} 个任务  ·  完成 {_jobs.Count(j=>j.State==JobState.Completed)}  ·  失败 {_jobs.Count(j=>j.State==JobState.Failed)}");
        if(refreshRows)JobDisplayChanged?.Invoke();
    }
    private async void StartClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
        => await StartQueueAsync();
    private void StopClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){_startsAfterSession.Clear();_queue.Stop();Save();Refresh();}
    private async void AddClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>await Configure(_last);
    private async void RemoveClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        try{await RemoveTasksAsync(SelectedJobs());}
        catch(Exception ex){await Ui.Message(this,"移除任务",ex.Message);}
    }
    private async void ClearClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        if(!CanClearAllTasks)return;
        try{await RemoveTasksAsync(_jobs.ToArray());}
        catch(Exception ex){await Ui.Message(this,"清空列表",ex.Message);}
    }
    private async void RetryClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        try{await RestartTasksAsync(SelectedJobs().Where(j=>j.State==JobState.Failed).ToArray());}
        catch(Exception ex){await Ui.Message(this,"重试任务",ex.Message);}
    }
    private async void SettingsClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        _appliedSettings=_settings.Clone();
        var w=new SettingsWindow(_settings,_optionServices);
        w.SetMcpStatus(() => _mcp?.Status ?? "已关闭");
        w.Applied+=(_,_)=>ApplyOptions();
        await w.ShowDialog<bool>(this);
    }
    private async void OutputClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){try{Directory.CreateDirectory(_settings.OutputFolder);Open(_settings.OutputFolder);}catch(Exception ex){await Ui.Message(this,"打开目录失败",ex.Message);}}
    private static void Open(string path){if(Directory.Exists(path))PlatformServices.OpenFolder(path);else Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
    private async void OpenSelectedClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){if(SelectedJobs() is [var j] && j.State==JobState.Completed){try{if(CanViewClassificationTask(j))await ShowClassificationTaskAsync(j);else if(j.HasInternalOutput)await ShowAiTaskAsync(j);else if(File.Exists(j.Output)||Directory.Exists(j.Output))Open(j.Output);}catch(Exception ex){await Ui.Message(this,"打开失败",ex.Message);}}}
    private async void RevealClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){if(SelectedJobs() is [var j]){try{var path=j.FeatureId=="folder-classification"?j.UserOutput:Directory.Exists(j.Output)?j.Output:Path.GetDirectoryName(j.Output)!;if(Directory.Exists(path))Open(path);}catch(Exception ex){await Ui.Message(this,"打开失败",ex.Message);}}}
    private async void LogClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        if(JobList.SelectedItem is not Job job)return;
        try{var log=await job.ReadLogAsync();if(!_closing)await Ui.Message(this,"任务日志",job.Status+"\n\n"+job.Error+"\n\n"+log);}
        catch(Exception ex){if(!_closing)await Ui.Message(this,"任务日志",ex.Message);}
    }
    private async void EditSelectedClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){if(JobList.SelectedItems?.Count==1 && JobList.SelectedItem is Job j)await EditJob(j);}
    private async void PreviewClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e){e.Handled=true;if(sender is Control {DataContext:Job job})await EditJob(job);}
    private async void JobDoubleClick(object? sender,TappedEventArgs e)
    {
        if(JobList.SelectedItems?.Count!=1 || JobList.SelectedItem is not Job j)return;
        if (CanViewAiTask(j)) await ShowAiTaskAsync(j);
        else await EditJob(j);
    }
    private void JobSelectionChanged(object? sender,SelectionChangedEventArgs e){if(!_refreshingTaskList)Refresh();}
    private async void ExportQueueClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file=await StorageProvider.SaveFilePickerAsync(new(){Title=Localization.Text("保存任务列表"),SuggestedFileName="AvaMedia-queue.json",DefaultExtension="json"});
        if(file?.TryGetLocalPath() is not {} path)return;
        try{await _storage.ExportJobsAsync(path,_jobs,includeNsfw:_settings.EnableNsfwContent);}
        catch(Exception ex){await Ui.Message(this,"保存任务列表",ex.Message);}
    }
    private async void ImportQueueClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files=await Ui.Pick(this,"载入任务列表",false);if(files.Length==0)return;
        try{var jobs=await _storage.ImportJobsAsync(files[0]);foreach(var job in jobs)_jobs.Add(job);Save();Refresh();}catch(Exception ex){await Ui.Message(this,"载入失败",ex.Message);}
    }
    private void ExitClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>RequestExit();
    private void SetSkin(string theme)
    {
        // Let the menu close and release its popup before replacing its theme and window frame.
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing) return;
            Skin.Apply(theme); _settings.Theme = theme; Save();
        }, DispatcherPriority.Normal);
    }
    private void LightClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>SetSkin("Light");
    private void DarkClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>SetSkin("Dark");
    private void MacOS9Click(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>SetSkin("MacOS9");
    private async void ChineseClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>await SetLanguage("zh-CN");
    private async void EnglishClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>await SetLanguage("en-US");
    private async void SystemLanguageClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>await SetLanguage("system");
    private async Task SetLanguage(string language)
    {
        _settings.Language=language;_storage.SaveSettings(_settings);_appliedSettings.Language=language;Localization.Apply(language);UpdateLanguageMenu();
        if(_settings.SystemContextMenu)
            try{_optionServices.SetContextMenu(true);}catch(Exception ex){await Ui.Message(this,"系统菜单更新失败",ex.Message);}
    }
    private void LanguageChanged(object? sender,EventArgs e){UpdateLanguageMenu();Refresh();}
    private void UpdateLanguageMenu()
    {
        Title=AppIdentity.WindowTitle;ChineseLanguageItem.IsChecked=_settings.Language=="zh-CN";
        EnglishLanguageItem.IsChecked=_settings.Language=="en-US";SystemLanguageItem.IsChecked=_settings.Language=="system";
    }
    private void HelpClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)
    {
        if(_helpWindow is { } existing){existing.Activate();return;}
        _helpWindow=new HelpWindow();
        _helpWindow.Closed+=(_,_)=>_helpWindow=null;
        _helpWindow.Show(this);
    }
    private async void AboutClick(object? sender,Avalonia.Interactivity.RoutedEventArgs e)=>await Ui.MessageFormatted(this,Localization.Format($"关于{Localization.Key(AppIdentity.ChineseName)}"),$"{Localization.Format($"{Localization.Key(AppIdentity.ChineseName+" · "+AppIdentity.EnglishName)} {AppIdentity.Version}")}\nAvalonia + C# 多媒体工具\n\n独立实现的客户端，界面布局参考 FormatFactory 5.10.0。\n版权所有 © 2026 AvaMedia contributors。\n原创代码和矢量图标采用 AGPL-3.0-only 许可证。\n本程序不提供担保，可按该许可证修改和再分发。\n许可全文见 LICENSE；源码见 https://github.com/mcxen/AvaMedia。\n\nAvalonia: MIT · NAudio: MIT · PDFsharp: MIT · PdfPig: Apache-2.0\n内置 FFmpeg 8.1.3 通过独立进程调用，采用 GPL-3.0-or-later；对应源码随 Release 提供。\n内置 yt-dlp 和 QuickJS-NG，保留各组件的第三方许可证。\n\n完整版权声明见 THIRD-PARTY-NOTICES.md 和 licenses/。\nFormatFactory 名称及原产品资源归各权利人所有。");
}
