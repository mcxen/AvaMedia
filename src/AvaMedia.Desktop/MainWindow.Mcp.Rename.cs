using System.Text.Json;
using AvaMedia.Core;
using AvaMedia.Mcp;

namespace AvaMedia.Desktop;

public partial class MainWindow
{
    private sealed partial class DesktopMcpWorkspace
    {
        public async Task<McpRenameUndo> UndoRenameAsync(Guid id, CancellationToken ct)
        {
            var operation = await Ui(() =>
            {
                if (!Engine.Settings.ExclusiveToolUnlocked) throw new InvalidOperationException("内部工具未激活。");
                var job = Find(id);
                if (job.FeatureId != "batch-rename" || job.State != JobState.Completed || job.Options.Rename is not { } rename)
                    throw new ArgumentException("只能撤销已完成的重命名任务。");
                var reservation = owner._queue.ReserveFiles(rename.Plan.SelectMany(item => new[] { item.Source, item.Target }));
                return (Journal: job.Output, Reservation: reservation);
            }, ct).ConfigureAwait(false);
            using (operation.Reservation)
            {
                var journal = JsonSerializer.Deserialize<BatchRename.RenameJournal>(await File.ReadAllTextAsync(operation.Journal, ct).ConfigureAwait(false))
                    ?? throw new InvalidDataException("恢复记录无效。");
                if (journal.State == "undone") return new(id, journal.Stages.Select(stage => stage.Item).ToArray(), true);
                ct.ThrowIfCancellationRequested();
                // Like the desktop button, complete commit/rollback and path synchronization even if the caller disconnects.
                var changes = await Task.Run(() => BatchRename.UndoRename(operation.Journal), CancellationToken.None).ConfigureAwait(false);
                await Ui(() => { owner.SynchronizeFileChanges(changes); return true; }, CancellationToken.None).ConfigureAwait(false);
                return new(id, changes, false);
            }
        }
    }
}
