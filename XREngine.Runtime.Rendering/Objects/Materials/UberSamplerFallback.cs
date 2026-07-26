using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral fallback texel for a semantic sampler role.
/// </summary>
public sealed record UberSamplerFallback(EUberSamplerRole Role, Vector4 Value, bool LinearData);
