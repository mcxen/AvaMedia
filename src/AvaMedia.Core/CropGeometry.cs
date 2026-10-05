namespace AvaMedia.Core;

public static class CropGeometry
{
    public static void Validate(CropArea area, MediaInfo media, bool evenPixels=true)
    {
        var minimum=evenPixels?2:1;
        if(!media.HasVideo || media.Width<minimum || media.Height<minimum)throw new ArgumentException("文件不包含可裁剪的视频画面。");
        if(area.X<0 || area.Y<0 || area.Width<minimum || area.Height<minimum)throw new ArgumentException($"裁剪坐标不能为负，宽度和高度至少为 {minimum} 像素。");
        if(evenPixels && (area.X | area.Y | area.Width | area.Height)%2!=0)throw new ArgumentException("裁剪 X、Y、宽度和高度须为偶数像素。");
        if((long)area.X+area.Width>media.Width || (long)area.Y+area.Height>media.Height)throw new ArgumentException($"裁剪区域超出 {media.Width} × {media.Height} 画面。");
    }
}
