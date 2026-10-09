namespace AvaMedia.Core;

public static class MediaEditValidation
{
    public static void Validate(MediaInfo media,ConversionOptions options)
    {
        if(media.Duration>0)
        {
            if(options.Start>=media.Duration || options.End>media.Duration+.001)throw new ArgumentException("剪辑区间超出媒体时长。");
        }
        else if(options.Start>0 || options.End>0)throw new ArgumentException("当前媒体没有可剪辑的时间区间。");
        if(options.CropWidth>0)CropGeometry.Validate(new(options.CropX,options.CropY,options.CropWidth,options.CropHeight),media,!MediaEngine.IsImage(options.Format) && options.Format!="gif");
        if(options.DelogoWidth>0 && (!media.HasVideo || (long)options.DelogoX+options.DelogoWidth>media.Width || (long)options.DelogoY+options.DelogoHeight>media.Height))throw new ArgumentException("水印区域超出画面。");
    }
}
