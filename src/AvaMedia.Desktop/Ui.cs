using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AvaMedia.Desktop;
internal static partial class Ui
{
    public static Button Button(string text,Action action)
    {
        var b=new Button{Content=text,Classes={"ac"}};b.Click+=(_,_)=>action();
        var states=text switch
        {
            "开始任务" => new[]{"开始任务","继续任务","重新检测","重新执行"},
            "查看结果" => new[]{"查看结果","校对字幕","调整片段"},
            "暂停任务" => new[]{"暂停任务","继续任务"},
            _ => Array.Empty<string>()
        };
        if(states.Length>0)StableLayout.Reserve(b,states);
        return b;
    }
    public static Button DialogButton(string text,Action action,bool isDefault=false)
    {var button=Button(text,action);button.Classes.Add("dialog-action");button.IsDefault=isDefault||text is "确定" or "保存";button.IsCancel=text is "取消" or "关闭";if(button.IsDefault)button.Classes.Add("primary");return button;}
    public static TextBlock Text(string text,string? role=null) => ComponentText(text,role);
    public static TextBlock Status(string text="", string? role="caption") => ComponentStatus(text,role);
    public static TextBlock FormattedText(FormattableString text,string? role=null) => ComponentFormattedText(text,role);
    public static TextBox Input(string value="",int width=0) => ComponentInput(value,width);
    public static NumericUpDown Number(double value,double minimum,double maximum,double step=1) => ComponentNumber(value,minimum,maximum,step);
    public static Control Parameter(Control control,string label) => ComponentParameter(control,label);
    public static Control Adjust(NumericUpDown input,double? maximum=null)
    {
        input.Classes.Set("ac",true);
        var min=(double)input.Minimum;var max=maximum??Math.Min((double)input.Maximum,1000000);
        max=Math.Max(min+.000001,max);var logarithmic=max-min>1000;
        double Scale(double value)=>logarithmic?Math.Log10(Math.Max(0,value-min)+1):value;
        double Unscale(double value)=>logarithmic?Math.Pow(10,value)+min-1:value;
        var slider=new Slider{Minimum=Scale(min),Maximum=Scale(max),Value=Scale(Math.Clamp((double)(input.Value??input.Minimum),min,max)),MinWidth=64,Classes={"ac"}};
        slider.SmallChange=(slider.Maximum-slider.Minimum)/100;slider.LargeChange=slider.SmallChange*10;
        var syncing=false;
        slider.PropertyChanged+=(_,change)=>
        {
            if(syncing||change.Property!=Slider.ValueProperty)return;
            syncing=true;
            var step=Math.Max(.001,(double)input.Increment);
            input.Value=(decimal)Math.Clamp(Math.Round(Unscale(slider.Value)/step)*step,min,(double)input.Maximum);
            syncing=false;
        };
        input.ValueChanged+=(_,_)=>{if(syncing)return;syncing=true;slider.Value=Scale(Math.Clamp((double)(input.Value??input.Minimum),min,max));syncing=false;};
        input.PropertyChanged+=(_,change)=>
        {
            if(change.Property==Control.IsEnabledProperty)slider.IsEnabled=input.IsEnabled;
            if(change.Property!=NumericUpDown.MinimumProperty&&change.Property!=NumericUpDown.MaximumProperty)return;
            syncing=true;min=(double)input.Minimum;max=Math.Max(min+.000001,Math.Min((double)input.Maximum,maximum??1000000));logarithmic=max-min>1000;
            slider.Minimum=Scale(min);slider.Maximum=Scale(max);slider.Value=Scale(Math.Clamp((double)(input.Value??input.Minimum),min,max));syncing=false;
        };
        slider.IsEnabled=input.IsEnabled;
        var name=Avalonia.Automation.AutomationProperties.GetName(input);
        Avalonia.Automation.AutomationProperties.SetName(slider,string.IsNullOrEmpty(name)?input.Name??"调整数值":name);
        var row=new Grid{ColumnDefinitions=new("*,Auto")};
        row.Bind(Grid.ColumnSpacingProperty,new DynamicResourceExtension("AcRowGap"));row.Children.Add(slider);
        input.Width=double.NaN;
        input.Bind(NumericUpDown.MinWidthProperty,new DynamicResourceExtension(input.Classes.Contains("compact")?"AcNumericCompactMinWidth":"AcNumericMinWidth"));
        input.HorizontalAlignment=HorizontalAlignment.Stretch;Grid.SetColumn(input,1);row.Children.Add(input);return row;
    }
    public static ComboBox Combo(IEnumerable<string> items,string selected) => ComponentCombo(items,selected);
    public static async Task<string[]> Pick(Window owner,string title,bool multiple=true)
    {var files=await ToolFilePicker.OpenFilesAsync(owner, new(){Title=Localization.Text(title),AllowMultiple=multiple});return files.Select(f=>f.TryGetLocalPath()).OfType<string>().ToArray();}
    public static async Task<string?> Folder(Window owner,string title)
    {var folders=await ToolFilePicker.OpenFoldersAsync(owner, new(){Title=Localization.Text(title),AllowMultiple=false});return folders.FirstOrDefault()?.TryGetLocalPath();}
    public static Task<bool> Confirm(Window owner,string title,string message,string action)
        => ComponentConfirm(owner,title,message,action);
    public static Task MessageDialog(Window owner,string title,string message,IReadOnlyList<string> details)
        => ComponentMessageDialog(owner,title,message,details);
    public static Task Message(Window owner,string title,string message)
        => Notify(owner,title,message);
    public static Task MessageFormatted(Window owner,string title,FormattableString message)
        => Notify(owner,title,message);
    private static Task Notify(Window owner,string title,object message)
    {
        Notifications.NotificationCenter.Shared.Publish(owner,new(Guid.NewGuid().ToString("N"),title,message));
        return Task.CompletedTask;
    }
}
