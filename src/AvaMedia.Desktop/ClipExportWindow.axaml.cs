using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed record ClipExportState(string Preset,string Folder,bool OutputToSource,ConversionOptions Options,bool AddSettingName=false);
public sealed record ClipExportDecision(bool BackToEditing,ClipExportState State,ConversionRequest? Request=null);

public partial class ClipExportWindow : Window
{
    private readonly ClipEditResult[] _edits;
    private ConversionOptions _options;
    public ClipExportWindow() : this([],MediaFolders.DefaultOutput) { }
    public ClipExportWindow(IEnumerable<ClipEditResult> edits,string folder,ClipExportState? state=null)
    {
        InitializeComponent();_edits=edits.ToArray();_options=state?.Options.Clone()??new();
        Avalonia.Automation.AutomationProperties.SetName(FormatCombo,"快速剪辑输出格式");
        Avalonia.Automation.AutomationProperties.SetName(ExportFolder,"快速剪辑保存位置");
        Avalonia.Automation.AutomationProperties.SetName(ExportSegments,"待导出的剪辑片段");
        Localization.SetText(ExportSummary,$"{_edits.Length} 个视频 · {_edits.Sum(e=>e.Segments.Count)} 个片段");
        ExportSegments.ItemsSource=_edits.SelectMany(edit=>edit.Segments.Select((segment,i)=>Path.GetFileName(edit.Path)+"\n"+new ClipSegmentEntry(segment){Number=i+1}.Summary)).ToArray();
        ExportFolder.Text=state?.Folder??folder;OutputToSource.IsChecked=state?.OutputToSource??false;
        AddSettingName.IsChecked=state?.AddSettingName??false;
        FormatCombo.ItemsSource=QuickClipBatch.Presets;FormatCombo.SelectedItem=state?.Preset??"MP4";
        ExportFolder.PropertyChanged+=(_,e)=>{if(e.Property==TextBox.TextProperty)ValidateExport();};
        SetOutputLocation();ValidateExport();
    }
    private string Preset=>FormatCombo.SelectedItem as string??"MP4";
    public ClipExportState ReadState()=>new(Preset,ExportFolder.Text?.Trim()??"",OutputToSource.IsChecked==true,_options.Clone(),AddSettingName.IsChecked==true);
    public ConversionRequest CreateRequest()
    {
        var state=ReadState();
        if(!state.OutputToSource && string.IsNullOrWhiteSpace(state.Folder))throw new ArgumentException("请选择保存位置。");
        if(!state.OutputToSource)_=Path.GetFullPath(state.Folder);
        var inputs=QuickClipWorkflow.PrepareExports(_edits,state.Preset,state.Options);
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
        if(FormatNote is null)return;
        FormatNote.Text=Preset=="Fast Copy"?"Fast Copy 剪辑边界受关键帧限制。":"";
        FormatNote.IsVisible=Preset=="Fast Copy";
        ValidateExport();
    }
    private void OutputLocationChanged(object? sender,RoutedEventArgs e){SetOutputLocation();ValidateExport();}
    private void SetOutputLocation(){if(ExportFolder is null)return;ExportFolder.IsEnabled=BrowseFolderButton.IsEnabled=OutputToSource.IsChecked!=true;}
    private async void BrowseFolderClick(object? sender,RoutedEventArgs e){if(await Ui.Folder(this,"选择导出文件夹") is {} folder)ExportFolder.Text=folder;}
    private async void ExportOptionsClick(object? sender,RoutedEventArgs e)
    {
        var options=_options.Clone();options.Format=Preset=="Fast Copy"?"mp4":Preset.ToLowerInvariant();
        if(await new OptionsWindow(options,copyStreamsMode:Preset=="Fast Copy",kind:MediaOptionsKind.ClipExport).ShowDialog<ConversionOptions?>(this) is {} result)
        {_options=result;ValidateExport();}
    }
    private void BackClick(object? sender,RoutedEventArgs e)=>Close(new ClipExportDecision(true,ReadState()));
    private void CancelClick(object? sender,RoutedEventArgs e)=>Close(null);
    private void ConfirmClick(object? sender,RoutedEventArgs e)
    {
        try{Close(new ClipExportDecision(false,ReadState(),CreateRequest()));}
        catch(Exception ex){ExportError.Text=ex.Message;JoinQueueButton.IsEnabled=false;}
    }
}
