namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral sampler addressing policy.
/// </summary>
public enum EAdvancedSamplerAddressMode : uint
{
    Repeat = 0,
    MirroredRepeat = 1,
    ClampToEdge = 2,
    ClampToBorder = 3,
}
