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
    ulong ViewGeneration);
