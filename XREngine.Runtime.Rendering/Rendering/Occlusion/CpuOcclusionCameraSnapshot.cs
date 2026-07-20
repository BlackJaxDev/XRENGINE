using System.Numerics;

namespace XREngine.Rendering.Occlusion;

/// <summary>
/// Camera state attached to an asynchronous occlusion query. Query results are
/// interpreted relative to this issuing pose rather than the most recent frame.
/// </summary>
internal readonly struct CpuOcclusionCameraSnapshot
{
    internal CpuOcclusionCameraSnapshot(
        Vector3 position,
        Vector3 forward,
        Vector3 up,
        Matrix4x4 projection,
        Matrix4x4 viewProjection,
        float nearZ)
    {
        Position = position;
        Forward = NormalizeOrFallback(forward, Vector3.UnitZ);
        Up = NormalizeOrFallback(up, Vector3.UnitY);
        Projection = projection;
        ViewProjection = viewProjection;
        NearZ = nearZ;
        IsValid = IsFinite(position) &&
            IsFinite(Forward) &&
            IsFinite(Up) &&
            IsFinite(projection) &&
            IsFinite(viewProjection) &&
            float.IsFinite(nearZ) &&
            nearZ > 0.0f;
    }

    internal Vector3 Position { get; }
    internal Vector3 Forward { get; }
    internal Vector3 Up { get; }
    internal Matrix4x4 Projection { get; }
    internal Matrix4x4 ViewProjection { get; }
    internal float NearZ { get; }
    internal bool IsValid { get; }

    internal static CpuOcclusionCameraSnapshot Capture(XRCamera camera)
        => new(
            camera.Transform.RenderTranslation,
            camera.Transform.RenderForward,
            camera.Transform.RenderUp,
            camera.ProjectionMatrixUnjittered,
            camera.ViewProjectionMatrixUnjittered,
            camera.NearZ);

    private static Vector3 NormalizeOrFallback(in Vector3 value, in Vector3 fallback)
        => value.LengthSquared() > 0.000001f ? Vector3.Normalize(value) : fallback;

    private static bool IsFinite(in Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(in Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
