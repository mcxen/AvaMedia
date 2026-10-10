using AvaMedia.Core;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private void InitializeModelActivity() => ModelActivity.Configure(new ModelStore().Root);
}
