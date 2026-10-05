using AvaMedia.Core;

var root = Path.GetFullPath(Path.Combine("artifacts", "batch-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]));
Directory.CreateDirectory(root);
var assertions = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); assertions++; Console.WriteLine("PASS: " + name); }
void Reject(Action action, string name) { try { action(); } catch (ArgumentException) { Check(true, name); return; } catch (IOException) { Check(true, name); return; } throw new Exception("FAIL: accepted " + name); }
var folder = Path.Combine(root, "文件夹 A"); var sub = Path.Combine(folder, "子目录"); Directory.CreateDirectory(sub);
var first = Path.Combine(folder, "视频 one.mp4"); var second = Path.Combine(folder, "视频 two.MKV");
await File.WriteAllTextAsync(first, "one"); await File.WriteAllTextAsync(second, "two"); await File.WriteAllTextAsync(Path.Combine(sub, "nested.mov"), "nested"); await File.WriteAllTextAsync(Path.Combine(sub, "ignore.txt"), "text");
Check(BatchVideoTools.CollectVideos([folder, first], false).Length == 2, "folder import filters videos and deduplicates");
Check(BatchVideoTools.CollectVideos([folder], true).Length == 3, "recursive folder import");
var rules = new RenameRules("{parent}_{index}", "片段-", "-成品", FirstIndex: 5, Digits: 3);
var plan = BatchVideoTools.PreviewRename([first, second], rules);
Check(Path.GetFileName(plan[0].Target) == "片段-文件夹 A_005-成品.mp4", "template / parent / prefix / suffix / zero padding");
Check(File.Exists(first) && !File.Exists(plan[0].Target), "preview does not mutate files");
var journal = Path.Combine(root, "rename-journal.json");
var changed = BatchVideoTools.ApplyRename(plan, journal);
Check(changed.Length == 2 && File.ReadAllText(plan[0].Target) == "one" && File.ReadAllText(plan[1].Target) == "two", "apply keeps content and extension");
BatchVideoTools.UndoRename(journal);
Check(File.ReadAllText(first) == "one" && File.ReadAllText(second) == "two", "undo restores original names");
Reject(() => BatchVideoTools.PreviewRename([first], new("CON")), "reserved name rejection");
Reject(() => BatchVideoTools.PreviewRename([first], new("../outside")), "directory traversal rejection");
Reject(() => BatchVideoTools.PreviewRename([first], new("bad.")), "trailing dot rejection");
var sameExt = Path.Combine(folder, "duplicate.mp4"); await File.WriteAllTextAsync(sameExt, "other");
Reject(() => BatchVideoTools.PreviewRename([first, sameExt], new("same")), "duplicate targets rejected");
var occupied = Path.Combine(folder, "occupied.mp4"); await File.WriteAllTextAsync(occupied, "preserved");
Reject(() => BatchVideoTools.PreviewRename([first], new("occupied")), "existing destination never overwritten");
Check(File.ReadAllText(occupied) == "preserved", "existing content preserved");
var stale = BatchVideoTools.PreviewRename([first], new("stale")); await File.AppendAllTextAsync(first, "changed");
Reject(() => BatchVideoTools.ApplyRename(stale, journal), "changed source invalidates old preview");
var swapA = Path.Combine(folder, "swap-a.mp4"); var swapB = Path.Combine(folder, "swap-b.mp4"); await File.WriteAllTextAsync(swapA, "A"); await File.WriteAllTextAsync(swapB, "B");
RenameItem Mapping(string source, string target) { var info = new FileInfo(source); return new(source, target, info.Length, info.LastWriteTimeUtc); }
BatchVideoTools.ApplyRename([Mapping(swapA, swapB), Mapping(swapB, swapA)], journal);
Check(File.ReadAllText(swapA) == "B" && File.ReadAllText(swapB) == "A", "two-phase rename permits name swaps");
BatchVideoTools.UndoRename(journal);
Check(File.ReadAllText(swapA) == "A" && File.ReadAllText(swapB) == "B", "undo name swaps");
if (OperatingSystem.IsWindows())
{
    var rollbackPlan = BatchVideoTools.PreviewRename([swapA, swapB], new("renamed_{index}"));
    using (var locked = new FileStream(swapB, FileMode.Open, FileAccess.Read, FileShare.None))
        Reject(() => BatchVideoTools.ApplyRename(rollbackPlan, journal), "locked file aborts batch");
    Check(File.ReadAllText(swapA) == "A" && File.ReadAllText(swapB) == "B" && !File.Exists(rollbackPlan[0].Target), "failed batch rolls back prior moves");
}
Reject(() => BatchVideoTools.ValidateContactSheet(new(Rows: 0)), "invalid grid rejection");
Reject(() => BatchVideoTools.ValidateContactSheet(new(Columns: 10, Rows: 10, CellWidth: 1920, CellHeight: 1080)), "oversized sheet rejection");
Reject(() => BatchVideoTools.ValidateContactSheet(new(StartSeconds: double.NaN)), "invalid time rejection");

var engine = new MediaEngine(new());
var landscape = Path.Combine(root, "横屏 video.mp4"); var portrait = Path.Combine(root, "竖屏 video.mp4");
foreach (var (path, size) in new[] { (landscape, "320x180"), (portrait, "180x320") })
{
    var result = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-n", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=25", "-t", "3", "-c:v", "mpeg4", "-q:v", "2", path]);
    Check(result.ExitCode == 0, "create fixture " + size);
}
var output = Path.Combine(root, "输出");
var grid = await BatchVideoTools.GenerateContactSheets(engine, landscape, output, new(CellWidth: 160, CellHeight: 90, Format: "png"));
var info = await engine.Probe(grid[0]);
Check(grid.Length == 1 && info.Width == 516 && info.Height == 306, "3x3 timestamped PNG dimensions");
var grids = await BatchVideoTools.GenerateContactSheets(engine, portrait, output, new(Columns: 2, Rows: 2, CellWidth: 160, CellHeight: 90, SheetsPerVideo: 2, Format: "jpg", StartSeconds: .2, EndSeconds: 2.8));
var portraitInfo = await engine.Probe(grids[0]);
Check(grids.Length == 2 && portraitInfo.Width == 350 && portraitInfo.Height == 210, "portrait padding and multiple 2x2 JPG sheets");
var duplicateGrid = await BatchVideoTools.GenerateContactSheets(engine, landscape, output, new(CellWidth: 160, CellHeight: 90, Format: "png", Timestamps: false));
Check(grid[0] != duplicateGrid[0] && File.Exists(grid[0]) && File.Exists(duplicateGrid[0]), "duplicate screenshot names do not overwrite");
var shortClip = Path.Combine(root, "very-short.mp4");
var shortResult = await ProcessRunner.Run(engine.FFmpeg, ["-v", "error", "-n", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5", "-t", "0.4", "-c:v", "mpeg4", shortClip]);
Check(shortResult.ExitCode == 0, "create very short low-frame-rate fixture");
var shortGrid = await BatchVideoTools.GenerateContactSheets(engine, shortClip, output, new(Columns: 4, Rows: 4, CellWidth: 100, CellHeight: 64, Timestamps: false));
Check(shortGrid.Length == 1 && File.Exists(shortGrid[0]), "very short clip still fills every grid cell");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { await BatchVideoTools.GenerateContactSheets(engine, landscape, output, new(), ct: cancelled.Token); throw new Exception("Cancellation was ignored"); }
    catch (OperationCanceledException) { Check(true, "cancel screenshot generation"); }
}
Console.WriteLine($"{assertions} assertions passed. Artifacts: {root}");
