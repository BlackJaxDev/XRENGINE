using Silk.NET.Vulkan;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>Preallocated mutable state for one in-flight Vulkan frame slot.</summary>
internal sealed class VulkanAdvancedSceneResourceSlot
{
    internal VulkanAdvancedSceneResourceSlot(
        int publicationCapacity,
        int receiptCapacity)
    {
        Entries = new VulkanAdvancedScenePublicationEntry[publicationCapacity];
        GlobalDescriptorSets = new DescriptorSet[publicationCapacity];
        ReceiptStates = new VulkanAdvancedScenePublicationUseState[receiptCapacity];
        for (int index = 0; index < ReceiptStates.Length; ++index)
            ReceiptStates[index] = new VulkanAdvancedScenePublicationUseState();
    }

    internal VulkanAdvancedScenePublicationEntry[] Entries { get; }
    internal DescriptorSet[] GlobalDescriptorSets { get; }
    internal VulkanAdvancedScenePublicationUseState[] ReceiptStates { get; }
    internal VulkanAdvancedScenePublicationAllocationPlan AllocationPlan { get; } = new();
    // These regions survive a completed slot reset.  A later generation may
    // patch their exact publication deltas in place only before it creates its
    // first immutable entry; later same-generation publications use COW.
    internal VulkanAdvancedSceneResidentTable<AdvancedDrawRecord> ResidentDraws { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedInstanceRecord> ResidentInstances { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedTransformRecord> ResidentTransforms { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedGeometryRecord> ResidentGeometry { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedDeformationRecord> ResidentDeformations { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedRenderStateRecord> ResidentRenderStates { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedEditorIdentityRecord> ResidentEditorIdentities { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedMaterialRecord> ResidentMaterials { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedShadingKernelRecord> ResidentKernels { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedMaterialLayoutRecord> ResidentLayouts { get; } = new();
    internal VulkanAdvancedSceneResidentBytes ResidentStaticVertices { get; } = new();
    internal VulkanAdvancedSceneResidentBytes ResidentIndices { get; } = new();
    internal VulkanAdvancedSceneResidentBytes ResidentPreSkinnedCurrent { get; } = new();
    internal VulkanAdvancedSceneResidentBytes ResidentPreSkinnedPrevious { get; } = new();
    internal VulkanAdvancedSceneResidentBytes ResidentMeshletDescriptors { get; } = new();
    internal VulkanAdvancedSceneResidentBytes ResidentMeshletVertexIndices { get; } = new();
    internal VulkanAdvancedSceneResidentBytes ResidentMeshletTriangleWords { get; } = new();
    internal VulkanAdvancedSceneResidentBytes ResidentMaterialConstants { get; } = new();
    internal VulkanAdvancedSceneResidentBytes ResidentMaterialBindings { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedTextureRecord> ResidentTextures { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedSamplerRecord> ResidentSamplers { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedLightRecord> ResidentLights { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedShadowRecord> ResidentShadows { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedProbeRecord> ResidentProbes { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedEnvironmentRecord> ResidentEnvironments { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedDecalRecord> ResidentDecals { get; } = new();
    internal VulkanAdvancedSceneResidentTable<AdvancedGiResourceRecord> ResidentGiResources { get; } = new();
    internal VulkanAdvancedSceneResidentLookups ResidentLookups { get; } = new();

    internal void ClearResidentMirrors()
    {
        ResidentDraws.Clear(); ResidentInstances.Clear(); ResidentTransforms.Clear(); ResidentGeometry.Clear();
        ResidentDeformations.Clear(); ResidentRenderStates.Clear(); ResidentEditorIdentities.Clear();
        ResidentMaterials.Clear(); ResidentKernels.Clear(); ResidentLayouts.Clear();
        ResidentTextures.Clear(); ResidentSamplers.Clear(); ResidentLights.Clear(); ResidentShadows.Clear();
        ResidentProbes.Clear(); ResidentEnvironments.Clear(); ResidentDecals.Clear(); ResidentGiResources.Clear();
        ResidentStaticVertices.Clear(); ResidentIndices.Clear(); ResidentPreSkinnedCurrent.Clear();
        ResidentPreSkinnedPrevious.Clear(); ResidentMeshletDescriptors.Clear();
        ResidentMeshletVertexIndices.Clear(); ResidentMeshletTriangleWords.Clear();
        ResidentMaterialConstants.Clear(); ResidentMaterialBindings.Clear();
        ResidentLookups.Clear();
    }
    internal DescriptorSet ResourceDescriptorSet;
    internal ulong FrameGeneration;
    internal uint NextTextureDescriptor = 1u;
    internal uint NextSamplerDescriptor = 1u;
    internal int EntryCount;
    internal int ReceiptCount;
    internal int ActiveUseCount;
    internal ulong StorageBytesConsumed;
    internal bool Quarantined;
    internal bool TransactionIntegrityFault;

    internal int Find(
        AdvancedSharedGpuSceneDatabase database,
        in AdvancedGpuScenePublicationReference publication)
    {
        for (int index = 0; index < EntryCount; ++index)
            if (ReferenceEquals(Entries[index].Database, database) &&
                Entries[index].Publication == publication)
            {
                return index;
            }

        return -1;
    }

    internal void BeginGeneration(ulong generation)
    {
        if (ActiveUseCount != 0)
            throw new InvalidOperationException(
                "A Vulkan advanced-scene frame slot cannot be recycled while native publication uses remain active.");

        for (int index = 0; index < EntryCount; ++index)
            Entries[index].Clear();

        FrameGeneration = generation;
        NextTextureDescriptor = 1u;
        NextSamplerDescriptor = 1u;
        EntryCount = 0;
        ReceiptCount = 0;
        StorageBytesConsumed = 0u;
        Quarantined = false;
        TransactionIntegrityFault = false;
    }
}
