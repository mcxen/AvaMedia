using System.Text.Json;

namespace AvaMedia.Core;

/// <summary>Auditable candidates and decisions, without provider credentials or model reasoning traces.</summary>
public sealed record VideoSummaryDiagnostic(string Stage, string Outcome, string Reason)
{
    public JsonElement? StructuredCandidate { get; init; }
    public string? ClaimText { get; init; }
    public string[] RawEvidenceIds { get; init; } = [];
    public string[] ExpandedEvidenceIds { get; init; } = [];
    /// <summary>Accepted review decisions, including cached reuse; not the count of unique final facts.</summary>
    public int AcceptedClaims { get; init; }
    /// <summary>Rejected candidate decisions, including cached reuse.</summary>
    public int RejectedClaims { get; init; }
}
