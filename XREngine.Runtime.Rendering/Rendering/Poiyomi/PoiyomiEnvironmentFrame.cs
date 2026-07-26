using System.Numerics;

namespace XREngine.Rendering.Poiyomi;

/// <summary>
/// Native approximation inputs for LTCGI, light volumes, and blacklight.
/// The flags identify which explicitly configured inputs are valid.
/// </summary>
public readonly record struct PoiyomiEnvironmentFrame(
    Vector4 Diffuse,
    Vector4 Specular,
    Vector4 Blacklight,
    int Flags);
