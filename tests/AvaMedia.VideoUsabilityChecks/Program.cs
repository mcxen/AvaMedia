using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaMedia.Core;
using AvaMedia.Desktop;

// Run identical interaction scenarios against baseline, individual source patches and combined code.
// Synthetic source metadata and a replaceable media backend isolate UI behavior from model/codec quality.
var variant = args.ElementAtOrDefault(0) ?? "combined";
var output = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/video-usability-ablation/combined");
Directory.CreateDirectory(output);
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true }).SetupWithoutStarting();
Localization.Apply("zh-CN"); Motion.SetReducedMotion(true);
var observations = new Dictionary<string, object>();
var checks = new List<string>();
var uiErrors = new List<string>();
Dispatcher.UIThread.UnhandledException += (_, error) => { uiErrors.Add(error.Exception.ToString()); error.Handled = true; };
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks.Add(name); Console.WriteLine("PASS " + name);
}
void Observe(string name, object value) { observations[name] = value; Console.WriteLine(name + " = " + value); }
bool Has(string group) => variant == "combined" || variant == group;
void Pump(int milliseconds = 0)
{
    Dispatcher.UIThread.RunJobs();
    if (milliseconds > 0)
    {
        using var cancellation = new CancellationTokenSource();
        DispatcherTimer.RunOnce(cancellation.Cancel, TimeSpan.FromMilliseconds(milliseconds));
        Dispatcher.UIThread.MainLoop(cancellation.Token);
    }
    Dispatcher.UIThread.RunJobs();
}
void Until(Func<bool> condition)
{
    var timer = Stopwatch.StartNew();
    while (!condition() && timer.ElapsedMilliseconds < 1500) Pump(10);
}
T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
Task InvokeAsync(object owner, string name) => (Task)owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null)!;
T Named<T>(Window owner, string name) where T : Control => owner.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
Button Button(Window owner, string text) => owner.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, text));
void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Pump(); }
Window Construct(Type type, Dictionary<string, object?> values)
{
    var constructor = type.GetConstructors().Single();
    return (Window)constructor.Invoke(constructor.GetParameters().Select(parameter => values.TryGetValue(parameter.Name!, out var value) ? value : parameter.DefaultValue).ToArray());
}
string Fixture(string name) { var path = Path.Combine(output, name + ".mp4"); File.WriteAllText(path, name); return path; }
ConversionOptions Reviewed(string path)
{
    var source = new FileInfo(path);
    return new() { Format = "srt", Transcription = new() { ReviewedCues = [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "第一条"), new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "第二条")],
        ReviewedSourceLength = source.Length, ReviewedSourceWriteUtc = source.LastWriteTimeUtc } };
}
var userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia");
string UserState() => string.Join('|', new[] { "settings.json", "queue.json", "tool-auto-subtitle.json", "tool-media-ai.json", "tool-semantic-rename.json" }
    .Select(name => File.Exists(Path.Combine(userRoot, name)) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(userRoot, name)))) : "missing"));
var priorUserState = UserState();
var engine = new ScenarioEngine();
var first = Fixture("first" ); var second = Fixture("second");
var owner = new Window { Width = 400, Height = 200 }; owner.Show(); Pump();
SubtitleReviewWindow Review(string path, string format = "srt")
{
    var options = Reviewed(path); options.Format = format;
    return new(engine, new(Catalog.Find("auto-subtitle"), [path], output, options), false);
}

// A: draft retention follows actual selection changes, including incomplete time input.
var review = new SubtitleReviewWindow(engine, new(Catalog.Find("auto-subtitle"), [first, second], output, Reviewed(first), InputOptions: [Reviewed(first), Reviewed(second)]), false);
var reviewResult = review.ShowDialog<ConversionRequest?>(owner); Pump();
var cues = Field<ListBox>(review, "_cues"); var files = Field<ListBox>(review, "_files");
var text = Field<TextBox>(review, "_text"); var start = Field<TextBox>(review, "_start");
cues.SelectedIndex = 0; text.Text = "已修改文字"; Pump(); cues.SelectedIndex = 1; cues.SelectedIndex = 0; Pump();
var keptCue = text.Text == "已修改文字";
Observe("A.text_retained_across_cues", keptCue); Check(keptCue == Has("a"), "A cue switch matches this source variant");
text.Text = "跨文件修改"; Pump();
var fileSwitchFailed = false;
try { files.SelectedIndex = 1; files.SelectedIndex = 0; cues.SelectedIndex = 0; Pump(); }
catch (Exception error) { fileSwitchFailed = true; uiErrors.Add(error.ToString()); }
var keptFile = !fileSwitchFailed && text.Text == "跨文件修改";
Observe("A.file_switch_exception", fileSwitchFailed);
Observe("A.text_retained_across_files", keptFile); Check(keptFile == Has("a"), "A file switch matches this source variant");
if (fileSwitchFailed)
{
    review.Close(); Pump();
    review = new SubtitleReviewWindow(engine, new(Catalog.Find("auto-subtitle"), [first, second], output, Reviewed(first), InputOptions: [Reviewed(first), Reviewed(second)]), false);
    reviewResult = review.ShowDialog<ConversionRequest?>(owner); Pump();
    cues = Field<ListBox>(review, "_cues"); text = Field<TextBox>(review, "_text"); start = Field<TextBox>(review, "_start"); cues.SelectedIndex = 0;
}
start.Text = "00:"; Pump(); cues.SelectedIndex = 1; cues.SelectedIndex = 0; Pump();
var keptDraft = start.Text == "00:";
Observe("A.incomplete_time_retained", keptDraft); Check(keptDraft == Has("a"), "A unfinished time stays editable without navigation interception");
var overlap = new TranscriptionOptions { ReviewedCues = [new(TimeSpan.Zero, TimeSpan.FromSeconds(2), "说话者一"), new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "说话者二")] };
var overlapAllowed = true; try { overlap.Validate(); } catch (ArgumentException) { overlapAllowed = false; }
Observe("A.overlap_allowed", overlapAllowed); Check(overlapAllowed == Has("a"), "A overlapping subtitles match this source variant");
start.Text = "00:00:00.000"; Field<TextBox>(review, "_end").Text = "00:00:02.000"; text.Text = "导出前修改"; Pump();
cues.SelectedIndex = 1; Field<TextBox>(review, "_end").Text = "00:00:03.000"; Pump();
Click(Field<Button>(review, "_export")); Pump();
var editedExport = reviewResult.IsCompleted && reviewResult.Result?.InputOptions?[0].Transcription?.ReviewedCues is { } exported && exported[0].Text == "导出前修改" && exported[0].End.TotalSeconds == 2;
Observe("A.edits_exported_with_overlap", editedExport); Check(editedExport == Has("a"), "A all edited drafts reach the export request");
if (!reviewResult.IsCompleted) review.Close(); Pump();

// B: recognition has no batch preflight gate or output-location prerequisite.
var failing = Fixture("unreadable"); var noAudio = Fixture("no-audio");
var storage = new Storage(Path.Combine(output, "state"));
var speech = Construct(typeof(SpeechToolsWindow), new() { ["engine"] = engine, ["feature"] = Catalog.Find("auto-subtitle"), ["outputFolder"] = output,
    ["files"] = new[] { failing, noAudio }, ["options"] = new ConversionOptions { Format = "srt", Transcription = new() }, ["storage"] = storage });
speech.Show(owner); Pump();
engine.Probed.Clear(); Click(Named<Button>(speech, "SpeechConfirm"));
Until(() => engine.Probed.Count >= 2 || !Field<bool>(speech, "_busy"));
var reachedReview = speech.OwnedWindows.OfType<SubtitleReviewWindow>().FirstOrDefault();
Observe("B.review_opened_after_bad_source", reachedReview is not null);
Observe("B.sources_attempted", engine.Probed.Count);
Check((reachedReview is not null) == Has("b"), "B recognition enters the review window directly");
var completedBatch = engine.Probed.Count == 2;
Observe("B.batch_progress_after_bad_source", completedBatch);
Check(completedBatch == (variant == "combined"), "B batch progress also requires A to update rows without rebuilding the file list");
reachedReview?.Close(); speech.Close(); Pump();
var noFolder = Construct(typeof(SpeechToolsWindow), new() { ["engine"] = engine, ["feature"] = Catalog.Find("auto-subtitle"), ["outputFolder"] = "",
    ["files"] = new[] { first }, ["options"] = Reviewed(first), ["editing"] = true, ["storage"] = storage });
noFolder.Show(owner); Pump();
var allowedWithoutFolder = Named<Button>(noFolder, "SpeechConfirm").IsEnabled;
Observe("B.recognition_enabled_without_output_folder", allowedWithoutFolder); Check(allowedWithoutFolder == Has("b"), "B output location is chosen at export");
var outputSelectors = noFolder.GetVisualDescendants().OfType<ComboBox>().Count(combo => combo.Name == "SpeechOutput");
Observe("B.output_selectors_before_recognition", outputSelectors); Check(outputSelectors == (Has("b") ? 0 : 1), "B output format appears once in the workflow");
noFolder.Close();
var editSpeech = Construct(typeof(SpeechToolsWindow), new() { ["engine"] = engine, ["feature"] = Catalog.Find("auto-subtitle"), ["outputFolder"] = output,
    ["files"] = new[] { first }, ["options"] = Reviewed(first), ["editing"] = true, ["storage"] = storage });
var request = ((SpeechToolsWindow)editSpeech).ReadRequest();
Observe("B.reviewed_cues_reused", request.Options.Transcription?.ReviewedCues is not null);
Check((request.Options.Transcription?.ReviewedCues is not null) == Has("b"), "B unchanged recognition settings keep reviewed subtitles");
editSpeech.Close(); Pump();

// C: unfinished optional parameters cannot trap their dialog; execution still validates styles.
var ai = Construct(typeof(MediaAiWindow), new() { ["engine"] = engine, ["settings"] = engine.Settings, ["initial"] = new[] { first },
    ["manageModels"] = (Func<Window, Task>)(_ => Task.CompletedTask), ["storage"] = storage });
ai.Show(owner); Pump();
var advancedTask = InvokeAsync(ai, "OpenAdvancedAsync"); Pump();
var advanced = ai.OwnedWindows.Single(); var frames = Field<NumericUpDown>(ai, "_frames"); var oldFrames = frames.Value;
frames.Text = "-"; advanced.Close(); Pump();
Observe("C.ai_settings_close_with_incomplete_number", advancedTask.IsCompleted);
Check(advancedTask.IsCompleted == Has("c"), "C AI settings can close with unfinished input");
if (Has("c")) Check(frames.Value == oldFrames && decimal.TryParse(frames.Text, out _), "C AI sampling restores its last usable value");
else advanced.Hide();
ai.Hide();
var person = new PersonClipWindow(engine, engine.Settings, [first], (_, _) => Task.CompletedTask, output);
person.Show(owner); Pump();
var personAdvancedTask = InvokeAsync(person, "OpenAdvancedAsync"); Pump();
var personAdvanced = person.OwnedWindows.Single(); Field<NumericUpDown>(person, "_fps").Text = "-"; personAdvanced.Close(); Pump();
Observe("C.person_settings_close_with_incomplete_number", personAdvancedTask.IsCompleted);
Check(personAdvancedTask.IsCompleted == Has("c"), "C person settings do not intercept window close");
if (!personAdvancedTask.IsCompleted) personAdvanced.Hide(); person.Hide();
var styled = Review(first, "ass"); var styledResult = styled.ShowDialog<ConversionRequest?>(owner); Pump();
Click(Button(styled, "字幕样式…")); Pump();
var styleDialog = styled.OwnedWindows.Single(); var color = Named<TextBox>(styleDialog, "SubtitleColor");
color.Text = "#"; Click(Button(styleDialog, "关闭")); Pump();
var styleClosed = !styleDialog.IsVisible;
Observe("C.style_dialog_close_with_incomplete_color", styleClosed); Check(styleClosed == Has("c"), "C optional subtitle styling permits close");
if (!styleClosed) styleDialog.Close();
// Close(object) from the titlebar bypasses the baseline's guarded Close button, allowing the export check.
if (styleDialog.IsVisible) styleDialog.Hide();
Click(Field<Button>(styled, "_export")); Pump();
Check(!styledResult.IsCompleted && Field<TextBlock>(styled, "_notice").Text!.Contains("颜色"), "Invalid subtitle color is rejected at export in every variant");
styled.Close(); Pump();

// D: preparation parameters and candidates affect the next match, without wiping manual results.
var rename = new RenameWindow(engine, engine.Settings, [first], Path.Combine(output, "rename.json")); rename.Show(owner); Pump();
Field<CheckBox>(rename, "_semantic").IsChecked = true; Pump();
var entry = Field<IEnumerable>(rename, "_entries").Cast<MediaFileEntry>().Single(); entry.Keyword = "手工命名"; entry.Include = true;
Field<TextBox>(rename, "_keywords").Text = "新的候选词"; Pump();
var keptKeyword = entry.Keyword == "手工命名" && entry.Include;
Observe("D.manual_name_retained_after_candidates_change", keptKeyword); Check(keptKeyword == Has("d"), "D candidates do not erase manual naming or selection");
entry.Keyword = "手工命名"; entry.Include = true; Field<NumericUpDown>(rename, "_semanticFrames").Text = "12"; Pump();
var keptSampling = entry.Keyword == "手工命名" && entry.Include;
Observe("D.manual_name_retained_after_sampling_change", keptSampling); Check(keptSampling == Has("d"), "D sampling changes preserve manual naming and selection");
rename.Close(); Pump();
var tagWindow = Construct(typeof(MediaAiWindow), new() { ["engine"] = engine, ["settings"] = engine.Settings, ["initial"] = new[] { first },
    ["manageModels"] = (Func<Window, Task>)(_ => Task.CompletedTask), ["storage"] = storage });
tagWindow.Show(owner); Pump();
Field<Dictionary<string, MediaTagResult>>(tagWindow, "_results")[first] = new(first, NsfwModeration.Rules.Select(rule => new MediaTagScore(rule.Tag, 0, 0)).Append(new("black_hair", .95, .95)).ToArray(), 1, 1, "fixture", new FileInfo(first).Length, File.GetLastWriteTimeUtc(first));
tagWindow.GetType().GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tagWindow, [true]);
tagWindow.GetType().GetMethod("RenderSelectedResult", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tagWindow, null); Pump();
var editTags = Button(tagWindow, "编辑标签…");
Observe("D.completed_tags_editable_during_batch", editTags.IsEnabled); Check(editTags.IsEnabled == Has("d"), "D completed tags stay editable while other files are processing");
if (editTags.IsEnabled)
{
    Click(editTags); var tagDialog = tagWindow.OwnedWindows.Single();
    var tagText = tagDialog.GetVisualDescendants().OfType<TextBox>().Single(); tagText.Text = "人工标签"; Click(Button(tagDialog, "保存"));
    Check(Field<IDictionary>(tagWindow, "_editedTags").Contains(first), "D editing a completed result saves the manual override during analysis");
}
tagWindow.Close(); Pump();

// Required boundaries: actual invalid cue values and changed sources remain blocked.
foreach (var cue in new[] { new SubtitleCue(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1), "文字"), new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), "文字"), new(TimeSpan.Zero, TimeSpan.FromSeconds(1), " ") })
{
    var rejected = false; try { new TranscriptionOptions { ReviewedCues = [cue] }.Validate(); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "Invalid cue remains rejected: " + cue);
}
var changed = Fixture("changed"); var changedReview = Review(changed); var changedResult = changedReview.ShowDialog<ConversionRequest?>(owner); Pump();
File.AppendAllText(changed, "changed"); Click(Field<Button>(changedReview, "_export")); Pump();
Check(!changedResult.IsCompleted && Field<TextBlock>(changedReview, "_notice").Text!.Contains("改变"), "Changed source cannot reuse stale subtitles");
changedReview.Close(); Pump();
var clipJobs = QuickClipWorkflow.PrepareJoinedJobs([new(first, new(30, 640, 360, true, true, "{}"), [new() { Start = 1, End = 5 }])], "Fast Copy", new(), output, false, "People");
Check(clipJobs.Count == 1 && clipJobs[0].FeatureId == "clip" && clipJobs[0].Options.CopyStreams, "One continuous retained interval becomes one direct stream-copy clip job");
owner.Close(); Pump();
Check(priorUserState == UserState(), "Ablation leaves user preferences and queue unchanged");
Observe("UI.errors_count", uiErrors.Count);
Check(Has("a") ? uiErrors.Count == 0 : uiErrors.Count > 0, "A fixes observed baseline item recycling exceptions");
File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { variant, observations, checks, uiErrors }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULT {checks.Count} checks {output}");

sealed class ScenarioEngine : IMediaEngine
{
    public AppSettings Settings { get; } = new() { EnableBetaFeatures = true, AutoDetectGpu = false, OutputToSource = false };
    public List<string> Probed { get; } = [];
    public string FFmpeg => "unused";
    public string FFprobe => "unused";
    public Task<MediaInfo> Probe(string path, CancellationToken ct = default, int videoStreamIndex = 0, int audioStreamIndex = 0)
    {
        Probed.Add(path);
        return Path.GetFileName(path).StartsWith("unreadable") ? Task.FromException<MediaInfo>(new IOException("fixture media cannot be read"))
            : Task.FromResult(new MediaInfo(30, 640, 360, false, true, "{}"));
    }
    public Task<byte[]> Thumbnail(string input, double seconds, int width = 640, int height = 360, CancellationToken ct = default, bool pad = true, int videoStreamIndex = 0, bool endExclusive = false) => Task.FromResult(Array.Empty<byte>());
    public Task<double> AdjacentFrameTime(string input, double seconds, int direction, CancellationToken ct = default, int videoStreamIndex = 0) => Task.FromResult(seconds);
    public Task Execute(Job job, Action<double> progress, CancellationToken ct) => throw new InvalidOperationException("Media encoding is outside this UI ablation.");
}
