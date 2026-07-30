namespace XREngine.Rendering;

/// <summary>
/// GPU-table capacities used by the diagnostic bounds-checking decode mode.
/// </summary>
public readonly record struct AdvancedVisibilityValidationBounds(
    uint DrawCount,
    uint InstanceCount,
    uint GeometryCount,
    uint MaterialCount,
    uint TransformCount,
    uint EditorIdentityCount,
    uint ShadingKernelCount)
{
    public bool ContainsDraw(uint drawIndex)
        => drawIndex != 0u && drawIndex <= DrawCount;
}
