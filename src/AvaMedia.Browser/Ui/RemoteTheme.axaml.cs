using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace AvaMedia.Browser;

public partial class RemoteTheme : Styles
{
    public RemoteTheme() => AvaloniaXamlLoader.Load(this);
}
