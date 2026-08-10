namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Publishes the active immutable resource-planner allocation snapshot for backend
/// wrappers.  It exposes physical groups only; planner mutation remains render-graph
/// authority work.
/// </summary>
internal sealed class VulkanResourcePlannerService
{
    private VulkanResourceAllocator? _allocator;
    private RenderGraph.VulkanFramePlanner? _framePlanner;
    private VulkanCommandThreadWorkspace<
        VulkanStateTracker,
        ResourcePlannerRuntimeState,
        FrameOpResourcePlannerSwitchingState,
        XRFrameBuffer,
        EReadBufferMode>? _commandWorkspace;

    internal void BindCommandRuntime(VulkanCommandRuntime commandRuntime)
    {
        ArgumentNullException.ThrowIfNull(commandRuntime);
        var workspace = commandRuntime.GetThreadWorkspace<
            VulkanStateTracker,
            ResourcePlannerRuntimeState,
            FrameOpResourcePlannerSwitchingState,
            XRFrameBuffer,
            EReadBufferMode>();
        var current = Interlocked.CompareExchange(
            ref _commandWorkspace,
            workspace,
            comparand: null);
        if (current is not null && !ReferenceEquals(current, workspace))
        {
            throw new InvalidOperationException(
                "The Vulkan resource planner service already owns a different command workspace.");
        }
    }

    internal void Publish(VulkanResourceAllocator allocator)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        Volatile.Write(ref _allocator, allocator);
    }

    internal void BindFramePlanner(RenderGraph.VulkanFramePlanner framePlanner)
    {
        ArgumentNullException.ThrowIfNull(framePlanner);
        RenderGraph.VulkanFramePlanner? current = Interlocked.CompareExchange(
            ref _framePlanner,
            framePlanner,
            comparand: null);
        if (current is not null && !ReferenceEquals(current, framePlanner))
            throw new InvalidOperationException("The Vulkan resource planner service already owns a different frame planner.");
    }

    internal bool TryGetPhysicalImageGroup(string resourceName, out VulkanPhysicalImageGroup? group)
    {
        VulkanResourceAllocator? allocator = ResolveActiveAllocator();
        if (allocator is not null)
            return allocator.TryGetPhysicalGroupForResource(resourceName, out group);

        group = null;
        return false;
    }

    private VulkanResourceAllocator? ResolveActiveAllocator()
    {
        var workspace = Volatile.Read(ref _commandWorkspace);
        ResourcePlannerRuntimeState? scopedState = workspace?.Current.ResourcePlannerRuntimeState;
        return scopedState.HasValue
            ? scopedState.Value.ResourceAllocator
            : Volatile.Read(ref _allocator);
    }

    /// <summary>Publishes a named buffer wrapper for render-graph resolution.</summary>
    internal void TrackBufferBinding(XRDataBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        string name = string.IsNullOrWhiteSpace(buffer.AttributeName)
            ? buffer.Name ?? string.Empty
            : buffer.AttributeName;
        if (!string.IsNullOrWhiteSpace(name))
            Volatile.Read(ref _framePlanner)?.TrackedBuffersByName[name] = buffer;
    }
}
