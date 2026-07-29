using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Fixed preparation capacities and whole-frame deformation budget.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedPreparationOptions(
    int MaximumDraws,
    int MaximumDeformationJobs,
    int MaximumDeformationFamilies,
    int MaximumIndirectRanges,
    int MaximumViews,
    AdvancedDeformedVertexArenaOptions DeformedArena,
    AdvancedDeformationBudget DeformationBudget,
    AdvancedFrameSlotUploadArenaOptions FrameUploadArena)
{
    public static AdvancedPreparationOptions Default => new(
        MaximumDraws: 65_536,
        MaximumDeformationJobs: 4_096,
        MaximumDeformationFamilies: 16,
        MaximumIndirectRanges: 64,
        // One desktop view plus the maximum OpenXR view set can coexist in
        // the same prepared world frame.
        MaximumViews: RenderFrameViewSet.MaxViewCount + 1,
        DeformedArena: new AdvancedDeformedVertexArenaOptions(
            InitialVertexCapacity: 65_536u,
            FrameSlotCount: 3,
            OwnerCapacity: 4_096,
            RetiredGenerationCapacity: 4),
        DeformationBudget: new AdvancedDeformationBudget(
            MaximumJobs: 4_096u,
            MaximumVertices: 4_194_304UL,
            MaximumOutputBytes: 256UL * 1024UL * 1024UL,
            OverflowBehavior:
                EAdvancedDeformationOverflowBehavior.KeepPreviousAndInvalidateVelocity),
        FrameUploadArena: AdvancedFrameSlotUploadArenaOptions.Default);
}
