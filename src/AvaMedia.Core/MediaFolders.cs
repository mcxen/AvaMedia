namespace AvaMedia.Core;

public static class MediaFolders
{
    public static string DefaultOutput
    {
        get
        {
            var videos=Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if(string.IsNullOrWhiteSpace(videos))videos=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),OperatingSystem.IsMacOS()?"Movies":"Videos");
            return Path.Combine(videos,"AvaMedia");
        }
    }
}
