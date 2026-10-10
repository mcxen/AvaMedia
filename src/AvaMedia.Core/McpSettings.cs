namespace AvaMedia.Core;

public sealed class McpSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 18920;
    public bool AllowLan { get; set; }
    public McpSettings Clone() => (McpSettings)MemberwiseClone();
    public void Validate()
    {
        if (Port is < 1024 or > 65535) throw new ArgumentException("MCP 端口须为 1024–65535。");
    }
}

public sealed partial class AppSettings
{
    public McpSettings Mcp { get; set; } = new();
}

public sealed partial class Job
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FileChangesCommitted { get; internal set; }
    public string SubmittedBy { get; set; } = "";
    public string SubmissionId { get; set; } = "";
    public string SubmissionHash { get; set; } = "";
}
