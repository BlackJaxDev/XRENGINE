using System.Numerics;

namespace XREngine.Rendering.Poiyomi;

/// <summary>
/// Scalar state accompanying the stable AudioLink GPU texture.
/// </summary>
public readonly record struct PoiyomiAudioLinkFrame(
    Vector4 TextureSize,
    Vector4 Time,
    Vector4 History);
