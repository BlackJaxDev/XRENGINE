using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan.RenderGraph;

internal readonly record struct VulkanPhysicalPlanningRequest(
    FrameOpContext Context,
    IReadOnlyCollection<RenderPassMetadata>? ActivePassMetadata,
    VulkanCompiledRenderGraph CompiledGraph,
    VulkanBarrierPlanner.QueueOwnershipConfig QueueOwnership,
    VulkanResourcePlanner PendingPlanner,
    VulkanResourceExtentContext ExtentContext,
    ulong PlannerSignature,
    ulong AllocationSignature,
    ResourcePlannerSignatureBreakdown SignatureBreakdown,
    VulkanBackendObjectContext BackendContext,
    VulkanResourceRuntime Resources,
    FrameOpResourcePlannerSwitchingState SwitchingState,
    VulkanAutoExposureHistoryCommandCapability Commands,
    bool SupportsTransformFeedback,
    bool IsDeviceLost,
    bool IsOpenXrOrVr,
    bool DeferReusedImageMetadataCommit);
