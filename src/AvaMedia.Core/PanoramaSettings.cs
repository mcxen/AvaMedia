using System.Text.Json.Serialization;

namespace AvaMedia.Core;

public enum PanoramaMode { Flat, HalfSphere, FullSphere }
public enum PanoramaLayout { Mono, SideBySide, TopBottom }
public enum PanoramaEye { Left, Right }
public enum PanoramaProjection { Equirectangular, Fisheye }

/// <summary>Camera angles and vertical field of view are in degrees.</summary>
public sealed record PanoramaSettings
{
    public PanoramaMode Mode { get; init; }
    public PanoramaLayout Layout { get; init; }
    public PanoramaEye Eye { get; init; }
    public PanoramaProjection Projection { get; init; }
    public double Yaw { get; init; }
    public double Pitch { get; init; }
    public double Roll { get; init; }
    public double FieldOfView { get; init; } = 90;
    [JsonIgnore] public bool IsImmersive => Mode != PanoramaMode.Flat;

    public PanoramaSettings Normalize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : PanoramaMode.Flat,
        Layout = Enum.IsDefined(Layout) ? Layout : PanoramaLayout.Mono,
        Eye = Enum.IsDefined(Eye) ? Eye : PanoramaEye.Left,
        Projection = Mode == PanoramaMode.HalfSphere && Enum.IsDefined(Projection) ? Projection : PanoramaProjection.Equirectangular,
        Yaw = Mode == PanoramaMode.HalfSphere ? Clamp(Yaw, -90, 90, 0) : Wrap(Yaw),
        Pitch = Clamp(Pitch, -89.9, 89.9, 0), Roll = Wrap(Roll),
        FieldOfView = Clamp(FieldOfView, 30, 120, 90)
    };

    public PanoramaSettings Recenter() => this with { Yaw = 0, Pitch = 0, Roll = 0, FieldOfView = 90 };
    private static double Clamp(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    private static double Wrap(double value) => double.IsFinite(value) ? ((value + 180) % 360 + 360) % 360 - 180 : 0;
}
