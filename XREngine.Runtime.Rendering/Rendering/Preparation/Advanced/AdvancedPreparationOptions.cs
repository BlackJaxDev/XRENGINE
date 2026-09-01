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
    private const int DefaultMaximumDrawCapacity = 65_536;

    public static AdvancedPreparationOptions Default => new(
        MaximumDraws: DefaultMaximumDrawCapacity,
        MaximumDeformationJobs: 4_096,
        MaximumDeformationFamilies: 16,
        // Every accepted draw can have a distinct range key. Keep the range
        // storage equal to the payload capacity so the default path remains
        // allocation-free for any valid frame, rather than a scene-specific
        // subset of it.
        MaximumIndirectRanges: DefaultMaximumDrawCapacity,
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
