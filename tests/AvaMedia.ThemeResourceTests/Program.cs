using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;
using AvaMedia.Desktop.Controls;

var output=Path.GetFullPath(args.FirstOrDefault()??"artifacts/theme-resources-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(output);
var checks=new List<string>();
void Check(bool result,string message){if(!result)throw new Exception(message);checks.Add(message);Console.WriteLine("PASS "+message);}
void Pump(){Dispatcher.UIThread.RunJobs();AvaloniaHeadlessPlatform.ForceRenderTimerTick();Dispatcher.UIThread.RunJobs();}
void Complete(Task task){var until=DateTime.UtcNow.AddSeconds(30);while(!task.IsCompleted){Pump();if(DateTime.UtcNow>until)throw new TimeoutException();Thread.Sleep(5);}task.GetAwaiter().GetResult();Pump();}
string Color(IBrush? brush)=>brush is ISolidColorBrush solid?solid.Color.ToString():"";
IBrush BrushFor(Control control,string key){if(!control.TryFindResource(key,control.ActualThemeVariant,out var value)||value is not IBrush brush)throw new Exception("Missing "+key);return brush;}
double Metric(Control control,string key){if(!control.TryFindResource(key,control.ActualThemeVariant,out var value)||value is not double size)throw new Exception("Missing "+key);return size;}
void Capture(Window window,string name){Pump();using var frame=window.CaptureRenderedFrame();frame!.Save(Path.Combine(output,name+".png"));}
void Inside(Window window,Control control,string name){var origin=control.TranslatePoint(default,window)!.Value;Check(control.Bounds.Width>0&&control.Bounds.Height>0&&origin.X>=0&&origin.Y>=0&&origin.X+control.Bounds.Width<=window.ClientSize.Width+.5&&origin.Y+control.Bounds.Height<=window.ClientSize.Height+.5,name+" stays inside the window");}

AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions{UseHeadlessDrawing=false}).SetupWithoutStarting();
Motion.SetReducedMotion(true);
var engine=new MediaEngine(new());
var source=Path.Combine(output,"resource sample.mp4");
Complete(Task.Run(async()=>{var result=await ProcessRunner.Run(engine.FFmpeg,["-v","error","-n","-f","lavfi","-i","testsrc2=size=128x72:rate=30","-t","1.4","-c:v","mpeg4",source]);if(result.ExitCode!=0)throw new Exception(result.Error);}));
var editor=new EditorWindow(engine,source,new(){Start=.033333333333,End=1.312684});editor.Show();Complete(editor.Ready);
var settings=new SettingsWindow(new(){OutputFolder=Path.Combine(output,"media")});settings.Show();Pump();
var start=editor.FindControl<TextBox>("StartTime")!;
var end=editor.FindControl<TextBox>("EndTime")!;
var range=editor.FindControl<RangeBar>("TrimBar")!;
var crop=editor.FindControl<CropOverlay>("CropLayer")!;
var play=editor.FindControl<Button>("PlayButton")!;
var cancel=editor.FindControl<Button>("EditorCancelButton")!;
var confirm=editor.FindControl<Button>("ConfirmButton")!;
var values=(start.Text,end.Text,range.Start,range.End);
foreach(var skin in new[]{"Light","Dark","MacOS9","Light"})
{
    Skin.Apply(skin);Pump();
    Check(Color(range.SelectionBrush)==Color(BrushFor(editor,"UiAccent")),skin+" updates the painted timeline selection");
    Check(Color(range.TrackBrush)==Color(BrushFor(editor,"UiSurfaceRaised"))&&Color(range.HandleBrush)==Color(BrushFor(editor,"UiSurface")),skin+" updates timeline surface and handles");
    Check(Color(crop.BorderBrush)==Color(BrushFor(editor,"UiCropBorder")),skin+" updates crop outlines");
    Check(Color(editor.FindControl<ActionIcon>("PlayIcon")!.Foreground)==Color(BrushFor(editor,"UiAccent")),skin+" updates transport icon color");
    Check(start.Height==Metric(editor,"UiFieldHeight")&&start.Width==end.Width&&start.FontSize==end.FontSize&&start.FontSize==Metric(editor,"UiTimeFontSize"),skin+" keeps matching time input metrics");
    Check(cancel.Width==confirm.Width&&cancel.Height==confirm.Height&&cancel.Width==Metric(editor,"UiDialogActionWidth"),skin+" keeps matching dialog action metrics");
    Check(play.Width==play.Height&&play.Width==Metric(editor,"UiTransportSize"),skin+" keeps transport controls square");
    Check(values==(start.Text,end.Text,range.Start,range.End),skin+" preserves edited media values and exact cut boundaries");
    Check(settings.FontFamily.ToString()==editor.FontFamily.ToString(),skin+" uses one inherited UI font across windows");
    var settingsButtons=new[]{"DefaultButton","CancelButton","ApplyButton","OkButton"}.Select(n=>settings.FindControl<Button>(n)!).ToArray();
    Check(settingsButtons.All(b=>b.Width==cancel.Width&&b.Height==cancel.Height),skin+" shares footer styles with Settings");
    Check(settings.FindControl<NumericUpDown>("ThreadsInput")!.Bounds.Height==Metric(settings,"UiFieldHeight"),skin+" keeps numeric fields at the shared input height");
    var numericText=settings.FindControl<NumericUpDown>("ThreadsInput")!.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Name=="PART_TextBox");
    Check(numericText.Bounds.Height-numericText.Padding.Top-numericText.Padding.Bottom>=numericText.FontSize*1.25,skin+" gives numeric text enough space to avoid clipping");
    settings.FindControl<NumericUpDown>("ThreadsInput")!.IsEnabled=false;Pump();
    Check(numericText.Bounds.Height-numericText.Padding.Top-numericText.Padding.Bottom>=numericText.FontSize*1.25,skin+" retains numeric text space when disabled");
    settings.FindControl<NumericUpDown>("ThreadsInput")!.IsEnabled=true;Pump();
    if(skin=="MacOS9")
    {
        var caption=editor.GetVisualDescendants().OfType<Button>().Single(b=>b.Name=="PlatinumClose");
        var titleBar=editor.GetVisualDescendants().OfType<Grid>().Single(g=>g.Name=="PlatinumTitleBar");
        Check(caption.Bounds.Height==19&&caption.Bounds.Height+caption.Margin.Top+caption.Margin.Bottom<=titleBar.Bounds.Height,"Platinum caption buttons retain their chrome role inside Editor");
    }
    settings.Width=780;settings.Height=640;editor.Width=1000;editor.Height=730;Pump();
    Inside(editor,start,skin+" start time");Inside(editor,end,skin+" end time");Inside(editor,confirm,skin+" editor confirmation");Inside(settings,settingsButtons[^1],skin+" settings confirmation");
    Capture(editor,"editor-"+skin.ToLowerInvariant());Capture(settings,"settings-"+skin.ToLowerInvariant());
    editor.Width=1400;editor.Height=980;Pump();
}
editor.Resources["UiTimeFieldWidth"]=188d;editor.Resources["UiFieldHeight"]=36d;editor.Resources["UiTransportSize"]=44d;editor.Resources["UiTimeFontSize"]=16d;
var replacement=new SolidColorBrush(Avalonia.Media.Color.Parse("#8A2BE2"));editor.Resources["UiAccent"]=replacement;Pump();
Check(start.Width==188&&end.Width==188&&start.Height==36&&end.Height==36&&start.FontSize==16,"Existing time fields respond to resource metric overrides");
Check(play.Bounds.Width==44&&play.Bounds.Height==44,"Transport layout responds to a resource size override");
Check(Color(range.SelectionBrush)==Color(replacement)&&Color(editor.FindControl<ActionIcon>("PlayIcon")!.Foreground)==Color(replacement),"Already rendered vector controls respond to live resource overrides");
Capture(editor,"editor-resource-overrides");
editor.Close();settings.Close();
File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new{checks=checks.Count,results=checks},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Verified {checks.Count} theme resource checks. {output}");
