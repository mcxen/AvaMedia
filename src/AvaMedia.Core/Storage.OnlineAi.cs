using System.Text.Json;

namespace AvaMedia.Core;

public sealed partial class Storage
{
    private sealed record OnlineAiCredential(string Endpoint, string Key);
    private const string OnlineAiKeyFile = "online-ai-key.json";

    private void LoadOnlineAiKey(OnlineAiOptions options)
    {
        var credential = Read<OnlineAiCredential>(OnlineAiKeyFile);
        if (credential is not null && credential.Endpoint == options.Endpoint) options.ApiKey = credential.Key;
    }

    private void SaveOnlineAiKey(OnlineAiOptions options)
    {
        var path = Path.Combine(_root, OnlineAiKeyFile);
        if (options.ApiKey.Length == 0) { if (File.Exists(path)) File.Delete(path); return; }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, settings))
                JsonSerializer.Serialize(stream, new OnlineAiCredential(options.Endpoint, options.ApiKey));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
