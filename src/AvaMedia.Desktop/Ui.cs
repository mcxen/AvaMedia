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
