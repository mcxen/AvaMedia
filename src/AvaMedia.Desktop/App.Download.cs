namespace AvaMedia.Desktop;

public sealed partial class App
{
    private static async Task CaptureDownloadAsync(MainWindow owner,string root)
    {
        var download=new DownloadWindow(new(),Path.Combine(root,"output"));
        download.Show(owner);await Task.Delay(1000);
        await Capture(download,Path.Combine(root,"download.png"));download.Close();
    }
}
