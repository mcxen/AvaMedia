using System.Text.Json;

namespace AvaMedia.Core;

public sealed record FilePickerVisit(string Path, DateTimeOffset Time);
public sealed record FilePickerState(IReadOnlyList<FilePickerVisit> Files, IReadOnlyList<FilePickerVisit> Folders,
    IReadOnlyList<string> Favorites, bool Grid = true);

/// <summary>One atomic state file; bounded history stores paths and visit times only.</summary>
public sealed class FilePickerStateStore(string? root = null)
{
    private readonly string _path = Path.Combine(root ?? Storage.DefaultRoot, "file-picker.json");
    private readonly SemaphoreSlim _gate = new(1);
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public Task<FilePickerState> LoadAsync(CancellationToken token = default) => ChangeAsync(null, token);
    public Task<FilePickerState> RecordAsync(IEnumerable<string> files, IEnumerable<string> folders, CancellationToken token = default)
    {
        var selected = files.Select(Path.GetFullPath).ToArray();
        var directories = folders.Concat(selected.Select(Path.GetDirectoryName).OfType<string>()).Select(Path.GetFullPath).ToArray();
        return ChangeAsync(state => state with { Files = Visits(selected, state.Files, 64), Folders = Visits(directories, state.Folders, 16) }, token);
    }
    public Task<FilePickerState> FavoriteAsync(string path, bool add, CancellationToken token = default)
        => ChangeAsync(state => state with { Favorites = (add ? state.Favorites.Append(Path.GetFullPath(path))
            : state.Favorites.Where(item => !Paths.Equals(item, Path.GetFullPath(path)))).Distinct(Paths).Take(128).ToArray() }, token);
    public Task<FilePickerState> SetGridAsync(bool grid, CancellationToken token = default)
        => ChangeAsync(state => state with { Grid = grid }, token);

    private static FilePickerVisit[] Visits(IEnumerable<string> paths, IReadOnlyList<FilePickerVisit> previous, int maximum)
        => paths.Select(path => new FilePickerVisit(path, DateTimeOffset.UtcNow)).Concat(previous)
            .DistinctBy(visit => visit.Path, Paths).OrderByDescending(visit => visit.Time).Take(maximum).ToArray();

    private Task<FilePickerState> ChangeAsync(Func<FilePickerState, FilePickerState>? change, CancellationToken token)
        => Task.Run(async () =>
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                FilePickerState state = new([], [], []);
                try
                {
                    await using var input = File.OpenRead(_path);
                    state = await JsonSerializer.DeserializeAsync<FilePickerState>(input, cancellationToken: token).ConfigureAwait(false) ?? state;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
                state = state with
                {
                    Files = (state.Files ?? []).Where(item => File.Exists(item.Path)).DistinctBy(item => item.Path, Paths).OrderByDescending(item => item.Time).Take(64).ToArray(),
                    Folders = (state.Folders ?? []).Where(item => Directory.Exists(item.Path)).DistinctBy(item => item.Path, Paths).OrderByDescending(item => item.Time).Take(16).ToArray(),
                    Favorites = (state.Favorites ?? []).Where(Directory.Exists).Distinct(Paths).Take(128).ToArray()
                };
                if (change is not null) state = change(state);
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var output = File.Create(temporary))
                        await JsonSerializer.SerializeAsync(output, state, cancellationToken: token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested(); File.Move(temporary, _path, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return state;
            }
            finally { _gate.Release(); }
        }, token);
}
