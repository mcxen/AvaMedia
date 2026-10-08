using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed record ClipExportState(string Preset,string Folder,bool OutputToSource,ConversionOptions Options,bool AddSettingName=false,bool JoinSegments=false);
public sealed record ClipExportDecision(bool BackToEditing,ClipExportState State,ConversionRequest? Request=null);

public partial class ClipExportWindow : Window, ISegmentThumbnailSource
{
    private readonly ClipEditResult[] _edits;
    private readonly IMediaPreview _previewFrames;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _thumbnailGate = new(2, 2);
    private ConversionOptions _options;
    public ClipExportWindow() : this([],MediaFolders.DefaultOutput) { }
    public ClipExportWindow(IEnumerable<ClipEditResult> edits,string folder,ClipExportState? state=null,bool allowJoin=false,IMediaPreview? previewFrames=null)
    {
        _previewFrames=previewFrames??new MediaEngine(new());
        InitializeComponent();_edits=edits.ToArray();_options=state?.Options.Clone()??new();
        Closed+=(_,_)=>{_lifetime.Cancel();_lifetime.Dispose();};
        Avalonia.Automation.AutomationProperties.SetName(FormatCombo,"快速剪辑输出格式");
        Avalonia.Automation.AutomationProperties.SetName(ExportFolder,"快速剪辑保存位置");
        Avalonia.Automation.AutomationProperties.SetName(ExportSegments,"待导出的剪辑片段");
        Localization.SetText(ExportSummary,$"{_edits.Length} 个视频 · {_edits.Sum(e=>e.Segments.Count)} 个片段");
        ExportSegments.ItemsSource=_edits.SelectMany(edit=>edit.Segments.Select((segment,i)=>new ClipSegmentEntry(segment,edit.Path){Number=i+1,SourceDuration=edit.Info.Duration})).ToArray();
        ExportFolder.Text=state?.Folder??folder;OutputToSource.IsChecked=state?.OutputToSource??false;
        AddSettingName.IsChecked=state?.AddSettingName??false;
        JoinSegments.IsVisible=allowJoin;JoinSegments.IsChecked=allowJoin && state?.JoinSegments==true;
        JoinSegments.IsCheckedChanged+=(_,_)=>ValidateExport();
        FormatCombo.ItemsSource=QuickClipBatch.Presets;FormatCombo.SelectedItem=state?.Preset??"MP4";
        ExportFolder.PropertyChanged+=(_,e)=>{if(e.Property==TextBox.TextProperty)ValidateExport();};
        SetOutputLocation();ValidateExport();
    }
    async Task<byte[]> ISegmentThumbnailSource.ReadSegmentThumbnail(string path, ConversionOptions options, CancellationToken ct)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _thumbnailGate.WaitAsync(request.Token);
        try { return await _previewFrames.Thumbnail(path, options.Start, 176, 100, request.Token, pad: false, videoStreamIndex: options.VideoStreamIndex); }
        finally { _thumbnailGate.Release(); }
    }
    private string Preset=>FormatCombo.SelectedItem as string??"MP4";
    public ClipExportState ReadState()=>new(Preset,ExportFolder.Text?.Trim()??"",OutputToSource.IsChecked==true,_options.Clone(),AddSettingName.IsChecked==true,JoinSegments.IsVisible && JoinSegments.IsChecked==true);
    public ConversionRequest CreateRequest()
    {
        var state=ReadState();
        if(!state.OutputToSource && string.IsNullOrWhiteSpace(state.Folder))throw new ArgumentException("请选择保存位置。");
        if(!state.OutputToSource)_=Path.GetFullPath(state.Folder);
        var inputs=QuickClipWorkflow.PrepareExports(_edits,state.Preset,state.Options);
        if(state.JoinSegments)QuickClipWorkflow.ValidateJoinedExports(_edits,state.Preset);
        return new(Catalog.Find("clip"),_edits.Select(e=>e.Path).ToArray(),state.Folder,state.Options.Clone(),inputs,state.OutputToSource,state.AddSettingName?(state.Preset=="Fast Copy"?"FastCopy":state.Preset):"");
    }
    private void ValidateExport()
    {
        if(_edits is null || _options is null || JoinQueueButton is null)return;
        try{_=CreateRequest();ExportError.Text="";JoinQueueButton.IsEnabled=true;}
        catch(Exception ex){ExportError.Text=ex.Message;JoinQueueButton.IsEnabled=false;}
    }
    private void FormatChanged(object? sender,SelectionChangedEventArgs e)
    {
        RefreshFormatNote();ValidateExport();
    }
    private void RefreshFormatNote()
    {
        if(FormatNote is null || _options is null)return;
        FormatNote.Text=Localization.Text(Preset=="Fast Copy"?"原格式 / 原码率：直接复制音视频；剪辑边界受关键帧限制。":
            _options.VideoRateMode switch
            {
                VideoRateMode.Source=>"重新编码，默认参考源视频码率；可在编码与质量中修改。",
                VideoRateMode.Quality=>"按质量编码，输出体积可能增大。",
                _=>"按自定义视频码率编码。"
            });
        FormatNote.IsVisible=true;
    }
    private void OutputLocationChanged(object? sender,RoutedEventArgs e){SetOutputLocation();ValidateExport();}
    private void SetOutputLocation(){if(ExportFolder is null)return;ExportFolder.IsEnabled=BrowseFolderButton.IsEnabled=OutputToSource.IsChecked!=true;}
    private async void BrowseFolderClick(object? sender,RoutedEventArgs e){if(await Ui.Folder(this,"选择导出文件夹") is {} folder)ExportFolder.Text=folder;}
    private async void ExportOptionsClick(object? sender,RoutedEventArgs e)
    {
        var options=_options.Clone();options.Format=Preset=="Fast Copy"?"mp4":Preset.ToLowerInvariant();
        if(await new OptionsWindow(options,copyStreamsMode:Preset=="Fast Copy",kind:MediaOptionsKind.ClipExport).ShowDialog<ConversionOptions?>(this) is {} result)
        {_options=result;RefreshFormatNote();ValidateExport();}
    }
    private void BackClick(object? sender,RoutedEventArgs e)=>Close(new ClipExportDecision(true,ReadState()));
    private void CancelClick(object? sender,RoutedEventArgs e)=>Close(null);
    private void ConfirmClick(object? sender,RoutedEventArgs e)
    {
        try{Close(new ClipExportDecision(false,ReadState(),CreateRequest()));}
        catch(Exception ex){ExportError.Text=ex.Message;JoinQueueButton.IsEnabled=false;}
    }
}
