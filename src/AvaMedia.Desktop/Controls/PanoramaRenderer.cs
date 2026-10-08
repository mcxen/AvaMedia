using AvaMedia.Core;
using SkiaSharp;

namespace AvaMedia.Desktop.Controls;

/// <summary>Inverse spherical projection: one camera ray per displayed pixel.</summary>
internal static class PanoramaRenderer
{
    // The pinned native Skia build supports these runtime shaders on a GPU canvas.
    internal const string ShaderSource = """
        uniform shader frame;
        uniform float2 viewport;
        uniform float2 textureSize;
        uniform float4 eyeRect;
        uniform float2 yawCS;
        uniform float2 pitchCS;
        uniform float2 rollCS;
        uniform float tanHalfFov;
        uniform float halfSphere;
        uniform float fisheye;

        half4 main(float2 position) {
            float2 screen = (position - viewport * 0.5) / (viewport.y * 0.5);
            float3 ray = normalize(float3(screen.x * tanHalfFov, -screen.y * tanHalfFov, 1.0));
            float3 rolled = float3(rollCS.x * ray.x - rollCS.y * ray.y,
                                  rollCS.y * ray.x + rollCS.x * ray.y, ray.z);
            float3 pitched = float3(rolled.x, pitchCS.x * rolled.y + pitchCS.y * rolled.z,
                                   -pitchCS.y * rolled.y + pitchCS.x * rolled.z);
            float3 direction = float3(yawCS.x * pitched.x + yawCS.y * pitched.z, pitched.y,
                                     -yawCS.y * pitched.x + yawCS.x * pitched.z);
            float2 origin = eyeRect.xy * textureSize;
            float2 size = eyeRect.zw * textureSize;
            float2 pixel;
            if (fisheye > 0.5) {
                float angle = acos(clamp(direction.z, -1.0, 1.0));
                if (angle > 1.570796327) return half4(0.0, 0.0, 0.0, 1.0);
                float2 axis = float2(direction.x, -direction.y);
                float radius = angle / 1.570796327 * min(size.x, size.y) * 0.5;
                pixel = origin + size * 0.5 + axis / max(length(axis), 0.000001) * radius;
            } else {
                // The pinned SkSL runtime exposes unary atan; restore its quadrant explicitly.
                float longitude = atan(abs(direction.x) / max(abs(direction.z), 0.0000001));
                if (direction.z < 0.0) longitude = 3.141592654 - longitude;
                if (direction.x < 0.0) longitude = -longitude;
                if (halfSphere > 0.5 && abs(longitude) > 1.570796327)
                    return half4(0.0, 0.0, 0.0, 1.0);
                float u = longitude / (halfSphere > 0.5 ? 3.141592654 : 6.283185307) + 0.5;
                if (halfSphere < 0.5) u = fract(u);
                float v = 0.5 - asin(clamp(direction.y, -1.0, 1.0)) / 3.141592654;
                pixel = origin + float2(u, v) * size;
            }
            // Clamp inside the selected eye, so filtering cannot bleed into the other eye.
            pixel = clamp(pixel, origin + 0.5, origin + size - 0.5);
            return sample(frame, pixel);
        }
        """;

    private static readonly Lazy<SKRuntimeEffect> Effect = new(() =>
        SKRuntimeEffect.Create(ShaderSource, out var error) ?? throw new InvalidOperationException("VR shader: " + error));

    public static void Initialize() => _ = Effect.Value;

    public static SKShader CreateShader(SKImage image, PanoramaSettings settings, float width, float height)
    {
        var effect = Effect.Value;
        static float[] Rotation(double degrees)
        { var radians = degrees * Math.PI / 180; return [(float)Math.Cos(radians), (float)Math.Sin(radians)]; }
        var second = settings.Eye == PanoramaEye.Right;
        float[] eye = settings.Layout switch
        {
            PanoramaLayout.SideBySide => [second ? .5f : 0, 0, .5f, 1],
            PanoramaLayout.TopBottom => [0, second ? .5f : 0, 1, .5f],
            _ => [0, 0, 1, 1]
        };
        var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["viewport"] = new[] { width, height }, ["textureSize"] = new[] { (float)image.Width, (float)image.Height },
            ["eyeRect"] = eye, ["yawCS"] = Rotation(settings.Yaw), ["pitchCS"] = Rotation(settings.Pitch),
            ["rollCS"] = Rotation(settings.Roll), ["tanHalfFov"] = (float)Math.Tan(settings.FieldOfView * Math.PI / 360),
            ["halfSphere"] = settings.Mode == PanoramaMode.HalfSphere ? 1f : 0f,
            ["fisheye"] = settings.Projection == PanoramaProjection.Fisheye ? 1f : 0f
        };
        using var source = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        var children = new SKRuntimeEffectChildren(effect) { ["frame"] = source };
        return effect.ToShader(true, uniforms, children) ?? throw new InvalidOperationException("VR shader creation failed.");
    }
}
