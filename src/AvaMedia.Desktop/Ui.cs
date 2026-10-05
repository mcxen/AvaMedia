using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AvaMedia.Desktop;
internal static class Ui
{
    public static Button Button(string text,Action action,double width=double.NaN)
    {var b=new Button{Content=text,Width=width};b.Click+=(_,_)=>action();return b;}
    public static TextBlock Text(string text,int size=13) => new(){Text=text,FontSize=size,VerticalAlignment=VerticalAlignment.Center};
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
        var close=Button("确定",()=>w.Close(),120);close.HorizontalAlignment=HorizontalAlignment.Right;close.Margin=new(0,14,0,0);Grid.SetRow(close,1);grid.Children.Add(close);w.Content=grid;await w.ShowDialog(owner);
    }
    public static async Task<bool> Confirm(Window owner,string title,string text)
    {
        var w=new Window{Title=title,Width=460,Height=180,WindowStartupLocation=WindowStartupLocation.CenterOwner,CanResize=false};
        var p=new StackPanel{Margin=new(20),Spacing=22};p.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap});
        var row=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Spacing=10};
        row.Children.Add(Button("取消",()=>w.Close(false),100));row.Children.Add(Button("确定",()=>w.Close(true),100));p.Children.Add(row);w.Content=p;return await w.ShowDialog<bool>(owner);
    }
}
