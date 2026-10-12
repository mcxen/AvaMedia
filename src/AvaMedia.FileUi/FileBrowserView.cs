using System.IO.Enumeration;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AvaMedia.FileUi;

/// <summary>Virtualized media picker; directory enumeration and visible media work have separate lifetimes.</summary>
public sealed partial class FileBrowserView : UserControl, IDisposable
{
    private readonly IFileBrowserSource _source;
    private readonly FileBrowserOptions _options;
    private readonly Func<string, string> _text;
    private readonly FileTreeView _places;
    private readonly ListBox _entries = new();
    private readonly PathInput _address = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _selectionCount = new();
    private readonly ComboBox _types = new() { MinWidth = 140, MaxWidth = 260 };
    private readonly CheckBox _hidden = new();
    private Button _back = null!, _up = null!, _select = null!;
    private readonly Stack<FileBrowserItem> _history = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _navigation;
    private FileBrowserDirectory? _directory;
    private bool _disposed, _loading;
    public event Action<IReadOnlyList<FileBrowserItem>>? Selected;
    public event Action? Cancelled;
    public Func<Task<IReadOnlyList<FileBrowserItem>>>? PickExternalAsync { get; set; }
    public FileBrowserItem? CurrentFolder => _recent ? null : _directory?.Folder;

    public FileBrowserView(IFileBrowserSource source, FileBrowserOptions options, Func<string, string>? text = null, IFileBrowserGlyphs? glyphs = null)
    {
        _source = source; _options = options; _places = new(source); _text = text ?? (value => value);
        FileBrowserTheme.Install(this);
        _entries.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        ScrollViewer.SetHorizontalScrollBarVisibility(_entries, ScrollBarVisibility.Disabled);
        _entries.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(ListBoxItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        var types = options.FileTypes is { Count: > 0 } ? options.FileTypes : [FilePickerFileTypes.All];
        _types.ItemsSource = types; _types.SelectedItem = options.SelectedFileType ?? types[0];
        _types.ItemTemplate = new FuncDataTemplate<FilePickerFileType>((type, _) => Label(_text(type?.Name ?? "")));
        _types.SelectionChanged += (_, _) => FilterAsync();
        _hidden.Content = _text("显示隐藏文件"); _hidden.IsCheckedChanged += (_, _) => FilterAsync();
        if (options.Compact) BuildCompact(glyphs); else BuildMediaPicker(glyphs);
        _address.KeyDown += async (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; await OpenAddressAsync(); } };
        KeyDown += async (_, args) =>
        {
            if (args.Key == Key.Escape) { args.Handled = true; Cancelled?.Invoke(); }
            else if (args.Key == Key.L && (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            { args.Handled = true; _address.IsVisible = true; _address.Focus(); _address.SelectAll(); }
            else if (args.Key == Key.Up && args.KeyModifiers == KeyModifiers.Alt && !_recent && _directory?.Parent is { } parent)
            { args.Handled = true; await NavigateAsync(parent); }
        };
        UpdateSelection();
    }

    public async Task InitializeAsync()
    {
        try
        {
            // Show the requested directory before history or sidebar persistence is loaded.
            var locationsTask = _source.GetLocationsAsync(_lifetime.Token);
            if (_options.StartFolder is { } start) await NavigateAsync(start, false);
            var locations = await locationsTask;
            if (_disposed) return;
            _locations = locations; if (_options.Compact) SetCompactLocations(locations);
            if (!_options.Compact)
            {
                _gridMode = await _source.GetGridModeAsync(_lifetime.Token); UpdateModeButtons();
                await RefreshHistoryAsync();
                if (_options.StartFolder is null && !_options.Folders && _recentHistory.Files.Count > 0) await ShowRecentAsync();
                else if (_directory is null && locations.FirstOrDefault()?.Folder is { } first) await NavigateAsync(first, false);
                else { UpdateModeButtons(); await FilterAsyncCore(); }
            }
            else if (_directory is null && locations.FirstOrDefault()?.Folder is { } folder) await NavigateAsync(folder, false);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error) { ShowError(error); }
    }

    public async Task NavigateAsync(FileBrowserItem folder, bool remember = true)
    {
        var (work, token) = BeginNavigation();
        try
        {
            var directory = await _source.ReadAsync(folder, token);
            if (_disposed || token.IsCancellationRequested) return;
            if (remember && !_recent && _directory is { } previous && previous.Folder.Address != folder.Address) _history.Push(previous.Folder);
            _recent = false; _directory = directory; _address.Text = _source.DisplayAddress(folder);
            _loading = false; await FilterAsyncCore();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!token.IsCancellationRequested) { RestoreAddress(); ShowError(error); } }
        finally { EndNavigation(work); }
    }

    private (CancellationTokenSource Work, CancellationToken Token) BeginNavigation()
    {
        _filterCancellation?.Cancel(); _filterVersion++;
        _navigation?.Cancel(); _navigation?.Dispose();
        var work = _navigation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _mediaWork?.Update([]); _chosen.Clear(); _focusEntry = null; _anchor = 0;
        _filtering = false; _loading = true; _entries.IsEnabled = false; _entries.ItemsSource = null;
        _status.Text = _text("正在读取…"); UpdateSelection(); UpdateInspector(null);
        return (work, work.Token);
    }
    private void EndNavigation(CancellationTokenSource work)
    {
        if (!_disposed && ReferenceEquals(_navigation, work)) { _loading = false; _entries.IsEnabled = true; UpdateSelection(); }
    }

    private async Task OpenAddressAsync()
    {
        try
        {
            var item = await _source.ResolveAsync(_address.Text?.Trim() ?? "", _lifetime.Token);
            if (_disposed) return;
            if (item is { IsFolder: true } folder) await NavigateAsync(folder);
            else if (!_options.Folders && item is { IsFolder: false } file && Matches(file.Name)) Selected?.Invoke([file]);
            else { RestoreAddress(); _status.Text = _text("找不到可选择的文件或文件夹。"); }
        }
        catch (Exception error) { RestoreAddress(); ShowError(error); }
        finally { if (!_options.Compact) _address.IsVisible = false; }
    }
    private void RestoreAddress() => _address.Text = _directory is { } directory ? _source.DisplayAddress(directory.Folder) : "";

    private async Task OpenSelectionAsync()
    {
        if (_loading) return;
        var item = _options.Compact ? (_entries.SelectedItem as FileBrowserEntry)?.Item : _focusEntry?.Item;
        if (item is { IsFolder: true } folder) await NavigateAsync(folder);
        else Confirm();
    }

    private int _filterVersion;
    private bool _filtering;
    private CancellationTokenSource? _filterCancellation;
    private async void FilterAsync()
    {
        try { await FilterAsyncCore(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error); }
    }
    private async Task FilterAsyncCore()
    {
        if (_disposed || _directory is null || _loading) return;
        _mediaWork?.Update([]); var version = ++_filterVersion;
        _filterCancellation?.Cancel(); _filterCancellation?.Dispose();
        _filterCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _filterCancellation.Token;
        _filtering = true;
        try
        {
            var directory = _directory; var showHidden = _hidden.IsChecked == true; var kind = _kind;
            var patterns = (_types.SelectedItem as FilePickerFileType)?.Patterns?.ToArray();
            var recent = _recent;
            var result = await Task.Run(() =>
            {
                var counts = new Dictionary<FileBrowserKind, int>();
                foreach (var entry in directory.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.IsFolder) continue;
                    var type = FileBrowserKinds.FromName(entry.Name);
                    counts[type] = counts.GetValueOrDefault(type) + 1;
                    counts[FileBrowserKind.All] = counts.GetValueOrDefault(FileBrowserKind.All) + 1;
                }
                var entries = directory.Entries.Where(entry => { token.ThrowIfCancellationRequested(); return (showHidden || !entry.Hidden)
                && (entry.IsFolder || !_options.Folders && Matches(entry.Name, patterns) && (kind == FileBrowserKind.All || FileBrowserKinds.FromName(entry.Name) == kind)); })
                .OrderByDescending(entry => entry.IsFolder).ThenBy(entry => recent ? "" : entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                var indexes = new Dictionary<FileBrowserEntry, int>(entries.Length);
                for (var index = 0; index < entries.Length; index++) { token.ThrowIfCancellationRequested(); indexes[entries[index]] = index; }
                return (entries, counts, indexes);
            }, token);
            if (_disposed || version != _filterVersion || !ReferenceEquals(directory, _directory)) return;
            var entries = result.entries; _kindCounts = result.counts;
            _visibleEntries = entries; _entryIndexes = result.indexes;
            _chosen.IntersectWith(entries);
            if (_focusEntry is not null && !_entryIndexes.ContainsKey(_focusEntry)) { _focusEntry = null; UpdateInspector(null); }
            _anchor = Math.Clamp(_anchor, 0, Math.Max(0, entries.Length - 1));
            if (_options.Compact) { _entries.ItemsSource = entries; _status.Text = ""; CompactDirectoryChanged(); }
            else
            {
                RebuildRows(); UpdateTabs(); RenderSidebar(); RenderBreadcrumbs(); RenderRecentFolders();
                var hidden = directory.Entries.Count - entries.Length;
                _status.Text = entries.Length == 0 ? _text("没有可显示的文件。") : $"{_text("显示")} {entries.Length} {_text("项")}";
                if (hidden > 0) _status.Text += $" · {_text("已隐藏")} {hidden} {_text("个文件")}";
            }
            UpdateSelection();
        }
        finally { if (version == _filterVersion) { _filtering = false; QueueVisibleMedia(); } }
    }
    private bool Matches(string name) => Matches(name, (_types.SelectedItem as FilePickerFileType)?.Patterns);
    private static bool Matches(string name, IReadOnlyList<string>? patterns) => patterns is not { Count: > 0 }
        || patterns.Any(pattern => pattern is "*" or "*.*" || FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));

    private FileBrowserItem[] Selection() => _options.Compact
        ? _entries.SelectedItems?.OfType<FileBrowserEntry>().Where(CanSelect).Select(entry => entry.Item).ToArray() ?? []
        : _chosen.Where(CanSelect).Select(entry => entry.Item).ToArray();
    private bool CanSelect(FileBrowserEntry entry) => _options.AllowFolders || entry.IsFolder == _options.Folders;
    private void UpdateSelection()
    {
        _back.IsEnabled = !_loading && _history.Count > 0;
        _up.IsEnabled = !_loading && !_recent && _directory?.Parent is not null;
        var selected = Selection(); var count = selected.Length;
        _select.IsEnabled = !_loading && (count > 0 || _options.Folders && CurrentFolder is not null);
        _selectionCount.Text = $"{_text("已选择")} {count} {_text(_options.Folders ? "个文件夹" : "个文件")}";
        if (!_options.Compact)
        {
            var bytes = _chosen.Where(CanSelect).Sum(entry => (double)(entry.Size ?? 0));
            if (bytes > 0) _selectionCount.Text += " · " + Size((ulong)bytes);
            _select.Content = _options.Embedded ? _text("添加所选项") : _options.Folders ? _text("选择文件夹") : $"{_text("打开")} {count} {_text("个文件")}";
            foreach (var cell in _cells) cell.UpdateSelection(_chosen.Contains(cell.Entry), Equals(cell.Entry, _focusEntry));
        }
        else UpdateCompactSelection(count);
    }
    private void Confirm()
    {
        if (_disposed || !_select.IsEnabled) return;
        var selected = Selection();
        if (selected.Length == 0 && _options.Folders && CurrentFolder is { } folder) selected = [folder];
        if (selected.Length > 0) Selected?.Invoke(_options.Multiple ? selected : selected.Take(1).ToArray());
    }
    private Button Button(string label, Func<Task> action)
    {
        var button = new Button { Content = _text(label) };
        button.Click += async (_, _) => { try { await action(); } catch (OperationCanceledException) { } catch (Exception error) { ShowError(error); } };
        return button;
    }
    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };
    private static string Size(ulong? size) => size is not { } bytes ? "—" : bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KB" : bytes < 1024 * 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" : $"{bytes / (1024d * 1024 * 1024):0.#} GB";
    private void ShowError(Exception error) { if (!_disposed) _status.Text = error.Message; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _filterCancellation?.Cancel(); _filterCancellation?.Dispose(); _navigation?.Cancel(); _navigation?.Dispose(); _mediaWork?.Dispose(); _places.Dispose();
        foreach (var cell in _cells.ToArray()) cell.ReleaseImage();
        _inspectorCell?.ReleaseImage(); _lifetime.Dispose();
    }
}
