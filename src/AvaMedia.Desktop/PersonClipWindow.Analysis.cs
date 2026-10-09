using Avalonia.Controls;
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
    private readonly AiActivityView _activity = new() { Compact = true };
    private CancellationTokenSource? _analysis;
    private bool _busy;

    private PersonClipOptions ReadDetection() => new(Value(_fps), Value(_threshold), Value(_padding), Value(_gap), Value(_minimum),
        _uncertain.IsChecked == true, _embedding.IsChecked == true, _gpu.IsChecked == true, _reuseFrames.IsChecked == true,
        SelectedDetectors, (PersonDetectionMode)_detectionMode.SelectedIndex, SkipDarkFrames: _dark.IsChecked == true,
        SkipBlankFrames: _blank.IsChecked == true, DarkLumaThreshold: Value(_darkThreshold));

    private async Task OpenAdvancedAsync()
    {
        if (_busy) return;
        var window = new Window { Title = "人物剪辑 · 高级设置", Width = 520, Height = 620, MinWidth = 460, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var content = new ScrollViewer { Content = _advancedParameters };
        var root = new Grid { RowDefinitions = new("*,Auto"), Margin = new(20), RowSpacing = 12 }; root.Children.Add(content);
        var close = Ui.DialogButton("关闭", window.Close); close.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(close, 1); root.Children.Add(close); window.Content = root;
        window.Closed += (_, _) =>
        {
            content.Content = null;
            foreach (var input in new[] { _fps, _threshold, _padding, _gap, _minimum, _darkThreshold }) ToolInputs.CommitNumber(input);
        };
        await window.ShowDialog(this);
    }

    private async Task AnalyzeAsync()
    {
        if (_busy || _closed || !_engine.Settings.EnableBetaFeatures) return;
        PersonClipOptions options;
        try { options = ReadDetection(); options.Validate(); new Storage().SaveToolOptions("person-clip", options with { ExcludedRanges = null }); }
        catch (Exception error) { _status.Text = error.Message; return; }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _analysis = operation; _busy = true; _stop.IsVisible = true; _rangePanel.IsEnabled = false; UpdateDetectorSelection();
        try
        {
            var store = new ModelStore();
            foreach (var id in options.SelectedDetectors.Concat(options.UseEmbedding ? new[] { ModelCatalog.EmbeddingId } : []))
            {
                if (await store.IsInstalledAsync(id, ct: operation.Token)) continue;
                var progress = new Progress<ModelDownloadProgress>(value =>
                { if (!_closed && _analysis == operation) _status.Text = Localization.Text("下载模型") + $" · {value.Percent:0}%"; });
                await store.DownloadAsync(id, progress, operation.Token);
            }
            var repeat = _entries.All(entry => entry.Result is not null);
            foreach (var entry in _entries.ToArray())
            {
                operation.Token.ThrowIfCancellationRequested();
                if (entry.Result is not null && !repeat) continue;
                entry.Error="";
                entry.Status = "分析中"; RefreshFiles();
                var progress = new Progress<PersonClipProgress>(value =>
                {
                    if (_closed || _analysis != operation) return;
                    _status.Text = Path.GetFileName(entry.Path) + " · " + Localization.Text(value.Stage);
                    if (value.Activity is not null) _activity.Update(value.Activity);
                });
                try
                {
                    var file = new FileInfo(entry.Path); entry.Length=file.Length;entry.WriteUtc=file.LastWriteTimeUtc;
                    entry.Result=null;
                    var result = await new PersonClipAnalysis(_engine, store).AnalyzeAsync(entry.Path, options with { ExcludedRanges = entry.Excluded }, progress, operation.Token);
                    CheckSource(entry); entry.Result = new(entry.Path, result.Info, result.Segments);
                    entry.Status = result.Segments.Count > 0 ? "已分析" : "没有可保留片段";
                }
                catch (OperationCanceledException) { entry.Status = entry.Result is null ? "待分析" : "已分析"; throw; }
                catch (Exception error) { entry.Status = "失败"; entry.Error=error.Message; _status.Text = Path.GetFileName(entry.Path) + " · " + error.Message; }
                RefreshFiles();
            }
            _status.Text = Localization.Format($"已分析 {_entries.Count(entry => entry.Result is not null)} / {_entries.Count} 个视频");
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = Localization.Text("分析已停止，已完成结果保留"); }
        catch (Exception error) { if (!_closed) _status.Text = error.Message; }
        finally
        {
            _analysis = null; _busy = false;
            if (!_closed) { _stop.IsVisible = false; RefreshFiles(); }
        }
    }

    private void RefreshResults()
    {
        _retained.ItemsSource=null;
        var entry = Selected;
        _review.IsEnabled = !_busy && entry?.Result?.Segments.Count > 0;
        _rangePanel.IsEnabled = !_busy && entry is not null;
        if (entry?.Result is not { } result) { _resultSummary.Text = entry?.Error.Length>0?entry.Error:Localization.Text(entry?.Status ?? "尚未分析"); return; }
        _resultSummary.Text = Localization.Format($"保留 {result.Segments.Count} 个片段 · {MediaTime.Format(result.Segments.Sum(segment => segment.End - segment.Start))}");
        _retained.ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<ConversionOptions>((segment,_)=>
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8,Margin=new(0,3) };
            row.Children.Add(Ui.Text(MediaTime.Format(segment!.Start) + " – " + MediaTime.Format(segment.End)));
            var remove=Ui.Button("移除",()=>
            {
                if(entry.Result is not {} current)return;
                entry.Result=current with { Segments=current.Segments.Where(item=>!ReferenceEquals(item,segment)).ToArray() };RefreshResults();UpdateDetectorSelection();
            });remove.IsEnabled=!_busy;Grid.SetColumn(remove,1);row.Children.Add(remove);return row;
        });
        _retained.ItemsSource=result.Segments;
    }

    private async Task ReviewAsync()
    {
        if (_busy || Selected is not { Result: { } result } entry || result.Segments.Count == 0) return;
        var editor = new EditorWindow(_engine, entry.Path, result.Segments[0], "quick-workflow", result.Segments);
        try
        {
            editor.SetWorkflowCompletion("保存片段");
            var revised = await editor.ShowDialog<ClipEditResult?>(this);
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
