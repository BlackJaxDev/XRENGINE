using System.Numerics;

namespace XREngine.Rendering.Materials;

/// <summary>
/// Scalar state accompanying the stable AudioLink GPU texture.
/// </summary>
public readonly record struct AudioLinkFrame(
    Vector4 TextureSize,
    Vector4 Time,
    Vector4 History);
