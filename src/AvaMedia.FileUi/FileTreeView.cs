using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace AvaMedia.FileUi;

/// <summary>Lazy directory tree shared by native and remote file browsers.</summary>
public sealed class FileTreeView(IFileBrowserSource source) : TreeView, IDisposable
{
    protected override Type StyleKeyOverride => typeof(TreeView);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _subscribed;
    public event Action<FileBrowserItem>? FolderSelected;
    public void SetLocations(IEnumerable<FileBrowserLocation> locations)
    {
        ItemsSource = locations.Select(location => Node(location.Name, location.Folder)).ToArray();
        AutomationProperties.SetName(this, "文件夹树");
        if (_subscribed) return;
        _subscribed = true;
        SelectionChanged += (_, _) => { if (SelectedItem is TreeViewItem { Tag: FileBrowserItem folder }) FolderSelected?.Invoke(folder); };
    }
    private TreeViewItem Node(string name, FileBrowserItem folder)
    {
        var item = new TreeViewItem { Header = name, Tag = folder, ItemsSource = new[] { new TreeViewItem { Header = "正在读取…", IsEnabled = false } } };
        AutomationProperties.SetName(item, name);
        var loaded = false; var loading = false;
        async Task Load()
        {
            if (loaded || loading || _lifetime.IsCancellationRequested) return;
            loading = true;
            try
            {
                var entries = await source.ReadFoldersAsync(folder, _lifetime.Token);
                if (_lifetime.IsCancellationRequested) return;
                item.ItemsSource = entries.Select(entry => Node(entry.Name, entry)).ToArray(); loaded = true;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                var retry = new Button { Content = "读取失败，重试", HorizontalAlignment = HorizontalAlignment.Left };
                ToolTip.SetTip(retry, error.Message); retry.Click += async (_, _) => await Load();
                item.ItemsSource = new[] { new TreeViewItem { Header = retry } };
            }
            finally { loading = false; }
        }
        item.PropertyChanged += async (_, args) => { if (args.Property == TreeViewItem.IsExpandedProperty && item.IsExpanded) await Load(); };
        return item;
    }
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}
