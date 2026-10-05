using System.Numerics;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>Preserves the current eye values until native submission accepts them.</summary>
internal readonly record struct TemporalHistoryEyeSnapshot(
    Vector2 Jitter,
    bool DepthZeroToOne,
    bool ReversedDepth,
    Matrix4x4 ViewMatrix,
    Matrix4x4 Projection,
    Matrix4x4 ViewProjection,
    Matrix4x4 ViewProjectionUnjittered,
    Matrix4x4 InverseViewProjection,
    Vector3 CameraPosition,
    Vector3 CameraForward);
