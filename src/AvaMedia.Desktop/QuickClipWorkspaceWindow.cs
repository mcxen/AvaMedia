using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

internal sealed class QuickClipWorkspaceWindow : Window
{
    private sealed class Source(string path, ClipEditResult? edit)
    {
        public string Path { get; } = path;
        public ClipEditResult? Edit { get; set; } = edit;
        public bool Include { get; set; } = true;
    }
    private readonly IMediaEngine _engine;
    private readonly List<Source> _sources;
    private readonly ListBox _files = new();
    private readonly StackPanel _segments = new() { Spacing = 10 };
    private readonly TextBlock _status = Ui.Text("", "caption");
    private readonly Button _export;
    private bool _editing;
    public QuickClipWorkspaceWindow(IMediaEngine engine, IEnumerable<string> paths, IEnumerable<ClipEditResult>? edits)
    {
        _engine = engine; _sources = paths.Select(path => new Source(path, edits?.FirstOrDefault(edit => edit.Path == path))).ToList();
        Title = "快速剪辑"; Width = 920; Height = 640; MinWidth = 740; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 12, Margin = new(20) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(Ui.Button("添加视频…", async () =>
        {
            try { foreach(var path in await Ui.Pick(this, "选择视频")) if(!_sources.Any(source => VideoFolderScanner.PathComparer.Equals(source.Path, path))) _sources.Add(new(path, null)); RefreshFiles(); }
            catch(Exception error) { _status.Text = error.Message; }
        }));
        toolbar.Children.Add(Ui.Button("移除视频", () => { if(_files.SelectedItem is Source source) _sources.Remove(source); RefreshFiles(); })); root.Children.Add(toolbar);
        _files.ItemTemplate = new FuncDataTemplate<Source>((source, _) =>
        {
            var row = new StackPanel { Spacing = 6, Margin = new(0, 5) };
            var include = new CheckBox { Content = System.IO.Path.GetFileName(source!.Path), IsChecked = source.Include }; Localization.SetIsUserText(include, true);
            include.IsCheckedChanged += (_, _) => { source.Include = include.IsChecked == true; Refresh(); }; row.Children.Add(include);
            row.Children.Add(Ui.Text(source.Edit is null ? "待剪辑" : Localization.Format($"{source.Edit.Segments.Count} 个片段"), "caption")); return row;
        });
        var body = new Grid { ColumnDefinitions = new("280,*"), ColumnSpacing = 20 }; body.Children.Add(_files);
        body.Children.Add(new ScrollViewer { Content = _segments, [Grid.ColumnProperty] = 1 }); Grid.SetRow(body, 1); root.Children.Add(body);
        var footer = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 12 }; footer.Children.Add(_status);
        var cancel = Ui.DialogButton("取消", () => Close(null)); Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        _export = Ui.DialogButton("导出选项…", () => ToolExecution.Complete(this,_sources.Where(source => source.Include).Select(source => source.Edit!).ToArray())); _export.Classes.Add("primary");_export.IsDefault=true;
        Grid.SetColumn(_export, 2); footer.Children.Add(_export); Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        _files.SelectionChanged += (_, _) => Refresh(); RefreshFiles();
    }
    private async Task EditAsync(Source source)
    {
        if(_editing) return; _editing = true;
        try
        {
            var editor = new EditorWindow(_engine, source.Path, source.Edit?.Segments.FirstOrDefault() ?? new(), "quick-workflow", source.Edit?.Segments);
            editor.SetWorkflowCompletion("保存剪辑");
            if(await ToolExecution.ShowAsync<ClipEditResult>(this,editor) is {} result) source.Edit = result;
            RefreshFiles();
        }
        catch(Exception error) { _status.Text = error.Message; }
        finally { _editing = false; }
    }
    private void RefreshFiles() { var selected = _files.SelectedItem; _files.ItemsSource = null; _files.ItemsSource = _sources.ToArray(); _files.SelectedItem = _sources.Contains(selected as Source ?? null!) ? selected : _sources.FirstOrDefault(); Refresh(); }
    private void Refresh()
    {
        _segments.Children.Clear();
        if(_files.SelectedItem is Source source)
        {
            var name = Ui.Text(System.IO.Path.GetFileName(source.Path), "heading"); Localization.SetIsUserText(name, true); _segments.Children.Add(name);
            _segments.Children.Add(Ui.Button(source.Edit is null ? "剪辑此视频…" : "继续编辑…", async () => await EditAsync(source)));
            foreach(var segment in source.Edit?.Segments ?? []) _segments.Children.Add(Ui.Text(MediaTime.Format(segment.Start) + " – " + MediaTime.Format(segment.End)));
        }
        var selected = _sources.Where(source => source.Include).ToArray(); var ready = selected.Count(source => source.Edit?.Segments.Count > 0);
        _status.Text = Localization.Format($"已剪辑 {ready} / {selected.Length} 个视频"); _export.IsEnabled = ready > 0 && ready == selected.Length;
    }
}
