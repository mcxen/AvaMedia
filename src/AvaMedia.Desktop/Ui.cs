using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AvaMedia.Desktop;
internal static class Ui
{
    public static Button Button(string text,Action action)
    {var b=new Button{Content=text};b.Click+=(_,_)=>action();return b;}
    public static Button DialogButton(string text,Action action)
    {var button=Button(text,action);button.Classes.Add("dialog-action");button.IsDefault=text is "确定" or "保存";button.IsCancel=text is "取消" or "关闭";if(button.IsDefault)button.Classes.Add("primary");return button;}
    public static TextBlock Text(string text,string? role=null)
    {var label=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};if(role is not null)label.Classes.Add(role);return label;}
    public static TextBlock FormattedText(FormattableString text,string? role=null)
    {var label=Text("",role);Localization.SetText(label,text);return label;}
    public static TextBox Input(string value="",int width=0) => new(){Text=value,MinWidth=width,HorizontalAlignment=HorizontalAlignment.Stretch};
    public static ComboBox Combo(IEnumerable<string> items,string selected)
    {var list=items.ToArray();return new(){ItemsSource=list,SelectedItem=list.Contains(selected)?selected:list.FirstOrDefault(),HorizontalAlignment=HorizontalAlignment.Stretch};}
    public static async Task<string[]> Pick(Window owner,string title,bool multiple=true)
    {var files=await owner.StorageProvider.OpenFilePickerAsync(new(){Title=Localization.Text(title),AllowMultiple=multiple});return files.Select(f=>f.TryGetLocalPath()).OfType<string>().ToArray();}
    public static async Task<string?> Folder(Window owner,string title)
    {var folders=await owner.StorageProvider.OpenFolderPickerAsync(new(){Title=Localization.Text(title),AllowMultiple=false});return folders.FirstOrDefault()?.TryGetLocalPath();}
    public static async Task Message(Window owner,string title,string message)
        => await ShowMessage(owner,title,box=>box.Text=message);
    public static async Task MessageFormatted(Window owner,string title,FormattableString message)
        => await ShowMessage(owner,title,box=>Localization.SetText(box,message));
    private static async Task ShowMessage(Window owner,string title,Action<TextBox> setMessage)
    {
        var w=new Window{Title=title,Width=650,Height=420,MinWidth=450,MinHeight=250,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var grid=new Grid{RowDefinitions=new("*,Auto"),Margin=new(18)};
        var body=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Stretch};setMessage(body);grid.Children.Add(body);
        var close=DialogButton("确定",()=>w.Close());close.HorizontalAlignment=HorizontalAlignment.Right;close.Margin=new(0,14,0,0);Grid.SetRow(close,1);grid.Children.Add(close);w.Content=grid;await w.ShowDialog(owner);
    }
}
