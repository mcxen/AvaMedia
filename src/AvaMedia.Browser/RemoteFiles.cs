using System.Text.Json.Nodes;
using Avalonia.Platform.Storage;
using AvaMedia.FileUi;

namespace AvaMedia.Browser;

internal sealed class RemoteFiles : IFileBrowserSource
{
    public async Task<IReadOnlyList<FileBrowserLocation>> GetLocationsAsync(CancellationToken ct)
        => (await RemoteApi.Call("file_locations", ct: ct))["locations"].Items()
            .Select(item => new FileBrowserLocation(item.Text("name"), new FileBrowserItem(item.Text("name"), item.Text("path"), true))).ToArray();
    public async Task<FileBrowserItem?> ResolveAsync(string address, CancellationToken ct)
    {
        var page = await RemoteApi.Call("browse_directory", new() { ["folder"] = address, ["limit"] = 1 }, ct);
        return Folder(page.Text("path"));
    }
    public async Task<FileBrowserDirectory> ReadAsync(FileBrowserItem folder, CancellationToken ct)
    {
        var (page, entries) = await Read(folder, false, ct);
        var parent = page.Text("parent");
        return new(folder, parent.Length == 0 ? null : Folder(parent), entries);
    }
    public async Task<IReadOnlyList<FileBrowserItem>> ReadFoldersAsync(FileBrowserItem folder, CancellationToken ct)
        => (await Read(folder, true, ct)).Entries.Select(entry => entry.Item).ToArray();
    private async Task<(JsonNode Page, List<FileBrowserEntry> Entries)> Read(FileBrowserItem folder, bool foldersOnly, CancellationToken ct)
    {
        var entries = new List<FileBrowserEntry>(); int? offset = 0; JsonNode page;
        do
        {
            page = await RemoteApi.Call("browse_directory", new() { ["folder"] = DisplayAddress(folder), ["offset"] = offset,
                ["limit"] = 200, ["showHidden"] = !foldersOnly, ["foldersOnly"] = foldersOnly }, ct);
            entries.AddRange(page["entries"].Items().Select(item => new FileBrowserEntry(
                new FileBrowserItem(item.Text("name"), item.Text("path"), item.Flag("isDirectory")),
                item["size"]?.GetValue<ulong>(), item["modifiedUtc"]?.GetValue<DateTimeOffset>(), item.Flag("hidden"))));
            offset = page["nextOffset"]?.GetValue<int>();
        } while (offset is not null);
        return (page, entries);
    }
    public string DisplayAddress(FileBrowserItem item) => item.Address;
    private static FileBrowserItem Folder(string path) => new(path.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault() ?? path, path, true);
}
