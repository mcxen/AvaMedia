using Avalonia.Controls;
using Avalonia.Layout;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class MediaAiWindow
{
    private readonly Dictionary<string, ResultTag[]> _editedTags = new(BatchRename.PathComparer);
    private sealed record Preferences(decimal Threshold, decimal Frames, bool Gpu, bool Reuse, bool Recursive, bool Scores, bool OnlyLibrary, bool RecognizeScenes = true, double SceneThreshold = .55, decimal SceneMargin = .03m, int ScoreMode = 0, bool AutoTxt = false);
    private void LoadPreferences()
    {
        if(_storage.LoadToolOptions<Preferences>("media-ai") is not {} saved) return;
        _threshold.Value = Math.Clamp(saved.Threshold, .05m, .95m); _frames.Value = Math.Clamp(saved.Frames, 1, 32);
        _gpu.IsChecked = saved.Gpu; _reuse.IsChecked = saved.Reuse; _recursive.IsChecked = saved.Recursive;
        _showScores.IsChecked = saved.Scores; _onlyLibrary.IsChecked = saved.OnlyLibrary;
        _sceneTags.IsChecked = saved.RecognizeScenes;
        _sceneThreshold.Value = Math.Clamp(saved.SceneThreshold, .05, .95); _sceneMargin.Value = Math.Clamp(saved.SceneMargin, 0, .5m);
        _scoreMode.SelectedIndex = Math.Clamp(saved.ScoreMode, 0, 3); _autoTxt.IsChecked = saved.AutoTxt;
    }
    private void SavePreferences() => _storage.SaveToolOptions("media-ai", new Preferences(_threshold.Value ?? .4m, _frames.Value ?? 8,
        _gpu.IsChecked == true, _reuse.IsChecked == true, _recursive.IsChecked == true, _showScores.IsChecked == true, _onlyLibrary.IsChecked == true, _sceneTags.IsChecked == true, _sceneThreshold.Value, _sceneMargin.Value ?? .03m, _scoreMode.SelectedIndex, _autoTxt.IsChecked == true));
    private async Task EditTagsAsync(MediaTagResult result)
    {
        var original = ResultTags(result).ToArray();
        var input = Ui.Input(string.Join(Environment.NewLine, original.Select(tag => tag.Label)));
        input.AcceptsReturn = true; input.TextWrapping = Avalonia.Media.TextWrapping.Wrap; Localization.SetIsUserText(input, true);
        var window = new Window { Title = "编辑标签", Width = 480, Height = 420, MinWidth = 360, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new Grid { RowDefinitions = new("*,Auto"), RowSpacing = 12, Margin = new(20) }; root.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(Ui.DialogButton("取消", window.Close));
        actions.Children.Add(Ui.DialogButton("保存", () =>
        {
            _editedTags[result.Path] = (input.Text ?? "").Split(['\r', '\n', ',', '，'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(label => original.FirstOrDefault(tag => tag.Label.Equals(label, StringComparison.OrdinalIgnoreCase)) ?? new ResultTag(label, "手动标签", 1, "manual", "manual")).ToArray();
            window.Close(); RefreshDisplayedResults();
        }));
        Grid.SetRow(actions, 1); root.Children.Add(actions); window.Content = root; await window.ShowDialog(this);
    }
    private async Task ShowEvidenceAsync(MediaTagResult result, ResultTag tag)
    {
        var matches = tag.Model == ModelCatalog.EmbeddingId
            ? result.Scenes?.Frames.Where(frame => frame.Candidates.Any(candidate => candidate.Label == tag.Label
                && candidate.Similarity >= _sceneThreshold.Value && candidate.Margin >= (double)(_sceneMargin.Value ?? .03m))).Select(frame => frame.Seconds).ToArray() ?? []
            : TagPoints(result, tag).Where(point => point.Score >= (double)(_threshold.Value ?? .4m)).Select(point => point.Seconds).Distinct().Order().ToArray();
        var window = new Window { Title = tag.Label, Width = 500, Height = 420, MinWidth = 380, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        Localization.SetIsUserText(window, true);
        var content = new StackPanel { Spacing = 8, Margin = new(20) };
        if(matches.Length == 0) content.Children.Add(Ui.Text(result.Frames.Count == 0 ? "此结果没有视频采样时间" : "没有达到当前阈值的采样画面", "caption"));
        foreach(var seconds in matches)
            content.Children.Add(Ui.Button(MediaTime.Format(seconds) + " · " + Localization.Text("播放画面"), async () =>
            {
                try
                {
                    var file = new FileInfo(result.Path);
                    if(!file.Exists || file.Length != result.Length || file.LastWriteTimeUtc != result.LastWriteUtc) throw new IOException("源文件已改变，请重新分析。");
                    var player = new PlayerWindow(_engine); player.ShowForPlayback(this); await player.OpenAtAsync(result.Path, seconds); window.Close();
                }
                catch(Exception error) { await Ui.Message(window, "无法播放", error.Message); }
            }));
        window.Content = new ScrollViewer { Content = content }; await window.ShowDialog(this);
    }
}
