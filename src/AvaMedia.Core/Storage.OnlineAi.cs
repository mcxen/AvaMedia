using System.Text.Json;

namespace AvaMedia.Core;

public sealed partial class Storage
{
    private sealed record OnlineAiCredential(string Id, string Endpoint, string Key);
    private const string OnlineAiKeyFile = "online-ai-key.json";

    private void LoadOnlineAiKey(OnlineAiSettings settings)
    {
        var credentials = Read<List<OnlineAiCredential>>(OnlineAiKeyFile) ?? [];
        foreach (var provider in settings.Providers)
            if (credentials.Find(c => c.Id == provider.Id && c.Endpoint == provider.Endpoint) is { } credential)
                provider.ApiKey = credential.Key;
    }

    private void SaveOnlineAiKey(OnlineAiSettings settings)
    {
        var credentials = settings.Providers.Where(p => p.ApiKey.Length != 0)
            .Select(p => new OnlineAiCredential(p.Id, p.Endpoint, p.ApiKey)).ToArray();
        var path = Path.Combine(_root, OnlineAiKeyFile);
        if (credentials.Length == 0) { if (File.Exists(path)) File.Delete(path); return; }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var streamOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, streamOptions)) JsonSerializer.Serialize(stream, credentials);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
