using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>One exact publication lowered once within a frame slot.</summary>
internal struct VulkanAdvancedScenePublicationEntry
{
    internal AdvancedSharedGpuSceneDatabase? Database;
    internal AdvancedGpuScenePublicationReference Publication;
    internal VulkanAdvancedScenePublicationState State;
    internal int ActiveUseCount;

    internal void Clear()
    {
        Database = null;
        Publication = default;
        State = default;
        ActiveUseCount = 0;
    }
}
