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
    {var button=Button(text,action);button.Classes.Add("dialog-action");return button;}
    public static TextBlock Text(string text,string? role=null)
    {var label=new TextBlock{Text=text,VerticalAlignment=VerticalAlignment.Center};if(role is not null)label.Classes.Add(role);return label;}
    public static TextBox Input(string value="",int width=0) => new(){Text=value,MinWidth=width,HorizontalAlignment=HorizontalAlignment.Stretch};
    public static ComboBox Combo(IEnumerable<string> items,string selected)
    {var list=items.ToArray();return new(){ItemsSource=list,SelectedItem=list.Contains(selected)?selected:list.FirstOrDefault(),HorizontalAlignment=HorizontalAlignment.Stretch};}
    public static async Task<string[]> Pick(Window owner,string title,bool multiple=true)
    {var files=await owner.StorageProvider.OpenFilePickerAsync(new(){Title=title,AllowMultiple=multiple});return files.Select(f=>f.TryGetLocalPath()).OfType<string>().ToArray();}
    public static async Task<string?> Folder(Window owner,string title)
    {var folders=await owner.StorageProvider.OpenFolderPickerAsync(new(){Title=title,AllowMultiple=false});return folders.FirstOrDefault()?.TryGetLocalPath();}
    public static async Task Message(Window owner,string title,string message)
    {
        var w=new Window{Title=title,Width=650,Height=420,MinWidth=450,MinHeight=250,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var grid=new Grid{RowDefinitions=new("*,Auto"),Margin=new(18)};
        grid.Children.Add(new TextBox{Text=message,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Stretch});
        var close=DialogButton("确定",()=>w.Close());close.HorizontalAlignment=HorizontalAlignment.Right;close.Margin=new(0,14,0,0);Grid.SetRow(close,1);grid.Children.Add(close);w.Content=grid;await w.ShowDialog(owner);
    }
}
