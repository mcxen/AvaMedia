using System.Text.Json.Serialization;

namespace AvaMedia.Core;

public enum AiActivityState { Running, Completed, Cancelled, Failed }

/// <summary>Transient, bounded observations of actual work; never serialized into the queue.</summary>
public sealed record AiActivity(string Stage, string Model, DateTime StartedUtc, DateTime UpdatedUtc)
{
    /// <summary>Human-readable progress time; editing and subtitle boundaries retain their own precision.</summary>
    public static string FormatElapsed(double seconds)
    {
        var whole = (long)Math.Clamp(double.IsFinite(seconds) ? Math.Floor(seconds) : 0, 0, long.MaxValue / 2);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{whole / 3600:00}:{whole / 60 % 60:00}:{whole % 60:00}");
    }
    public string Backend { get; init; } = "";
    public string Detail { get; init; } = "";
    public double? Current { get; init; }
    public double? Total { get; init; }
    public string Unit { get; init; } = "";
    public int ResultCount { get; init; }
    public string ResultLabel { get; init; } = "条";
    public string[] RecentResults { get; init; } = [];
    public string[] RecentStages { get; init; } = [];
    public byte[]? Preview { get; init; }
    public string PreviewCaption { get; init; } = "";
    public AiActivityState State { get; init; }
}

/// <summary>Serializes native callbacks and bounds retained text and frames.</summary>
public sealed class AiActivityReporter(Action<AiActivity> report, string model, string resultLabel = "条")
{
    private readonly object _gate = new();
    private AiActivity _value = new("准备", model, DateTime.UtcNow, DateTime.UtcNow) { ResultLabel = resultLabel };
    private void Publish(Func<AiActivity, AiActivity> update)
    {
        lock (_gate)
        {
            _value = update(_value) with { UpdatedUtc = DateTime.UtcNow };
            report(_value);
        }
    }
    public void Stage(string stage, double? current = null, double? total = null, string unit = "", string detail = "") =>
        Publish(value => value with
        {
            Stage = stage, Current = current, Total = total, Unit = unit, Detail = detail,
            RecentStages = value.Stage == stage ? value.RecentStages : value.RecentStages.TakeLast(11)
                .Append($"{AiActivity.FormatElapsed((DateTime.UtcNow - value.StartedUtc).TotalSeconds)} · {stage}").ToArray()
        });
    public void Advance(double current, double total, string unit, string? detail = null) =>
        Publish(value => value with { Current = current, Total = total, Unit = unit, Detail = detail ?? value.Detail });
    public void Backend(string backend) => Publish(value => value with { Backend = backend });
    public void Frame(byte[] png, string caption) => Publish(value => value with { Preview = png, PreviewCaption = caption });
    public void Result(string text, int? count = null) => Publish(value => value with
    {
        ResultCount = count ?? value.ResultCount + 1,
        RecentResults = value.RecentResults.TakeLast(29).Append(text.Length > 800 ? text[..800] : text).ToArray()
    });
    public void Finish(string stage)
    {
        Publish(value => value with
        {
            Stage = stage, State = AiActivityState.Completed, Current = null, Total = null, Unit = "",
            RecentStages = value.Stage == stage ? value.RecentStages : value.RecentStages.TakeLast(11)
                .Append($"{AiActivity.FormatElapsed((DateTime.UtcNow - value.StartedUtc).TotalSeconds)} · {stage}").ToArray()
        });
    }
}

public sealed partial class Job
{
    private AiActivity? _activity;
    [JsonIgnore]
    public AiActivity? Activity
    {
        get => Volatile.Read(ref _activity);
        set { Volatile.Write(ref _activity, value); Raise(); }
    }
}
