using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class RenameWindow
{
    private readonly AppSettings _settings;
    private readonly Func<Window, Task>? _manageModels;
    private readonly CheckBox _semantic = new() { Content = "关键词匹配重命名 · Beta" };
    private readonly TextBox _keywords = new() { AcceptsReturn = true, Height = 88, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        Watermark = Localization.Text("做饭，骑车，海边") };
    private readonly NumericUpDown _semanticFrames = SemanticNumber(1, 32, 8, 1);
    private readonly CheckBox _semanticGpu = new() { Content = "自动适配 GPU", IsChecked = true };
    private readonly CheckBox _semanticReuse = new() { Content = "复用相似画面", IsChecked = true };
    private readonly NumericUpDown _semanticThreshold = SemanticNumber(0, 1, .55m, .05m);
    private readonly NumericUpDown _semanticMargin = SemanticNumber(0, 1, .03m, .01m);
    private readonly StackPanel _semanticPanel = new() { Spacing = 8, IsVisible = false };
    private readonly Controls.AiActivityView _semanticActivity = new();
    private readonly StackPanel _semanticParameters = new() { Spacing = 8, IsVisible = false };
    private readonly TextBlock _semanticModelStatus = Ui.Text("读取模型状态…", "caption");
    private readonly Dictionary<string, MediaKeywordResult> _semanticResults = new(BatchRename.PathComparer);
    private readonly Dictionary<string, string> _semanticDetails = new(BatchRename.PathComparer);
    private readonly CancellationTokenSource _semanticLifetime = new();
    private Button? _matchKeywords;
    private bool _semanticInstalled;
    private bool _semanticApplying;
    private string _ordinaryPattern = "{name}_{index}";
    private string _keywordPattern = "{keyword}_{index}";
    private bool SemanticEnabled => _settings.EnableBetaFeatures == true && _semantic.IsChecked == true;

    private void InitializeSemanticRename()
    {
        if (_settings.EnableBetaFeatures == true)
        {
            _semanticPanel.IsVisible = true;
            _semanticGpu.IsChecked = _settings.AutoDetectGpu;
            _semanticPanel.Children.Add(_semantic);
            _semantic.IsCheckedChanged += (_, _) =>
            {
                if (_semantic.IsChecked == true) { _ordinaryPattern = _pattern.Text ?? ""; _pattern.Text = _keywordPattern; }
                else { _keywordPattern = _pattern.Text ?? ""; _pattern.Text = _ordinaryPattern; }
                _semanticParameters.IsVisible = SemanticEnabled;
                InvalidatePlan();
            };
            Localization.SetIsUserText(_keywords, true);
            InitializeSemanticWordLibraries();
            _semanticParameters.Children.Add(_keywords);
            _semanticParameters.Children.Add(Ui.Text("逗号或换行分隔；可写“海边=人在海边散步”。", "caption"));
            AddRow(_semanticParameters, "每视频采样帧数", _semanticFrames);
            AddRow(_semanticParameters, "最低相似度", _semanticThreshold);
            AddRow(_semanticParameters, "关键词分差", _semanticMargin);
            _semanticParameters.Children.Add(_semanticGpu); _semanticParameters.Children.Add(_semanticReuse);
            _semanticParameters.Children.Add(Ui.Text("相似度不是概率；不确定结果可手动勾选和修改标签。", "caption"));
            _semanticParameters.Children.Add(Ui.Text("{keyword} 匹配关键词", "caption"));
            _semanticParameters.Children.Add(_semanticModelStatus);
            var models = Ui.Button("模型管理…", async () =>
            {
                try
                {
                    if (_manageModels is not null) await _manageModels(this);
                    if (_closed) return;
                    if (_settings.EnableBetaFeatures) await RefreshSemanticModelAsync();
                    else { _semantic.IsChecked = false; _semanticPanel.IsVisible = false; }
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { if (!_closed) _progressText.Text = error.Message; }
            });
            models.IsVisible = _manageModels is not null; _semanticParameters.Children.Add(models);
            _matchKeywords = Ui.Button("匹配并预览", async () => await MatchKeywordsAsync());
            _matchKeywords.IsEnabled = false; _semanticParameters.Children.Add(_matchKeywords);
            _semanticPanel.Children.Add(_semanticParameters); _renamePanel.Children.Add(_semanticPanel);
            _keywords.TextChanged += (_, _) => ClearSemanticMatches();
            _semanticGpu.IsCheckedChanged += (_, _) => ClearSemanticMatches(); _semanticReuse.IsCheckedChanged += (_, _) => ClearSemanticMatches();
            foreach (var number in new[] { _semanticFrames, _semanticThreshold, _semanticMargin })
                number.PropertyChanged += (_, change) =>
                { if (change.Property == NumericUpDown.ValueProperty || change.Property == NumericUpDown.TextProperty) ClearSemanticMatches(); };
            Opened += async (_, _) =>
            {
                try { await RefreshSemanticModelAsync(); }
                catch (OperationCanceledException) { }
                catch (Exception error) { if (!_closed) _semanticModelStatus.Text = error.Message; }
            };
        }
        Closed += (_, _) => { _semanticLifetime.Cancel(); _semanticLifetime.Dispose(); };
    }
    private static NumericUpDown SemanticNumber(decimal min, decimal max, decimal value, decimal step) => new()
    { Minimum = min, Maximum = max, Value = value, Increment = step, HorizontalAlignment = HorizontalAlignment.Stretch };
    private static double SemanticValue(NumericUpDown input)
    {
        if (!decimal.TryParse(input.Text, System.Globalization.NumberStyles.Number, input.NumberFormat, out var value)
            || value < input.Minimum || value > input.Maximum) throw new ArgumentException("请输入范围内的语义匹配参数。");
        return (double)value;
    }
    private async Task RefreshSemanticModelAsync()
    {
        var token = _semanticLifetime.Token;
        var installed = await Task.Run(() => new ModelStore().IsInstalledAsync(ModelCatalog.EmbeddingId, ct: token), token);
        if (_closed) return;
        _semanticInstalled = installed;
        _semanticModelStatus.Text = Localization.Text(installed ? "EmbeddingGemma 2 已下载" : "请先在模型管理中下载 EmbeddingGemma 2");
        if (_matchKeywords is not null) _matchKeywords.IsEnabled = installed;
    }
    private void ClearSemanticMatches()
    {
        if (_operation is not null || !SemanticEnabled) return;
        _semanticResults.Clear(); _semanticDetails.Clear(); _semanticApplying = true;
        try { foreach (var entry in _entries) { entry.Keyword = ""; entry.Include = false; entry.Status = "待匹配"; entry.Details = entry.Path; } }
        finally { _semanticApplying = false; }
        InvalidatePlan();
    }
    private void ValidateSemanticSources(IEnumerable<string> paths)
    {
        if (!SemanticEnabled) return;
        foreach (var path in paths)
        {
            if (!_semanticResults.TryGetValue(path, out var result)) continue;
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != result.Length || file.LastWriteTimeUtc != result.LastWriteUtc)
                throw new IOException("匹配后源文件已改变，请重新匹配：" + path);
        }
    }
    private async Task MatchKeywordsAsync()
    {
        if (!SemanticEnabled || !_semanticInstalled || _operation is not null || _renaming || _importing) return;
        SemanticKeyword[] keywords; MediaKeywordOptions options;
        try
        {
            if (_entries.Count == 0) throw new ArgumentException("请添加图片或视频。");
            keywords = SemanticCandidates();
            var frames = SemanticValue(_semanticFrames);
            if (frames != Math.Truncate(frames)) throw new ArgumentException("采样帧数须为整数。");
            options = new((int)frames, SemanticValue(_semanticThreshold), SemanticValue(_semanticMargin), _semanticReuse.IsChecked == true); options.Validate();
        }
        catch (Exception error) { await ShowErrorAsync("语义匹配失败", error); return; }
        ClearSemanticMatches();
        var selected = _entries.ToArray();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_semanticLifetime.Token);
        _operation = operation; SetBusy(true); _stop.IsEnabled = true;
        _progressText.Text = Localization.Text("加载嵌入模型…");
        _semanticActivity.Update(new("加载嵌入模型", "Gemma · 媒体嵌入", DateTime.UtcNow, DateTime.UtcNow));
        var matched = 0; var failed = 0;
        try
        {
            var modelProgress = new Progress<AiActivity>(activity => { if (!_closed && _operation == operation) _semanticActivity.Update(activity); });
            await using var matcher = await MediaKeywordMatcher.CreateAsync(_engine, keywords, ct: operation.Token, preferGpu: _semanticGpu.IsChecked == true, progress: modelProgress);
            for (var index = 0; index < selected.Length; index++)
            {
                operation.Token.ThrowIfCancellationRequested();
                var entry = selected[index]; var current = index; var finished = false;
                entry.Status = "匹配中…";
                var progress = new Progress<MediaKeywordProgress>(value =>
                {
                    if (_closed || _operation != operation || finished) return;
                    if (value.Activity is { } activity) _semanticActivity.Update(activity);
                    entry.Status = Localization.Text(value.Activity?.Stage ?? "匹配中") + (value.Activity?.Total > 0 ? $" {value.Activity.Current:0}/{value.Activity.Total:0}" : "");
                    Localization.SetText(_progressText, $"{entry.Name} · {current + 1}/{selected.Length} 个文件");
                });
                try
                {
                    var result = await matcher.MatchAsync(entry.Path, options, progress, operation.Token);
                    if (_closed) return;
                    finished = true;
                    _semanticResults[entry.Path] = result;
                    _semanticApplying = true;
                    try { entry.Keyword = result.Keyword; entry.Include = result.IsMatch; }
                    finally { _semanticApplying = false; }
                    entry.Status = Localization.Format($"{Localization.Key(result.IsMatch ? "已匹配" : "待确认")} · {result.Similarity:0.000}");
                    var scores = string.Join(Environment.NewLine, result.Scores.Take(20).Select(score => $"{score.Keyword}: {score.Similarity:0.000}"));
                    entry.Details = entry.Path + Environment.NewLine + scores + Environment.NewLine
                        + Localization.Format($"模型计算 {result.InferredFrames} 帧 · 复用 {result.ReusedFrames} 帧");
                    _semanticDetails[entry.Path] = entry.Details;
                    if (result.IsMatch) matched++;
                }
                catch (OperationCanceledException) { finished = true; if (!_closed) entry.Status = "已停止"; throw; }
                catch (Exception error)
                {
                    finished = true;
                    if (!_closed) { entry.Status = Localization.Format($"失败：{error.Message}"); entry.Details = entry.Path + Environment.NewLine + error.Message; }
                    AppDiagnostics.Record("Rename keyword matching", error); failed++;
                }
            }
            if (_closed) return;
            InvalidatePlan();
            if (matched > 0) await PreviewRename();
            Localization.SetText(_progressText, $"匹配完成：已勾选 {matched} 个，待确认 {selected.Length - matched - failed} 个，失败 {failed} 个。");
            _semanticActivity.Finish(AiActivityState.Completed, "关键词匹配完成");
        }
        catch (OperationCanceledException) { if (!_closed) { _semanticActivity.Finish(AiActivityState.Cancelled, "已停止"); _progressText.Text = Localization.Text("语义匹配已停止，已完成结果已保留。"); } }
        catch (Exception error) { if (!_closed) { _semanticActivity.Finish(AiActivityState.Failed, "匹配失败"); await ShowErrorAsync("语义匹配失败", error); } }
        finally
        {
            _operation = null;
            if (!_closed) { SetBusy(false); _stop.IsEnabled = false; InvalidatePlan(); }
        }
    }
}
