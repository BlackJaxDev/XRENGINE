namespace XREngine.Components;

/// <summary>
/// Identifies a contiguous range in a template's depth-ordered particle index
/// stream. Particles in one range may execute in parallel after the previous
/// depth has completed.
/// </summary>
public readonly record struct PhysicsChainDepthRange(
    int TreeIndex,
    int Depth,
    int IndexStart,
    int IndexCount);
