using System.Text.Json.Serialization;
using AvaMedia.Core.Compute;

namespace AvaMedia.Core;

public sealed partial class Job
{
    /// <summary>Attachment restored from the file-backed job log; no queue/multi-stage schema change.</summary>
    [JsonIgnore]
    public ComputeOutcome? ComputeOutcome => this.GetComputeOutcome();
}
