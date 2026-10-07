using System.Collections.ObjectModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed class DownloadEntry(string url) : Observable
{
    private bool _checked;
    private DownloadVideo? _video;
    private string _error="";
    public string Url { get; }=url;
    public DownloadVideo? Video=>_video;
    public bool IsChecked { get=>_checked;set=>Set(ref _checked,value&&IsReady); }
    public bool IsReady=>_video is not null && !_video.IsLive && !HasError;
    public bool HasError=>_error.Length>0;
    public string ErrorSummary=>_error.Length>300?_error[..300]+"…":_error;
    public string Title=>_video?.Title??DownloadLinks.Platform(Url);
    public string DisplayUrl=>DownloadLinks.Display(Url);
    public string Description=>_video is {} v ? v.Platform+" · "+(v.Uploader.Length>0?v.Uploader+" · ":"")+(v.Duration>0?MediaTime.Format(v.Duration):"时长待下载时确认") : HasError?"解析失败":"等待解析";
    public void Complete(DownloadVideo video)
    {
        _video=video;if(video.IsLive)_error="直播暂不支持，请使用已发布的视频链接。";
        Refresh();IsChecked=IsReady;
    }
    public void Fail(string message){_error=message;IsChecked=false;Refresh();}
    private void Refresh(){foreach(var name in new[]{nameof(IsReady),nameof(HasError),nameof(ErrorSummary),nameof(Title),nameof(Description)})Raise(name);}
}

public partial class DownloadWindow : Window
{
    private readonly ObservableCollection<DownloadEntry> _entries=[];
    private readonly IVideoDownloadService _service;
    private readonly CancellationTokenSource _lifetime=new();
    private CancellationTokenSource? _inspection;
    private bool _busy,_closed,_checking;
    private Task _ready=Task.CompletedTask;
    public Task Ready=>_ready;
    public IReadOnlyList<DownloadEntry> Entries=>_entries;
    public DownloadWindow() : this(new(),MediaFolders.DefaultOutput) { }
    public DownloadWindow(AppSettings settings,string folder,IEnumerable<string>? links=null,IVideoDownloadService? service=null)
    {
        InitializeComponent();_service=service??new VideoDownloadService(settings);
        DownloadList.ItemsSource=_entries;DownloadFolder.Text=folder;LinksInput.Text=string.Join(Environment.NewLine,links??[]);
        DownloadFormat.ItemsSource=new[]{"MP4 视频","MKV 视频","MP3 音频","M4A 音频"};DownloadFormat.SelectedIndex=0;
        DownloadQuality.ItemsSource=new[]{"最佳","2160p / 4K","1440p / 2K","1080p","720p","480p","360p"};DownloadQuality.SelectedIndex=3;
        DownloadSubtitles.ItemsSource=new[]{"不保存字幕","人工字幕（中文 / 英文）","人工和自动字幕（中文 / 英文）"};DownloadSubtitles.SelectedIndex=0;
        CookieSource.ItemsSource=new[]{"不读取登录态","Firefox","Chrome","Edge","Safari","Brave","cookies.txt 文件"};CookieSource.SelectedIndex=0;
        AutomationProperties.SetName(LinksInput,"视频链接或完整分享文本");AutomationProperties.SetName(DownloadList,"解析出的可下载视频");
        AutomationProperties.SetName(DownloadQuality,"下载最高画质");AutomationProperties.SetName(CookieSource,"下载登录态来源");
        AutomationProperties.SetName(DownloadProxy,"下载代理");AutomationProperties.SetName(DownloadFolder,"下载保存位置");
        foreach(var control in new TextBox[]{DownloadFolder,DownloadProxy,CookieFileInput})control.PropertyChanged+=(_,e)=>{if(e.Property==TextBox.TextProperty)RefreshSelection();};
        DragDrop.SetAllowDrop(LinksInput,true);LinksInput.AddHandler(DragDrop.DropEvent,DropLinks);
        Closed+=(_,_)=>{_closed=true;_inspection?.Cancel();_lifetime.Cancel();};
        Opened+=async(_,_)=>
        {
            try
            {
                var path=MediaEngine.Resolve(settings.YtDlpPath,"yt-dlp");var version=await ProcessRunner.Run(path,["--version"],_lifetime.Token);
                if(!_closed)EngineStatus.Text=Localization.Format($"yt-dlp {(version.ExitCode==0?version.Output.Trim():"不可用")}");
            }
            catch(OperationCanceledException){}catch(Exception){if(!_closed)EngineStatus.Text="下载引擎未安装";}
        };
        RefreshSelection();PlatformHelp.Text=Help("YouTube");
    }

    public DownloadOptions ReadOptions()=>new()
    {
        MaxHeight=new[]{0,2160,1440,1080,720,480,360}[Math.Max(0,DownloadQuality.SelectedIndex)],
        ExpandPlaylist=ExpandPlaylist.IsChecked==true,
        CookieBrowser=CookieSource.SelectedIndex switch{1=>"firefox",2=>"chrome",3=>"edge",4=>"safari",5=>"brave",_=>""},
        CookieFile=CookieSource.SelectedIndex==6?CookieFileInput.Text?.Trim()??"":"",
        Proxy=DownloadProxy.Text?.Trim()??"",Subtitles=DownloadSubtitles.SelectedIndex>0,
        AutoSubtitles=DownloadSubtitles.SelectedIndex==2,Metadata=SaveMetadata.IsChecked==true
    };
    public VideoDownloadRequest ReadRequest()
    {
        var options=ReadOptions();options.Validate();
        var videos=_entries.Where(e=>e.IsChecked&&e.IsReady).Select(e=>e.Video!).ToArray();
        if(videos.Length==0)throw new ArgumentException("请先解析链接并选择视频。");
        var folder=DownloadFolder.Text?.Trim()??"";if(folder.Length==0)throw new ArgumentException("请选择保存位置。");_=Path.GetFullPath(folder);
        return new(videos,folder,new[]{"mp4","mkv","mp3","m4a"}[Math.Max(0,DownloadFormat.SelectedIndex)],options);
    }

    private void InspectClick(object? sender,RoutedEventArgs e)=>_ready=InspectAsync();
    private void RetryFailedClick(object? sender,RoutedEventArgs e)=>_ready=InspectAsync(retryFailed:true);
    public async Task InspectAsync(bool retryFailed=false)
    {
        if(_busy)return;
        IReadOnlyList<string> urls;DownloadOptions options;
        try{urls=retryFailed?_entries.Where(e=>e.HasError).Select(e=>e.Url).Distinct(StringComparer.Ordinal).ToArray():DownloadLinks.Extract(LinksInput.Text);if(urls.Count==0)throw new ArgumentException("没有找到 HTTP / HTTPS 链接，请粘贴视频分享文本。");options=ReadOptions();options.Validate();}
        catch(Exception ex){DownloadError.Text=ex.Message;return;}
        _inspection=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);var token=_inspection.Token;
        SetBusy(true);if(retryFailed){foreach(var failed in _entries.Where(e=>e.HasError).ToArray())_entries.Remove(failed);}else _entries.Clear();DownloadError.Text="";var truncated=false;
        try
        {
            var seen=new HashSet<string>(_entries.Where(e=>e.Video is not null).Select(e=>e.Video!.Url),StringComparer.Ordinal);
            for(var i=0;i<urls.Count;i++)
            {
                token.ThrowIfCancellationRequested();Localization.SetText(InspectStatus,$"正在解析 {i+1}/{urls.Count} · {DownloadLinks.Platform(urls[i])}");
                var pending=new DownloadEntry(urls[i]);AddEntry(pending);
                try
                {
                    var result=await _service.InspectAsync(urls[i],options,token);if(_closed)return;token.ThrowIfCancellationRequested();
                    _entries.Remove(pending);truncated|=result.Truncated;
                    foreach(var video in result.Videos)
                    {
                        if(!seen.Add(video.Url))continue;
                        if(_entries.Count>=100){truncated=true;break;}
                        var entry=new DownloadEntry(video.Url);entry.Complete(video);AddEntry(entry);
                    }
                }
                catch(OperationCanceledException){pending.Fail("已取消解析，可重新解析此链接。");throw;}
                catch(Exception ex){pending.Fail(DownloadDiagnostics.Redact(ex.Message));}
                RefreshSelection();
                if(_entries.Count>=100){truncated|=i<urls.Count-1;break;}
            }
            InspectStatus.Text=Localization.Join(" · ", new[] { Localization.Format($"解析完成 · {_entries.Count(e=>e.IsReady)} 个视频"), truncated ? "已限制为前 100 项" : "" }.Where(s=>s.Length>0));
        }
        catch(OperationCanceledException){if(!_closed)InspectStatus.Text="解析已取消，已完成的视频仍可加入队列。";}
        finally{_inspection.Dispose();_inspection=null;if(!_closed){SetBusy(false);RefreshSelection();}}
    }

    private void AddEntry(DownloadEntry entry){entry.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(DownloadEntry.IsChecked))RefreshSelection();};_entries.Add(entry);}
    private void SetBusy(bool value)
    {
        _busy=value;InspectButton.IsEnabled=PasteLinksButton.IsEnabled=ClearLinksButton.IsEnabled=LinksInput.IsEnabled=DownloadSettingsPanel.IsEnabled=DownloadList.IsEnabled=SelectAllCheck.IsEnabled=!value;
        CancelInspectButton.IsVisible=value;AddDownloadsButton.IsEnabled=!value&&_entries.Any(e=>e.IsChecked&&e.IsReady);
        RetryFailedButton.IsEnabled=!value&&_entries.Any(e=>e.HasError);
    }
    private void RefreshSelection()
    {
        if(SelectionSummary is null)return;
        var ready=_entries.Count(e=>e.IsReady);var selected=_entries.Count(e=>e.IsChecked&&e.IsReady);var errors=_entries.Count(e=>e.HasError);
        SelectionSummary.Text=Localization.Format($"选中 {selected}/{ready} 个视频{(errors>0?Localization.Format($" · {errors} 项未能解析"):"")}。加入队列后，主窗口“开始”执行。");
        EmptyState.IsVisible=_entries.Count==0;_checking=true;SelectAllCheck.IsChecked=ready>0&&selected==ready;_checking=false;
        AddDownloadsButton.IsEnabled=!_busy&&selected>0&&!string.IsNullOrWhiteSpace(DownloadFolder.Text);
        RetryFailedButton.IsEnabled=!_busy&&errors>0;
    }
    private void SelectAllChanged(object? sender,RoutedEventArgs e){if(_checking || _busy)return;var selected=SelectAllCheck.IsChecked==true;foreach(var entry in _entries.Where(e=>e.IsReady))entry.IsChecked=selected;RefreshSelection();}
    private void VideoSelected(object? sender,SelectionChangedEventArgs e){if(DownloadList.SelectedItem is DownloadEntry entry)PlatformHelp.Text=Help(entry.Video?.Platform??DownloadLinks.Platform(entry.Url));}
    private static string Help(string platform)=>platform switch
    {
        "YouTube"=>"YouTube：支持单视频和播放列表；网络不可达时设置代理。部分内容需要登录态。",
        "哔哩哔哩"=>"哔哩哔哩：分享短链可解析；分P可展开。高画质依赖账号可观看的清晰度。",
        "抖音"=>"抖音：使用视频分享链接。遇到登录或验证提示，请使用已登录浏览器的登录态。",
        "小红书"=>"小红书：保留分享链接里的 xsec_token 等参数。当前下载视频笔记，图文笔记不在此流程中。",
        "Bunkr"=>"Bunkr：支持单个视频和相册，勾选展开相册后可逐项选择视频。下载使用原文件画质，签名链接在开始下载时刷新。",
        "Pixeldrain"=>"Pixeldrain：支持单个视频和文件列表，保留 #item 参数可选择列表中的单项。下载使用原文件画质；限额或验证要求由站点决定。",
        _=>"其他网站由 yt-dlp 解析。网站支持和可用画质取决于当前引擎与视频访问状态。"
    };
    private void FormatChanged(object? sender,SelectionChangedEventArgs e){if(DownloadQuality is not null)DownloadQuality.IsEnabled=DownloadFormat.SelectedIndex<2;}
    private void CookieSourceChanged(object? sender,SelectionChangedEventArgs e){if(CookieFilePanel is not null)CookieFilePanel.IsVisible=CookieSource.SelectedIndex==6;}
    private void NetworkSettingsClick(object? sender,RoutedEventArgs e){CookieSource.BringIntoView();CookieSource.Focus();}
    private async void CookieFileClick(object? sender,RoutedEventArgs e)
    {
        var files=await StorageProvider.OpenFilePickerAsync(new(){Title = Localization.Text("选择 Netscape 格式 cookies.txt"),AllowMultiple=false,FileTypeFilter=[new(Localization.Text("Cookies 文本")){Patterns=["*.txt"]}]});
        if(files.FirstOrDefault()?.TryGetLocalPath() is {} path)CookieFileInput.Text=path;
    }
    private async void BrowseFolderClick(object? sender,RoutedEventArgs e){if(await Ui.Folder(this,"选择下载保存位置") is {} folder)DownloadFolder.Text=folder;}
    private async void PasteClick(object? sender,RoutedEventArgs e)
    {
        try{if(Clipboard is {} clipboard){using var data=await clipboard.TryGetDataAsync();if(data is not null && await data.TryGetTextAsync() is {} text)LinksInput.Text=text;}}
        catch(Exception ex){DownloadError.Text=Localization.Format($"无法读取剪贴板：{ex.Message}");}
    }
    private void DropLinks(object? sender,DragEventArgs e){if(!_busy && e.DataTransfer.TryGetText() is {} text){LinksInput.Text=text;e.Handled=true;}}
    private void CancelInspectClick(object? sender,RoutedEventArgs e)=>_inspection?.Cancel();
    private void ClearClick(object? sender,RoutedEventArgs e){if(_busy)return;LinksInput.Text="";_entries.Clear();InspectStatus.Text="";DownloadError.Text="";RefreshSelection();}
    private void RemoveVideoClick(object? sender,RoutedEventArgs e){if(!_busy && sender is Control{DataContext:DownloadEntry entry}){_entries.Remove(entry);RefreshSelection();}}
    private void CancelClick(object? sender,RoutedEventArgs e)=>Close(null);
    private void ConfirmClick(object? sender,RoutedEventArgs e){if(_busy)return;try{Close(ReadRequest());}catch(Exception ex){DownloadError.Text=ex.Message;}}
}
