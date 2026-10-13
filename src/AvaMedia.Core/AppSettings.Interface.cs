namespace AvaMedia.Core;

public sealed partial class AppSettings
{
    // 0 follows the desktop; explicit percentages are applied before X11 starts.
    public int LinuxInterfaceScalePercent { get; set; }
}
