namespace AvaMedia.Core;

/// <summary>Renders the selected frame with the same filters used by the export command.</summary>
public static class MediaVisualPreview
{
    public static async Task<byte[]> RenderAsync(IMediaEngine engine,string source,MediaInfo info,ConversionOptions options,double seconds,CancellationToken token)
    {
        var draft=options.Clone();
        var width=draft.CropWidth>0?draft.CropWidth:info.Width;var height=draft.CropHeight>0?draft.CropHeight:info.Height;
        var ratio=(double)width/Math.Max(1,height);
        if(draft.Width>0&&draft.Height>0){width=draft.Width;height=draft.Height;}
        else if(draft.Width>0){width=draft.Width;height=Math.Max(1,(int)Math.Round(width/ratio));}
        else if(draft.Height>0){height=draft.Height;width=Math.Max(1,(int)Math.Round(height*ratio));}
        var scale=Math.Min(1,1200d/Math.Max(width,height));
        draft.Width=Math.Max(1,(int)Math.Round(width*scale));draft.Height=Math.Max(1,(int)Math.Round(height*scale));
        draft.Format=MediaEngine.IsImage(options.Format)&&options.Format is "jpg" or "jpeg" or "webp"?options.Format:"png";
        draft.Start=info.Duration>0?Math.Clamp(seconds,0,Math.Max(0,info.Duration-.001)):0;draft.End=0;
        draft.CopyStreams=false;draft.VideoCodec=draft.AudioCodec="自动";draft.Mute=true;
        draft.PreserveSourceAttributes=false;draft.LosslessRotation=null;draft.VideoCompression=null;draft.VideoSlimming=null;
        draft.Threads=1;draft.Speed=1;draft.KeepAllAudioStreams=false;
        var scratch=Path.Combine(Path.GetTempPath(),"AvaMedia-visual-preview-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
        try
        {
            var output=Path.Combine(scratch,"frame."+draft.Format);
            var job=new Job{FeatureId="image-png",Inputs=[source],Options=draft,Output=output,Duration=info.Duration};
            var result=await ProcessRunner.Run(engine.FFmpeg,MediaEngine.BuildArguments(job,[info]),token).ConfigureAwait(false);
            if(result.ExitCode!=0)throw new InvalidDataException(result.Error);
            return await File.ReadAllBytesAsync(output,token).ConfigureAwait(false);
        }
        finally{try{Directory.Delete(scratch,true);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
}
