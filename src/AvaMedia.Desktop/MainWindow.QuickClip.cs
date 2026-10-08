using Avalonia.Platform.Storage;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private async Task PickQuickClipVideos()
    {
        var files=await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Localization.Text("快速剪辑 · 选择要编辑的视频"),AllowMultiple=true,
            FileTypeFilter=[new FilePickerFileType(Localization.Text("视频文件")){Patterns=QuickClipBatch.VideoExtensions.Select(e=>"*."+e).ToArray()},FilePickerFileTypes.All]
        });
        await EditQuickClipAsync(files.Select(f=>f.TryGetLocalPath()).OfType<string>());
    }

    public async Task EditQuickClipAsync(IEnumerable<string> selectedPaths, IReadOnlyList<ClipEditResult>? initialEdits=null, bool allowJoin=false)
    {
        var paths=selectedPaths.Select(Path.GetFullPath).Distinct(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal).ToArray();
        if(paths.Length==0)return;
        _last=Catalog.Find("clip");
        var edits=paths.Select(path=>initialEdits?.FirstOrDefault(edit=>edit.Path==path)).ToArray();ClipExportState? exportState=new(QuickClipBatch.DefaultPreset,_settings.OutputFolder,_settings.OutputToSource,new(),_settings.AddSettingName,JoinSegments:allowJoin);
        while(true)
        {
            for(var i=0;i<paths.Length;i++)
            {
                var previous=edits[i];
                var editor=new EditorWindow(Engine,paths[i],previous?.Segments.FirstOrDefault()??new(),"quick-workflow",previous?.Segments);
                editor.SetWorkflowStep(i+1,paths.Length);
                var result=await editor.ShowDialog<ClipEditResult?>(this);
                if(result is null)return;
                edits[i]=result;
            }
            var decision=await new ClipExportWindow(edits.OfType<ClipEditResult>(),_settings.OutputFolder,exportState,allowJoin,previewFrames:Engine).ShowDialog<ClipExportDecision?>(this);
            if(decision is null)return;
            exportState=decision.State;
            if(decision.BackToEditing)continue;
            if(decision.Request is not {} request)return;
            try
            {
                // Build the complete batch first; cancellation or invalid settings leave the queue unchanged.
                var jobs=decision.State.JoinSegments
                    ? QuickClipWorkflow.PrepareJoinedJobs(edits.OfType<ClipEditResult>(),decision.State.Preset,decision.State.Options,request.OutputFolder,request.OutputToSource,request.SettingName,_jobs.Select(j=>j.Output))
                    : QuickClipBatch.CreateJobs(request.ClipInputs!,request.OutputFolder,request.OutputToSource,request.SettingName,_jobs.Select(j=>j.Output));
                foreach(var job in jobs)_jobs.Add(job);
                Save();Refresh();
            }
            catch(Exception ex){await Ui.Message(this,"导出参数错误",ex.Message);continue;}
            return;
        }
    }
}
