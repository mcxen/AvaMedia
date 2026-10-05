using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;
public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        Title="选项";Width=780;Height=500;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new(22),Spacing=12};panel.Children.Add(Ui.Text("常规 / 外部工具",18));
        var output=Ui.Input(settings.OutputFolder);var ffmpeg=Ui.Input(settings.FFmpegPath);var probe=Ui.Input(settings.FFprobePath);var ytdlp=Ui.Input(settings.YtDlpPath);var parallel=Ui.Combo(["1","2","3","4","6","8"],settings.ParallelJobs.ToString());
        Add("输出文件夹",output,true);Add("FFmpeg",ffmpeg);Add("FFprobe",probe);Add("yt-dlp (下载)",ytdlp);
        var row=new Grid{ColumnDefinitions=new("135,*")};row.Children.Add(Ui.Text("同时执行任务数"));Grid.SetColumn(parallel,1);row.Children.Add(parallel);panel.Children.Add(row);
        var reducedMotion=new CheckBox{Content="减少界面动效（立即显示状态变化）",IsChecked=settings.ReduceMotion};panel.Children.Add(reducedMotion);
        panel.Children.Add(new TextBlock{Text="工具路径留空时，自动查找 AVAMEDIA_* 环境变量、工程 .tools 和 PATH。\n编码器是否可用取决于 FFmpeg 构建及本机硬件。",TextWrapping=Avalonia.Media.TextWrapping.Wrap,FontSize=12});
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12,HorizontalAlignment=HorizontalAlignment.Right};buttons.Children.Add(Ui.Button("取消",()=>Close(false),110));var ok=new Button{Content="确定",Width=110};ok.Click+=async(_,_)=>
        {
            try
            {
                if(string.IsNullOrWhiteSpace(output.Text))throw new ArgumentException("请选择输出目录。");var directory=Path.GetFullPath(output.Text);
                var tools=new[]{ffmpeg.Text?.Trim()??"",probe.Text?.Trim()??"",ytdlp.Text?.Trim()??""};foreach(var path in tools)if(path.Length>0&&!File.Exists(path))throw new FileNotFoundException("工具路径不存在。",path);
                settings.OutputFolder=directory;settings.FFmpegPath=tools[0];settings.FFprobePath=tools[1];settings.YtDlpPath=tools[2];settings.ParallelJobs=int.Parse((string)parallel.SelectedItem!);settings.ReduceMotion=reducedMotion.IsChecked==true;Close(true);
            }catch(Exception ex){await Ui.Message(this,"参数错误",ex.Message);}
        };buttons.Children.Add(ok);panel.Children.Add(buttons);Content=panel;
        void Add(string label,TextBox box,bool folder=false)
        {
            var g=new Grid{ColumnDefinitions=new("135,*,70")};g.Children.Add(Ui.Text(label));Grid.SetColumn(box,1);g.Children.Add(box);
            var button=new Button{Content="浏览…",Margin=new(8,0,0,0)};button.Click+=async(_,_)=>{if(folder){if(await Ui.Folder(this,"选择目录") is {} path)box.Text=path;}else{var files=await Ui.Pick(this,"选择可执行文件",false);if(files.Length>0)box.Text=files[0];}};Grid.SetColumn(button,2);g.Children.Add(button);panel.Children.Add(g);
        }
    }
}
