using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace AvaMedia.FileUi;

/// <summary>Shared import actions. Hosts retain their own validation and media editing state.</summary>
public sealed class FileImportActions : WrapPanel
{
    private readonly Button _files = new(), _folders = new();
    private bool _busy;
    public Func<Task>? AddFilesAsync { get; set; }
    public Func<Task>? AddFoldersAsync { get; set; }
    public Func<Exception, Task>? ReportErrorAsync { get; set; }
    public Func<bool>? CanImport { get; set; }
    public Button FilesButton => _files;
    public Button FoldersButton => _folders;
    public string FilesText { get => _files.Content as string ?? ""; set => _files.Content = value; }
    public string FoldersText { get => _folders.Content as string ?? ""; set => _folders.Content = value; }
    public bool AllowFolders { get => _folders.IsVisible; set => _folders.IsVisible = value; }

    public FileImportActions()
    {
        Orientation = Orientation.Horizontal;
        FilesText = "添加文件…"; FoldersText = "添加文件夹…";
        foreach (var button in new[] { _files, _folders })
        {
            button.Margin = new Thickness(0, 0, 8, 4); button.Classes.Add("field-action"); Children.Add(button);
        }
        AllowFolders = false;
        _files.Click += async (_, _) => await RunAsync(AddFilesAsync);
        _folders.Click += async (_, _) => await RunAsync(AddFoldersAsync);
    }

    public async Task RunAsync(Func<Task>? action)
    {
        if (action is null || _busy || !IsEffectivelyEnabled || CanImport?.Invoke() == false) return;
        _busy = true;
        try { await action(); }
        catch (Exception error)
        {
            if (ReportErrorAsync is { } report) await report(error);
            else throw;
        }
        finally { _busy = false; }
    }
}

/// <summary>Composes the shared import actions with a tool's file list and optional preview.</summary>
public sealed class FileImportPanel : UserControl
{
    private readonly Grid _layout = new() { RowDefinitions = new("Auto,*"), RowSpacing = 4 };
    private readonly ContentControl _files = new();
    public FileImportActions Actions { get; } = new();
    public Control? FileList { get => _files.Content as Control; set => _files.Content = value; }
    public Func<IReadOnlyList<IStorageItem>, Task>? ImportDroppedAsync { get; set; }

    public FileImportPanel()
    {
        _layout.Children.Add(Actions); Grid.SetRow(_files, 1); _layout.Children.Add(_files); Content = _layout;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, args) =>
        {
            if (ImportDroppedAsync is null) return;
            args.DragEffects = IsEffectivelyEnabled && Actions.CanImport?.Invoke() != false && args.DataTransfer.TryGetFiles() is not null
                ? DragDropEffects.Copy : DragDropEffects.None;
            args.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, async (_, args) =>
        {
            if (ImportDroppedAsync is not { } import) return;
            args.Handled = true;
            var items = args.DataTransfer.TryGetFiles()?.ToArray() ?? [];
            await Actions.RunAsync(() => import(items));
        }, RoutingStrategies.Bubble);
    }
}
