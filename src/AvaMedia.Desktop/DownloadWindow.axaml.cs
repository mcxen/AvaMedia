using System.Collections.ObjectModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
    public string Title=>_video?.Title??DisplayUrl;
    public string DisplayUrl=>DownloadLinks.Display(_video is { SourceUrl.Length: > 0 } v?v.SourceUrl:Url);
    public string Detail=>Title==DisplayUrl?Title:Title+Environment.NewLine+DisplayUrl;
    public bool HasDescription=>_video is not null;
    public string Description=>_video is {} v
        ? Localization.Join(" · ",new[]{Localization.Text(v.Platform),v.WebView?.Extension.ToUpperInvariant()??"",v.Uploader,v.Duration>0?MediaTime.Format(v.Duration):""}.Where(text=>text.Length>0))
        : "";
    public void Complete(DownloadVideo video,bool selected=true)
    {
        _video=video;if(video.IsLive)_error="直播暂不支持，请使用已发布的视频链接。";
        Refresh();IsChecked=selected&&IsReady;
    }
    public void Fail(string message){_error=message;IsChecked=false;Refresh();}
    private void Refresh(){foreach(var name in new[]{nameof(IsReady),nameof(HasError),nameof(ErrorSummary),nameof(Title),nameof(DisplayUrl),nameof(Detail),nameof(Description),nameof(HasDescription)})Raise(name);}
}

public partial class DownloadWindow : Window
{
    private readonly ObservableCollection<DownloadEntry> _entries=[];
    private readonly IVideoDownloadService _service;
    private readonly bool _editing;
    private readonly HashSet<string> _capturedSnapshots = [];
    private readonly HashSet<string> _retainedSnapshots = [];
    private readonly int[] _qualityHeights;
    private string _inspectedText="";
    private readonly CancellationTokenSource _lifetime=new();
    private CancellationTokenSource? _inspection;
    private bool _busy,_closed,_checking,_selectingEntry;
    private Task _ready=Task.CompletedTask;
    private readonly DispatcherTimer _autoInspect = new() { Interval = TimeSpan.FromMilliseconds(600) };
    public Task Ready=>_ready;
    public IReadOnlyList<DownloadEntry> Entries=>_entries;
    public DownloadWindow() : this(new(),MediaFolders.DefaultOutput) { }
    public DownloadWindow(AppSettings settings,string folder,IEnumerable<string>? links=null,IVideoDownloadService? service=null,Job? editingJob=null)
    {
        InitializeComponent();_service=service??new VideoDownloadService(settings);_editing=editingJob is not null;
        var options=editingJob?.Options.Download??new();
        _qualityHeights=new[]{0,2160,1440,1080,720,480,360}.Append(options.MaxHeight).Distinct().OrderBy(height=>height==0?int.MaxValue:height).Reverse().ToArray();
        DownloadList.ItemsSource=_entries;DownloadFolder.Text=folder;LinksInput.Text=string.Join(Environment.NewLine,links??[]);
        DownloadFormat.ItemsSource=new[]{"MP4 视频","MKV 视频","MP3 音频","M4A 音频"};DownloadFormat.SelectedIndex=Math.Max(0,Array.IndexOf(new[]{"mp4","mkv","mp3","m4a"},editingJob?.Options.Format??"mp4"));
        DownloadQuality.ItemsSource=_qualityHeights.Select(height=>height switch{0=>"最佳",2160=>"2160p / 4K",1440=>"1440p / 2K",_=>height+"p"}).ToArray();DownloadQuality.SelectedIndex=Array.IndexOf(_qualityHeights,options.MaxHeight);
        DownloadSubtitles.ItemsSource=new[]{"不保存字幕","人工字幕","含自动字幕"};DownloadSubtitles.SelectedIndex=options.Subtitles?options.AutoSubtitles?2:1:0;
        CookieSource.ItemsSource=new[]{"不读取登录态","Firefox","Chrome","Edge","Safari","Brave","cookies.txt 文件","浏览器 CDP","内嵌浏览器"};CookieSource.SelectedIndex=options.UseWebViewCookies?8:options.UseBrowserCookies?7:options.CookieFile.Length>0?6:Math.Max(0,Array.IndexOf(new[]{"","firefox","chrome","edge","safari","brave"},options.CookieBrowser));
        BrowserEndpoint.Text=options.Browser?.Endpoint??options.CdpEndpoint;
        CookieFileInput.Text=options.CookieFile;DownloadProxy.Text=options.Proxy;SaveMetadata.IsChecked=options.Metadata;ExpandPlaylist.IsChecked=options.ExpandPlaylist;
        if(editingJob is not null)
        {
            Title="编辑下载任务";AddDownloadsButton.Content="保存修改";
            ExpandPlaylist.IsChecked=false;ExpandPlaylist.IsVisible=false;
            OutputNameLabel.IsVisible=DownloadOutputName.IsVisible=DownloadExtension.IsVisible=true;
            DownloadOutputName.Text=Path.GetFileNameWithoutExtension(editingJob.Output);
            LinksInput.Text=string.Join(Environment.NewLine,editingJob.Inputs);_inspectedText=LinksInput.Text.Trim();
            if(editingJob.Inputs.FirstOrDefault() is {} url)
            {
                var entry=new DownloadEntry(url);
                entry.Complete(new(options.WebView?.MediaUrl??url,"",editingJob.Name,"",editingJob.Duration,options.WebView is null?DownloadLinks.Platform(url):"内嵌浏览器",Browser:options.Browser,SourceUrl:options.WebView?.PageUrl??"",WebView:options.WebView));AddEntry(entry);
            }
        }
        FormatChanged(null,null!);
        AutomationProperties.SetName(LinksInput,"视频链接或完整分享文本");AutomationProperties.SetName(DownloadList,"解析出的可下载视频");
        AutomationProperties.SetName(DownloadQuality,"下载最高画质");AutomationProperties.SetName(CookieSource,"下载登录态来源");
        AutomationProperties.SetName(DownloadProxy,"下载代理");AutomationProperties.SetName(DownloadFolder,"下载保存位置");
        AutomationProperties.SetName(DownloadOutputName,"下载文件名");
        AutomationProperties.SetName(DownloadFormat,"保存内容");AutomationProperties.SetName(DownloadSubtitles,"字幕（中 / 英）");
        AutomationProperties.SetName(BrowserEndpoint,"浏览器 CDP");AutomationProperties.SetName(CookieFileInput,"Cookie 文件");
        foreach(var control in new TextBox[]{DownloadFolder,DownloadProxy,CookieFileInput,DownloadOutputName})control.PropertyChanged+=(_,e)=>{if(e.Property==TextBox.TextProperty)RefreshSelection();};
        _autoInspect.Tick+=(_,_)=>{_autoInspect.Stop();if(!_busy&&!_closed&&!LinksUnchanged&&HasDirectLinks())_ready=InspectAsync();};
        LinksInput.PropertyChanged+=(_,e)=>
        {
            if(e.Property!=TextBox.TextProperty)return;
            _autoInspect.Stop();
            if(!_busy){_entries.Clear();InspectStatus.Text="";SetError("");if(HasDirectLinks())_autoInspect.Start();}
            RefreshSelection();
        };
        DragDrop.SetAllowDrop(LinksInput,true);LinksInput.AddHandler(DragDrop.DropEvent,DropLinks);
        Closed+=(_,_)=>
        {
            _closed=true;_autoInspect.Stop();_inspection?.Cancel();_lifetime.Cancel();
            foreach(var id in _capturedSnapshots.Except(_retainedSnapshots))
                try{File.Delete(WebViewCookieStore.PathFor(id));}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){AppDiagnostics.Record("Browser cookies cleanup",ex);}
        };
        Opened+=(_,_)=>{if(!_editing&&HasDirectLinks())_ready=InspectAsync();};
        NetworkOptions.IsExpanded=CookieSource.SelectedIndex!=0||options.Proxy.Length>0||BrowserEndpoint.Text!=BrowserVideoCapture.DefaultEndpoint;
        RefreshSelection();
    }

    public DownloadOptions ReadOptions()=>new()
    {
        MaxHeight=DownloadQualityPanel.IsVisible?_qualityHeights[Math.Max(0,DownloadQuality.SelectedIndex)]:0,
        ExpandPlaylist=ExpandPlaylist.IsChecked==true,
        CookieBrowser=CookieSource.SelectedIndex switch{1=>"firefox",2=>"chrome",3=>"edge",4=>"safari",5=>"brave",_=>""},
        CookieFile=CookieSource.SelectedIndex==6?CookieFileInput.Text?.Trim()??"":"",
        Proxy=DownloadProxy.Text?.Trim()??"",Subtitles=DownloadSubtitlesPanel.IsVisible&&DownloadSubtitles.SelectedIndex>0,
        AutoSubtitles=DownloadSubtitlesPanel.IsVisible&&DownloadSubtitles.SelectedIndex==2,Metadata=SaveMetadata.IsChecked==true,UseBrowserCookies=CookieSource.SelectedIndex==7,
        UseWebViewCookies=CookieSource.SelectedIndex==8,
        CdpEndpoint=BrowserEndpoint.Text?.Trim()??BrowserVideoCapture.DefaultEndpoint
    };
    public VideoDownloadRequest ReadRequest()
    {
        if(!LinksUnchanged)throw new ArgumentException("请解析修改后的链接。");
        var options=ReadOptions();options.Validate();
        var videos=_entries.Where(e=>e.IsChecked&&e.IsReady).Select(e=>e.Video!).ToArray();
        if(videos.Length==0)throw new ArgumentException("请先解析链接并选择视频。");
        if(options.UseBrowserCookies&&videos.Any(video=>video.Browser is null))throw new ArgumentException("请先从浏览器识别视频，再使用 CDP 登录态。");
        if(options.UseWebViewCookies&&videos.Any(video=>video.WebView is null))throw new ArgumentException("请先在内嵌浏览器嗅探视频。");
        if(_editing && videos.Length!=1)throw new ArgumentException("编辑任务时请选择一个视频。");
        var folder=DownloadFolder.Text?.Trim()??"";if(folder.Length==0)throw new ArgumentException("请选择保存位置。");_=Path.GetFullPath(folder);
        var name=_editing?DownloadOutputName.Text?.Trim()??"":"";if(_editing)DownloadBatch.ValidateOutputName(name);
        return new(videos,folder,new[]{"mp4","mkv","mp3","m4a"}[Math.Max(0,DownloadFormat.SelectedIndex)],options,name);
    }

    private bool LinksUnchanged=>string.Equals(LinksInput.Text?.Trim()??"",_inspectedText,StringComparison.Ordinal);
    private bool HasDirectLinks()
    {
        try{var urls=DownloadLinks.Extract(LinksInput.Text);return urls.Count>0&&urls.All(url=>DownloadLinks.IsFileditchPage(url)||DownloadLinks.MediaExtension(url).Length>0);}
        catch(ArgumentException){return false;}
    }

    private void InspectClick(object? sender,RoutedEventArgs e)
    {
        if(_busy){_inspection?.Cancel();InspectButton.IsEnabled=false;}
        else if(_entries.Any(entry=>entry.Video?.WebView is not null))BrowserCaptureClick(sender,e);
        else _ready=InspectAsync();
    }
    private void RetryFailedClick(object? sender,RoutedEventArgs e)=>_ready=InspectAsync(retryFailed:true);
    public async Task InspectAsync(bool retryFailed=false)
    {
        if(_busy)return;
        retryFailed&=LinksUnchanged;
        IReadOnlyList<string> urls;DownloadOptions options;
        try{urls=retryFailed?_entries.Where(e=>e.HasError).Select(e=>e.Url).Distinct(StringComparer.Ordinal).ToArray():DownloadLinks.Extract(LinksInput.Text);if(urls.Count==0)throw new ArgumentException("没有找到 HTTP / HTTPS 链接，请粘贴视频分享文本。");options=ReadOptions();options.Validate();}
        catch(Exception ex){SetError(ex.Message);return;}
        var contexts=_entries.Where(entry=>entry.Video?.Browser is not null).ToDictionary(entry=>entry.Video!.Url,entry=>entry.Video!.Browser,StringComparer.Ordinal);
        _inspection=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);var token=_inspection.Token;
        _autoInspect.Stop();
        SetBusy(true);if(retryFailed){foreach(var failed in _entries.Where(e=>e.HasError).ToArray())_entries.Remove(failed);}else {_entries.Clear();_inspectedText=LinksInput.Text?.Trim()??"";}SetError("");var truncated=false;
        try
        {
            var seen=new HashSet<string>(_entries.Where(e=>e.Video is not null).Select(e=>e.Video!.Url),StringComparer.Ordinal);
            for(var i=0;i<urls.Count;i++)
            {
                token.ThrowIfCancellationRequested();Localization.SetText(InspectStatus,$"正在解析 {i+1}/{urls.Count} · {DownloadLinks.Platform(urls[i])}");
                var pending=new DownloadEntry(urls[i]);AddEntry(pending);
                try
                {
                    var result=await _service.InspectAsync(urls[i],options with { Browser=contexts.GetValueOrDefault(urls[i]) },token);if(_closed)return;token.ThrowIfCancellationRequested();
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
            InspectStatus.Text=truncated?"已限制为前 100 项":"";
        }
        catch(OperationCanceledException){if(!_closed)InspectStatus.Text="解析已取消";}
        finally{_inspection.Dispose();_inspection=null;if(!_closed){SetBusy(false);RefreshSelection();}}
    }

    private void AddEntry(DownloadEntry entry)
    {
        if(_editing && _entries.Any(e=>e.IsChecked))entry.IsChecked=false;
        entry.PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName!=nameof(DownloadEntry.IsChecked)||_checking)return;
            if(_editing && entry.IsChecked && !_selectingEntry)
            {
                _selectingEntry=true;
                foreach(var other in _entries.Where(e=>e!=entry))other.IsChecked=false;
                _selectingEntry=false;
            }
            RefreshSelection();
        };
        _entries.Add(entry);
    }
    private void SetBusy(bool value)
    {
        _busy=value;
        ExternalBrowserCaptureButton.IsEnabled=BrowserCaptureButton.IsEnabled=PasteLinksButton.IsEnabled=LinksInput.IsEnabled=DownloadSettingsPanel.IsEnabled=DownloadList.IsEnabled=ExpandPlaylist.IsEnabled=!value;
        InspectButton.Content=value?"取消解析":"解析链接";
        RefreshSelection();
    }
    private void RefreshSelection()
    {
        if(SelectionSummary is null)return;
        var ready=_entries.Count(e=>e.IsReady);var selected=_entries.Count(e=>e.IsChecked&&e.IsReady);var errors=_entries.Count(e=>e.HasError);
        SelectionSummary.Text=Localization.Join(" · ",new[]
        {
            ready>0?Localization.Format($"选中 {selected}/{ready} 个视频"):"",
            errors>0?Localization.Format($"{errors} 项未能解析"):""
        }.Where(text=>text.Length>0));
        EmptyState.IsVisible=_entries.Count==0;
        SelectionToolbar.IsVisible=ready+errors>0;
        SelectAllCheck.IsVisible=!_editing&&ready>1;SelectAllCheck.IsEnabled=!_busy;
        _checking=true;SelectAllCheck.IsChecked=selected==0?false:selected==ready?true:null;_checking=false;
        AddDownloadsButton.IsEnabled=!_busy&&LinksUnchanged&&selected>0&&(!_editing || selected==1&&!string.IsNullOrWhiteSpace(DownloadOutputName.Text))&&!string.IsNullOrWhiteSpace(DownloadFolder.Text);
        RetryFailedButton.IsVisible=!_busy&&LinksUnchanged&&errors>0;
        InspectButton.IsEnabled=_busy?_inspection?.IsCancellationRequested==false:!string.IsNullOrWhiteSpace(LinksInput.Text);
        ClearLinksButton.IsEnabled=!_busy&&(!string.IsNullOrWhiteSpace(LinksInput.Text)||_entries.Count>0);
        UpdateQualitySelection();
    }
    private void SelectAllChanged(object? sender,RoutedEventArgs e)
    {
        if(_checking||_busy)return;
        var selected=SelectAllCheck.IsChecked==true;_checking=true;
        foreach(var entry in _entries.Where(e=>e.IsReady))entry.IsChecked=selected;
        _checking=false;RefreshSelection();
    }
    private void VideoRowPressed(object? sender,PointerPressedEventArgs e)
    {
        if(_busy||!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed||sender is not Control{DataContext:DownloadEntry{IsReady:true} entry})return;
        entry.IsChecked=!entry.IsChecked;e.Handled=true;
    }
    private async void BrowserCaptureClick(object? sender,RoutedEventArgs e)
    {
        if(_busy)return;
        _autoInspect.Stop();SetBusy(true);SetError("");
        try
        {
            var url=DownloadLinks.Extract(LinksInput.Text,preservePageFragments:true).FirstOrDefault()??"";
            var videos=await new BrowserDownloadWindow(url,_editing).ShowDialog<IReadOnlyList<DownloadVideo>?>(this);
            if(_closed||videos is null)return;
            foreach(var video in videos)if(video.WebView is {} web)_capturedSnapshots.Add(web.CookieSnapshotId);
            _entries.Clear();
            LinksInput.Text=string.Join(Environment.NewLine,videos.Select(video=>video.SourceUrl).Distinct(StringComparer.Ordinal));
            _inspectedText=LinksInput.Text.Trim();
            foreach(var video in videos){var entry=new DownloadEntry(video.Url);entry.Complete(video);AddEntry(entry);}
            CookieSource.SelectedIndex=8;InspectStatus.Text="";
        }
        catch(Exception ex){if(!_closed)SetError(ex.Message);}
        finally{if(!_closed){SetBusy(false);RefreshSelection();}}
    }
    private async void ExternalBrowserCaptureClick(object? sender,RoutedEventArgs e)
    {
        if(_busy)return;
        _autoInspect.Stop();_inspection=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        SetBusy(true);SetError("");InspectStatus.Text="正在识别浏览器视频";
        try
        {
            var urls=DownloadLinks.Extract(LinksInput.Text);
            var result=await new BrowserVideoCapture().CaptureAsync(BrowserEndpoint.Text??"",urls,_inspection.Token);
            if(_closed)return;
            _entries.Clear();
            var videos=result.Videos;
            LinksInput.Text=string.Join(Environment.NewLine,videos.Select(video=>video.SourceUrl.Length>0?video.SourceUrl:video.Url));_inspectedText=LinksInput.Text.Trim();
            foreach(var video in videos){var entry=new DownloadEntry(video.Url);entry.Complete(video);AddEntry(entry);}
            if(CookieSource.SelectedIndex is 0 or 8)CookieSource.SelectedIndex=7;
            InspectStatus.Text="";
        }
        catch(OperationCanceledException){if(!_closed)InspectStatus.Text="解析已取消";}
        catch(Exception ex){if(!_closed){InspectStatus.Text="";SetError(DownloadDiagnostics.Redact(ex.Message));}}
        finally{_inspection.Dispose();_inspection=null;if(!_closed){SetBusy(false);RefreshSelection();}}
    }
    private void FormatChanged(object? sender,SelectionChangedEventArgs e)
    {
        UpdateQualitySelection();
        if(DownloadExtension is not null)DownloadExtension.Text="."+new[]{"mp4","mkv","mp3","m4a"}[Math.Max(0,DownloadFormat.SelectedIndex)];
    }
    private void UpdateQualitySelection()
    {
        if(DownloadQualityPanel is null||DownloadSubtitlesPanel is null)return;
        var selected=_entries.Where(entry=>entry.IsChecked&&entry.IsReady).ToArray();
        var hasWebsiteOptions=selected.Length==0||selected.Any(entry=>entry.Video!.Platform is not ("视频直链" or "Fileditch" or "Bunkr" or "Pixeldrain" or "内嵌浏览器")||entry.Video.WebView?.Extension is "m3u8" or "mpd");
        DownloadQualityPanel.IsVisible=DownloadFormat.SelectedIndex<2&&hasWebsiteOptions;
        DownloadSubtitlesPanel.IsVisible=hasWebsiteOptions;
    }
    private void CookieSourceChanged(object? sender,SelectionChangedEventArgs e){if(CookieFilePanel is not null)CookieFilePanel.IsVisible=CookieSource.SelectedIndex==6;}
    private async void CookieFileClick(object? sender,RoutedEventArgs e)
    {
        var files=await StorageProvider.OpenFilePickerAsync(new(){Title = Localization.Text("选择 Netscape 格式 cookies.txt"),AllowMultiple=false,FileTypeFilter=[new(Localization.Text("Cookies 文本")){Patterns=["*.txt"]}]});
        if(files.FirstOrDefault()?.TryGetLocalPath() is {} path)CookieFileInput.Text=path;
    }
    private async void BrowseFolderClick(object? sender,RoutedEventArgs e){if(await Ui.Folder(this,"选择下载保存位置") is {} folder)DownloadFolder.Text=folder;}
    private async void PasteClick(object? sender,RoutedEventArgs e)
    {
        try{if(Clipboard is {} clipboard){using var data=await clipboard.TryGetDataAsync();if(data is not null && await data.TryGetTextAsync() is {} text)LinksInput.Text=text;}}
        catch(Exception ex){SetError(Localization.Format($"无法读取剪贴板：{ex.Message}"));}
    }
    private void DropLinks(object? sender,DragEventArgs e){if(!_busy && e.DataTransfer.TryGetText() is {} text){LinksInput.Text=text;e.Handled=true;}}
    private void ClearClick(object? sender,RoutedEventArgs e){if(_busy)return;LinksInput.Text="";_entries.Clear();InspectStatus.Text="";SetError("");RefreshSelection();}
    private void SetError(string message){DownloadError.Text=message;DownloadError.IsVisible=message.Length>0;}
    private void CancelClick(object? sender,RoutedEventArgs e)=>Close(null);
    private void ConfirmClick(object? sender,RoutedEventArgs e)
    {
        if(_busy)return;
        try
        {
            var request=ReadRequest();
            foreach(var video in request.Videos)if(video.WebView is {} web)_retainedSnapshots.Add(web.CookieSnapshotId);
            Close(request);
        }
        catch(Exception ex){SetError(ex.Message);}
    }
}
