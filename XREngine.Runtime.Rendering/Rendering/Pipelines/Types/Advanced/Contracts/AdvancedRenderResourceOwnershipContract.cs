using System.Collections.ObjectModel;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

/// <summary>
/// Canonical allocation, lifetime, and synchronization rules for advanced resources.
/// </summary>
public static class AdvancedRenderResourceOwnershipContract
{
    private static readonly AdvancedRenderResourceOwnershipDescriptor[] Definitions =
    [
        new(
            EAdvancedRenderResourceOwnership.PipelinePersistent,
            RenderResourceLifetime.Persistent,
            PipelineAllocates: true,
            PipelineDisposes: true,
            ReplicatedPerFrameSlot: false,
            RotatesHistory: false,
            RequiresExplicitBinding: false,
            RequiresOwnerSynchronization: false),
        new(
            EAdvancedRenderResourceOwnership.FrameSlotTransient,
            RenderResourceLifetime.Transient,
            PipelineAllocates: true,
            PipelineDisposes: true,
            ReplicatedPerFrameSlot: true,
            RotatesHistory: false,
            RequiresExplicitBinding: false,
            RequiresOwnerSynchronization: true),
        new(
            EAdvancedRenderResourceOwnership.TemporalHistory,
            RenderResourceLifetime.Persistent,
            PipelineAllocates: true,
            PipelineDisposes: true,
            ReplicatedPerFrameSlot: false,
            RotatesHistory: true,
            RequiresExplicitBinding: false,
            RequiresOwnerSynchronization: true),
        new(
            EAdvancedRenderResourceOwnership.Imported,
            RenderResourceLifetime.External,
            PipelineAllocates: false,
            PipelineDisposes: false,
            ReplicatedPerFrameSlot: false,
            RotatesHistory: false,
            RequiresExplicitBinding: true,
            RequiresOwnerSynchronization: true),
        new(
            EAdvancedRenderResourceOwnership.External,
            RenderResourceLifetime.External,
            PipelineAllocates: false,
            PipelineDisposes: false,
            ReplicatedPerFrameSlot: false,
            RotatesHistory: false,
            RequiresExplicitBinding: true,
            RequiresOwnerSynchronization: true),
    ];

    private static readonly ReadOnlyCollection<AdvancedRenderResourceOwnershipDescriptor> OrderedDefinitions =
        Array.AsReadOnly(Definitions);

    /// <summary>
    /// Ownership classes in stable enum order.
    /// </summary>
    public static IReadOnlyList<AdvancedRenderResourceOwnershipDescriptor> Ordered
        => OrderedDefinitions;

    /// <summary>
    /// Resolves one ownership descriptor without allocating.
    /// </summary>
    public static AdvancedRenderResourceOwnershipDescriptor Get(
        EAdvancedRenderResourceOwnership ownership)
    {
        int index = (int)ownership;
        if ((uint)index >= (uint)Definitions.Length ||
            Definitions[index].Ownership != ownership)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ownership),
                ownership,
                "Unknown advanced render-resource ownership class.");
        }

        return Definitions[index];
    }
}
