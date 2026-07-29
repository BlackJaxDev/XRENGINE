namespace XREngine.Rendering;

/// <summary>
/// Global-illumination resource representation.
/// </summary>
public enum EAdvancedGiResourceType : uint
{
    None = 0,
    LightVolume = 1,
    RadianceCascade = 2,
    Surfel = 3,
    Voxel = 4,
    Reservoir = 5,
}
