namespace XREngine.Rendering;

/// <summary>
/// Aggregate deformation shader specialization axes.
/// </summary>
[Flags]
public enum EAdvancedDeformationFeatureFlags : uint
{
    None = 0u,
    Skinning = 1u << 0,
    Blendshapes = 1u << 1,
    Normals = 1u << 2,
    Tangents = 1u << 3,
    SpillInfluences = 1u << 4,
    Meshlets = 1u << 5,
    Velocity = 1u << 6,
    MaximumBlendshapeAccumulation = 1u << 7,
    VelocityInvalid = 1u << 8,
    /// <summary>
    /// Palette rows already contain inverse-bind composition. Applying a
    /// second inverse-bind transform would corrupt the pose.
    /// </summary>
    PrecomposedPalette = 1u << 9,
}
