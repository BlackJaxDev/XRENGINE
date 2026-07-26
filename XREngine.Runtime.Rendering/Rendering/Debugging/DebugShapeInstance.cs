using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Transforms.Rotations;

namespace XREngine.Rendering.Debugging;

/// <summary>
/// Compact shape submission carried from component callbacks to the shared debug batch.
/// </summary>
internal readonly record struct DebugShapeInstance(
    EDebugShapeInstanceKind Kind,
    Matrix4x4 Transform,
    Vector3 Position,
    Vector3 Axis,
    Rotator Rotation,
    Vector2 Extents,
    float Radius,
    float Height,
    bool Solid,
    bool DepthTested,
    ColorF4 Color);
