using System.Numerics;

namespace XREngine.Rendering.Commands;

/// <summary>
/// View data consumed by backend projections without making view changes
/// structural scene mutations.
/// </summary>
public readonly record struct BackendReadyCanonicalViewRecord(
    uint ViewId,
    Matrix4x4 View,
    Matrix4x4 Projection,
    int ViewportWidth,
    int ViewportHeight,
    ulong ViewGeneration)
{
    public Matrix4x4 ProjectionUnjittered { get; init; }
    public Matrix4x4 ViewProjectionJittered { get; init; }
    public Matrix4x4 ViewProjectionUnjittered { get; init; }
    public Matrix4x4 PreviousViewProjectionJittered { get; init; }
    public Matrix4x4 PreviousViewProjectionUnjittered { get; init; }
    public Vector4 FrustumPlane0 { get; init; }
    public Vector4 FrustumPlane1 { get; init; }
    public Vector4 FrustumPlane2 { get; init; }
    public Vector4 FrustumPlane3 { get; init; }
    public Vector4 FrustumPlane4 { get; init; }
    public Vector4 FrustumPlane5 { get; init; }
    public Vector4 CameraPositionAndNear { get; init; }
    public Vector4 CameraForwardAndFar { get; init; }
    public Vector4 CurrentAndPreviousJitter { get; init; }
    public Vector4 DepthParams { get; init; }
    public uint OutputLayer { get; init; }
    public EAdvancedViewRecordFlags Flags { get; init; }
    public ulong HistoryKey { get; init; }
    public uint ViewMaskLo { get; init; }
    public uint ViewMaskHi { get; init; }
}
