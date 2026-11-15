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
    /// <summary>
    /// Width of the internal render target of this view in pixels, when the view renders
    /// below its output size (render scale with an upscale, such as TSR). 0 means the view
    /// renders at <see cref="ViewportWidth"/>. Native stages map pixels of their internal
    /// targets to clip space with this size; <see cref="ViewportWidth"/> stays the output size.
    /// </summary>
    public int RenderWidth { get; init; }

    /// <summary>
    /// Height of the internal render target of this view in pixels; 0 means
    /// <see cref="ViewportHeight"/>. See <see cref="RenderWidth"/>.
    /// </summary>
    public int RenderHeight { get; init; }

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
    public ulong SourceCameraIdentity { get; init; }
    public uint CullingLayerMask { get; init; } = uint.MaxValue;
    public uint ViewMaskLo { get; init; }
    public uint ViewMaskHi { get; init; }
    public Vector4 FoveationCenterAndBias { get; init; }
    public Vector4 FoveationRadii { get; init; }
    /// <summary>Render time frozen with native view authoring.</summary>
    public float RenderTimeSeconds { get; init; }
}
