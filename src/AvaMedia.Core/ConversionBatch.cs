namespace AvaMedia.Core;

public static class ConversionBatch
{
    public static bool IsGrouped(Feature feature)=>PdfTools.Supports(feature.Operation) || feature.Operation is Operation.Join or Operation.Mux or Operation.AudioMix or Operation.PdfMerge or Operation.ImagesPdf or Operation.Zip or Operation.Download or Operation.IsoCopy;

    public static IReadOnlyList<Job> CreateJobs(Feature feature,IReadOnlyList<string> files,string outputFolder,ConversionOptions options,IReadOnlyList<ConversionOptions>? inputOptions=null,IEnumerable<string>? reserved=null)
    {
        if(inputOptions is not null && inputOptions.Count!=files.Count)throw new ArgumentException("输入文件与独立参数数量不一致。");
        if(string.IsNullOrWhiteSpace(outputFolder))throw new ArgumentException("请选择输出目录。");
        var grouped=IsGrouped(feature);var drafts=new List<Job>();
        if(grouped)drafts.Add(new(){FeatureId=feature.Id,Inputs=files.ToArray(),Options=options.Clone(),InputOptions=inputOptions?.Select(o=>o.Clone()).ToList()});
        else for(int index=0;index<files.Count;index++)drafts.Add(new(){FeatureId=feature.Id,Inputs=[files[index]],Options=(inputOptions?[index]??options).Clone()});
        if(drafts.Count==0)throw new ArgumentException("请添加文件。");
        foreach(var draft in drafts)
        {
            draft.Output=Path.Combine(Path.GetTempPath(),"AvaMedia-validation-"+Guid.NewGuid()+"."+draft.Options.Format);
            MediaEngine.Validate(draft);
        }
        var used=new HashSet<string>(reserved??[],OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        foreach(var path in files)used.Add(path);
        var directory=Catalog.DirectoryOutput(feature.Operation);
        foreach(var draft in drafts)
        {
            var name=feature.Operation==Operation.Download?"Download-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"):Path.GetFileNameWithoutExtension(draft.Inputs.FirstOrDefault()??"output");
            draft.Output=MediaEngine.UniqueOutput(outputFolder,name,draft.Options.Format,used,directory);used.Add(draft.Output);
        }
        return drafts;
    }
}
