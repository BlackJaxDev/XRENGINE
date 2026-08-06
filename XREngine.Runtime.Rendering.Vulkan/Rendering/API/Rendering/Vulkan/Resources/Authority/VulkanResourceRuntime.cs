using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Aggregates the mutable resource services for one logical-device lifetime.
/// </summary>
/// <remarks>
/// This type deliberately has no renderer reference. Native Vulkan calls, command recording,
/// and shutdown ordering remain renderer concerns; this object only establishes the single
/// ownership boundary for the state those operations mutate.
/// </remarks>
internal sealed class VulkanResourceRuntime
{
    internal VulkanResourceRuntime(int frameSlotCount)
    {
        BackendObjects = new VulkanBackendObjectRegistry();
        Descriptors = new VulkanDescriptorManager();
        Allocations = new VulkanAllocationAuthority(
            new VulkanBufferResourceManager(),
            new VulkanImageAllocationTracker(),
            new VulkanStagingManager());
        Uploads = new VulkanTextureUploadService();
        Queries = new VulkanQueryAuthority();
        FallbackTexture = new VulkanFallbackTextureState();
        Lifetime = new VulkanLifetimeAuthority(
            new VulkanResourceLifetimeTracker(),
            new VulkanResourceRetirementQueue(frameSlotCount));
    }

    internal VulkanBackendObjectRegistry BackendObjects { get; }
    internal VulkanDescriptorManager Descriptors { get; }
    internal VulkanAllocationAuthority Allocations { get; }
    internal VulkanTextureUploadService Uploads { get; }
    internal VulkanQueryAuthority Queries { get; }
    internal VulkanFallbackTextureState FallbackTexture { get; }
    internal VulkanLifetimeAuthority Lifetime { get; }
    internal VulkanPipelineManager PipelineManager { get; } = new();
    internal VulkanBackendObjectContext? BackendObjectContext;
    internal RenderPass SwapchainRenderPass;
    internal RenderPass SwapchainLoadRenderPass;
    internal Dictionary<ulong, uint> RenderPassColorAttachmentCounts { get; } = new();
    internal Dictionary<ulong, Format[]> RenderPassColorAttachmentFormats { get; } = new();
    internal Dictionary<ulong, string> RenderPassSemanticSignatures { get; } = new();
    internal Dictionary<Format, bool> FormatColorBlendSupport { get; } = new();
    internal bool? SupportsGpuAutoExposure;
    internal bool AutoExposureComputeInitialized;
    internal XRRenderProgram? AutoExposureComputeProgram2D;
    internal XRRenderProgram? AutoExposureComputeProgram2DArray;
    internal object TextureUploadContextSync { get; } = new();
    internal Dictionary<VulkanFrameBufferRenderPassKey, Silk.NET.Vulkan.RenderPass> FrameBufferRenderPasses { get; } = new();
    internal VulkanPhysicalImageGroup? RetainedAutoExposureHistoryGroup;

    /// <summary>
    /// Mapped frame storage is created only after device and frame-slot setup. Replacing it is
    /// intentionally explicit so an old generation cannot be silently retargeted.
    /// </summary>
    internal VulkanMappedFrameArena? MappedFrameArena { get; private set; }

    internal void PublishMappedFrameArena(VulkanMappedFrameArena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (MappedFrameArena is not null)
            throw new InvalidOperationException("A mapped frame arena is already published.");

        MappedFrameArena = arena;
    }

    internal VulkanMappedFrameArena? DetachMappedFrameArena()
    {
        VulkanMappedFrameArena? arena = MappedFrameArena;
        MappedFrameArena = null;
        return arena;
    }
}
