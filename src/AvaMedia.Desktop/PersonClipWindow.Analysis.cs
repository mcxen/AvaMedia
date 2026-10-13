using static AvaMedia.Desktop.Classification.ClassifyUi;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public sealed partial class PersonClipWindow
{
    private StackPanel _advancedParameters = null!;
    private Button _analyze = null!, _stop = null!, _review = null!;
    private readonly ListBox _retained = new() { MaxHeight=260, BorderThickness=new(0) };
    private readonly TextBlock _resultSummary = Ui.Text("尚未分析", "caption");
    private readonly AvaMedia.Controls.EmptyState _resultEmpty = new() { Text = Localization.Text("等待检测结果") };
    private readonly AiActivityView _activity = new() { Compact = true };
    private bool _busy;
    private Entry? _retainedEntry;

    private PersonClipOptions ReadDetection() => new(Value(_fps), Value(_threshold), Value(_padding), Value(_gap), Value(_minimum),
        _uncertain.IsChecked == true, _embedding.IsChecked == true, _reuseFrames.IsChecked == true,
        SelectedDetectors, (PersonDetectionMode)_detectionMode.SelectedIndex, SkipDarkFrames: _dark.IsChecked == true,
        SkipBlankFrames: _blank.IsChecked == true, DarkLumaThreshold: Value(_darkThreshold));

    private async Task OpenAdvancedAsync()
    {
        if (_busy) return;
        var window = new Window { Title = "人物剪辑 · 高级设置", Width = 520, Height = 620, MinWidth = 460, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var content = new ScrollViewer { Content = _advancedParameters };
        var root = new Grid { RowDefinitions = new("*,Auto"), [!Avalonia.Controls.Control.MarginProperty] = Resource("CtWindowMargin"), [!Grid.RowSpacingProperty] = Resource("AcRowGap") }; root.Children.Add(content);
        var close = Ui.DialogButton("关闭", window.Close); close.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(close, 1); root.Children.Add(close); ApplyToolStyles(window);
        window.Content = root;
        window.Closed += (_, _) =>
        {
            content.Content = null;
            foreach (var input in new[] { _fps, _threshold, _padding, _gap, _minimum, _darkThreshold }) ToolInputs.CommitNumber(input);
        };
        await window.ShowDialog(this);
    }

    private void RefreshResults()
    {
        var entry = Selected;
        _review.IsEnabled = !_busy && !SelectedTaskActive && entry?.Result?.Segments.Count > 0;
        _rangePanel.IsEnabled = !_busy && !SelectedTaskActive && entry is not null;
        if (entry?.Result is not { } result)
        {
            _resultEmpty.IsVisible = true;
            _resultEmpty.IsError = entry?.Error.Length > 0;
            _resultEmpty.Text = Localization.Text(_resultEmpty.IsError ? "无法完成检测" : "等待检测结果");
            _resultEmpty.Detail = entry?.Error ?? "";
            _retained.ItemsSource = null; _retainedEntry = null;
            _resultSummary.Text = entry?.Error.Length > 0 ? entry.Error : Localization.Text(entry?.Status ?? "尚未分析");
            return;
        }
        _resultEmpty.IsVisible = result.Segments.Count == 0;
        _resultEmpty.IsError = false; _resultEmpty.Detail = "";
        _resultEmpty.Text = Localization.Text("没有可保留片段");
        _resultSummary.Text = (entry.Observed?.Result.IsPartial == true ? Localization.Text("部分结果") + " · " : "") + Localization.Format($"保留 {result.Segments.Count} 个片段 · {MediaTime.Format(result.Segments.Sum(segment => segment.End - segment.Start))}");
        if (ReferenceEquals(_retainedEntry, entry) && ReferenceEquals(_retained.ItemsSource, result.Segments)) return;
        _retainedEntry = entry;
        _retained.ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<ConversionOptions>((segment,_)=>
        {
            // Avalonia invokes the template with null when it clears a recycled row.
            if (segment is null) return null;
            var row = new Grid { ColumnDefinitions = new("*,Auto"), [!Grid.ColumnSpacingProperty] = Resource("AcRowGap"),Margin=new(0,3) };
            row.Children.Add(Ui.Text(MediaTime.Format(segment.Start) + " – " + MediaTime.Format(segment.End)));
            var remove=Ui.Button("移除",()=>
            {
                if(_busy || SelectedTaskActive || entry.Result is not {} current)return;
                entry.Result=current with { Segments=current.Segments.Where(item=>!ReferenceEquals(item,segment)).ToArray() };RefreshResults();UpdateDetectorSelection();
            });
            remove.Bind(Button.IsEnabledProperty, new Binding(nameof(Button.IsEnabled)) { Source = _review });
            Grid.SetColumn(remove,1);row.Children.Add(remove);return new AvaMedia.Controls.ListRow { Child = row };
        });
        _retained.ItemsSource=result.Segments;
    }

    private async Task ReviewAsync()
    {
        if (_busy || SelectedTaskActive || Selected is not { Result: { } result } entry || result.Segments.Count == 0) return;
        var editor = new EditorWindow(_engine, entry.Path, result.Segments[0], "quick-workflow", result.Segments);
        try
        {
            editor.SetWorkflowCompletion("保存片段");
            var revised = await ToolExecution.ShowAsync<ClipEditResult>(this,editor);
            if (!_closed && revised is not null) { CheckSource(entry); entry.Result = revised; RefreshResults(); UpdateDetectorSelection(); }
        }
        catch(Exception error){ _status.Text=error.Message; }
    }
    private static void CheckSource(Entry entry)
    {
        var file = new FileInfo(entry.Path);
        if (!file.Exists || file.Length != entry.Length || file.LastWriteTimeUtc != entry.WriteUtc)
            throw new IOException("分析后源视频已改变，请重新分析：" + Path.GetFileName(entry.Path));
    }
}
