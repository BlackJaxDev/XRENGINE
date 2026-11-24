namespace XREngine.Rendering.Meshlets;

/// <summary>
/// The residency state of one registered geometry page in the <see cref="MeshletPageRegistry"/>.
/// </summary>
public enum EMeshletPageResidencyState : byte
{
    /// <summary>The page has no pool slot. Its page table entry reads <c>NotResident</c>.</summary>
    NotResident = 0,
    /// <summary>The page has a pool slot, and the next captured page table names it.</summary>
    Resident = 1,
    /// <summary>
    /// The page image is missing or not valid. The manager logs the page once and does not
    /// request it again while its geometry is registered. Its parent pages stay resident and draw.
    /// </summary>
    Failed = 2,
}
