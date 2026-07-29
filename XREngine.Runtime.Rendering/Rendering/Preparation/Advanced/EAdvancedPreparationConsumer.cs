namespace XREngine.Rendering;

/// <summary>
/// Every geometry consumer of aggregate deformation output.
/// </summary>
[Flags]
public enum EAdvancedPreparationConsumer : uint
{
    None = 0u,
    Visibility = 1u << 0,
    Depth = 1u << 1,
    Velocity = 1u << 2,
    MaterialReconstruction = 1u << 3,
    DirectionalShadow = 1u << 4,
    PointShadow = 1u << 5,
    SpotShadow = 1u << 6,
    Probe = 1u << 7,
    Capture = 1u << 8,
}
