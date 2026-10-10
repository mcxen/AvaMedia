using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AvaMedia.Desktop;
internal static class Ui
{
    public static Button Button(string text,Action action)
    {
        var b=new Button{Content=text};b.Click+=(_,_)=>action();
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
    public static Button DialogButton(string text,Action action)
    {var button=Button(text,action);button.Classes.Add("dialog-action");button.IsDefault=text is "确定" or "保存";button.IsCancel=text is "取消" or "关闭";if(button.IsDefault)button.Classes.Add("primary");return button;}
    public static TextBlock Text(string text,string? role=null)
    {var label=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};if(role is not null)label.Classes.Add(role);return label;}
    public static TextBlock Status(string text="", string? role="caption")
    {var label=Text(text,role);StableLayout.SetStatusLines(label,2);return label;}
    public static TextBlock FormattedText(FormattableString text,string? role=null)
    {var label=Text("",role);Localization.SetText(label,text);return label;}
    public static TextBox Input(string value="",int width=0) => new(){Text=value,MinWidth=width,HorizontalAlignment=HorizontalAlignment.Stretch};
    public static NumericUpDown Number(double value,double minimum,double maximum,double step=1) => new()
    {Value=(decimal)value,Minimum=(decimal)minimum,Maximum=(decimal)maximum,Increment=(decimal)step,FormatString=step>=1?"0":"0.##"};
    public static Control Parameter(Control control,string label)
    {
        if(control is not NumericUpDown number)return control;
        Avalonia.Automation.AutomationProperties.SetName(number,label);return Adjust(number);
    }
    public static Control Adjust(NumericUpDown input,double? maximum=null)
    {
        var min=(double)input.Minimum;var max=maximum??Math.Min((double)input.Maximum,1000000);
        max=Math.Max(min+.000001,max);var logarithmic=max-min>1000;
        double Scale(double value)=>logarithmic?Math.Log10(Math.Max(0,value-min)+1):value;
        double Unscale(double value)=>logarithmic?Math.Pow(10,value)+min-1:value;
        var slider=new Slider{Minimum=Scale(min),Maximum=Scale(max),Value=Scale(Math.Clamp((double)(input.Value??input.Minimum),min,max)),MinWidth=64};
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
        var row=new Grid{ColumnDefinitions=new("*,88"),ColumnSpacing=10};row.Children.Add(slider);
        input.Width=88;input.HorizontalAlignment=HorizontalAlignment.Stretch;Grid.SetColumn(input,1);row.Children.Add(input);return row;
    }
    public static ComboBox Combo(IEnumerable<string> items,string selected)
    {var list=items.ToArray();return new(){ItemsSource=list,SelectedItem=list.Contains(selected)?selected:list.FirstOrDefault(),HorizontalAlignment=HorizontalAlignment.Stretch};}
    public static async Task<string[]> Pick(Window owner,string title,bool multiple=true)
    {var files=await owner.StorageProvider.OpenFilePickerAsync(new(){Title=Localization.Text(title),AllowMultiple=multiple});return files.Select(f=>f.TryGetLocalPath()).OfType<string>().ToArray();}
    public static async Task<string?> Folder(Window owner,string title)
    {var folders=await owner.StorageProvider.OpenFolderPickerAsync(new(){Title=Localization.Text(title),AllowMultiple=false});return folders.FirstOrDefault()?.TryGetLocalPath();}
    // Modal yes/no confirmation for destructive in-window actions; the action button keeps the dialog's single primary role.
    public static async Task<bool> Confirm(Window owner,string title,string message,string action)
    {
        var dialog=new Window{Title=title,Width=480,Height=220,MinWidth=400,MinHeight=200,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var body=new Grid{RowDefinitions=new("*,Auto"),RowSpacing=16,Margin=new(20)};
        body.Children.Add(new ScrollViewer{Content=Text(message)});
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Spacing=8};
        buttons.Children.Add(DialogButton("取消",()=>dialog.Close(false)));
        var confirm=DialogButton(action,()=>dialog.Close(true));confirm.Classes.Add("primary");buttons.Children.Add(confirm);
        Grid.SetRow(buttons,1);body.Children.Add(buttons);dialog.Content=body;
        return await dialog.ShowDialog<bool>(owner);
    }
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
