namespace XREngine.Rendering.Vulkan;

/// <summary>Mutable storage accessed only through a command-runtime scope token.</summary>
internal sealed class VulkanCommandThreadContext
{
    internal VulkanCommandThreadContext(VulkanCommandRuntime owner) => Owner = owner;

    /// <summary>The sole command runtime allowed to install scoped state here.</summary>
    public VulkanCommandRuntime Owner { get; }
    public VulkanCommandRuntime? RenderStateOwner;
    public VulkanStateTracker? RenderState;
    public VulkanCommandRuntime? ResourcePlannerRuntimeStateOwner;
    public ResourcePlannerRuntimeState? ResourcePlannerRuntimeState;
    /// <summary>
    /// Immutable envelope paired with <see cref="ResourcePlannerRuntimeState"/>.
    /// Command consumers select this concrete value instead of exposing the
    /// thread workspace to resource-publication readers.
    /// </summary>
    public ResourcePlannerRuntimeGeneration? ResourcePlannerRuntimeGeneration;
    public VulkanCommandRuntime? FrameOpResourcePlannerSwitchingStateOwner;
    public FrameOpResourcePlannerSwitchingState? FrameOpResourcePlannerSwitchingState;
    public VulkanCommandRuntime? FramebufferBindingOwner;
    public XRFrameBuffer? BoundDrawFrameBuffer;
    public XRFrameBuffer? BoundReadFrameBuffer;
    public EReadBufferMode ReadBufferMode;
    public bool PreparedCommandChainEncodingActive;
    public VulkanFrameOpWorkspace? FrameOpWorkspace;
    public ulong ForwardLightingSnapshotFrame;
    public ForwardLightingBindingSnapshotCacheKey ForwardLightingSnapshotKey;
    public ComputeDispatchSnapshot? ForwardLightingSnapshot;
    public bool HasForwardLightingSnapshot;

    public void Reset()
    {
        RenderStateOwner = null;
        RenderState = null;
        ResourcePlannerRuntimeStateOwner = null;
        ResourcePlannerRuntimeState = null;
        ResourcePlannerRuntimeGeneration = null;
        FrameOpResourcePlannerSwitchingStateOwner = null;
        FrameOpResourcePlannerSwitchingState = null;
        FramebufferBindingOwner = null;
        BoundDrawFrameBuffer = null;
        BoundReadFrameBuffer = null;
        ReadBufferMode = default;
        PreparedCommandChainEncodingActive = false;
        ForwardLightingSnapshotFrame = 0;
        ForwardLightingSnapshot = null;
        HasForwardLightingSnapshot = false;
        FrameOpWorkspace?.Reset();
        FrameOpWorkspace = null;
    }
}
