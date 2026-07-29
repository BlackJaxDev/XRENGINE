namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral sampler filtering policy.
/// </summary>
public enum EAdvancedSamplerFilter : uint
{
    Nearest = 0,
    Linear = 1,
    Anisotropic = 2,
}
