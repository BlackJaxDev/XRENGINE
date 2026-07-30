namespace XREngine.Rendering;

/// <summary>
/// Audited maximum logical identifier ranges for the first production visibility format.
/// These are capacity limits, not same-frame GPU counts.
/// </summary>
public readonly record struct AdvancedVisibilityCapacityInventory(
    uint Draws,
    uint Instances,
    uint PrimitivesPerDraw,
    uint Meshlets,
    uint Materials,
    uint Views,
    uint EditorIds)
{
    public static AdvancedVisibilityCapacityInventory TargetScenes => new(
        Draws: 65_536u,
        Instances: 65_536u,
        PrimitivesPerDraw: AdvancedVisibilityBufferContract.MaximumEncodableIndex,
        Meshlets: 16_777_216u,
        Materials: 65_536u,
        Views: RenderFrameViewSet.MaxViewCount + 1u,
        EditorIds: AdvancedVisibilityBufferContract.MaximumEncodableIndex);

    public bool FitsVersion1
        => Draws <= AdvancedVisibilityBufferContract.MaximumEncodableIndex &&
           Instances <= AdvancedVisibilityBufferContract.MaximumEncodableIndex &&
           PrimitivesPerDraw <= AdvancedVisibilityBufferContract.MaximumEncodableIndex &&
           Meshlets <= AdvancedVisibilityPrimitiveIdentity.MaximumMeshletIndex + 1u &&
           Materials <= AdvancedVisibilityBufferContract.MaximumEncodableIndex &&
           Views <= AdvancedVisibilityMetadataWord.MaximumViewCount &&
           EditorIds <= AdvancedVisibilityBufferContract.MaximumEncodableIndex;
}
