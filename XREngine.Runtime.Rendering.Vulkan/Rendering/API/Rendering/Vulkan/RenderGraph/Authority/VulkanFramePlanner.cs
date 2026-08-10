using System.Threading;
using System.Collections.Concurrent;
using XREngine.Rendering.Resources;
using XREngine.Rendering.RenderGraph;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Owns renderer-local frame-planning workspaces and publishes sealed render-graph plans.
/// This authority deliberately has no dependency on the renderer facade.
/// </summary>
internal sealed partial class VulkanFramePlanner
{
    private const int MaxInteractiveResizeExtentSnapshots = 32;
    private long _publishedBarrierGeneration;
    private long _frameContextId;
    private int _frozenPlanReaders;
    private ResourcePlannerRuntimeGeneration? _publishedResourcePlannerGeneration;
    internal static bool ReportedNativeNegativeOneToOneDepth;
    internal static bool ReportedShaderRemappedNegativeOneToOneDepth;
    internal static ConcurrentDictionary<IReadOnlyCollection<RenderPassMetadata>, RenderPassMetadataSignatureCacheEntry>
        PassMetadataSignatureCache { get; } = new(ReferenceEqualityComparer.Instance);

    public VulkanFramePlanner()
        => ResourcePublications = new VulkanResourcePlannerPublicationReader(this);

    public VulkanInteractiveResizePlannerExtentCache InteractiveResizeExtentCache { get; } =
        new(MaxInteractiveResizeExtentSnapshots);
    public ulong FrozenResourcePlanRevision { get; private set; }
    /// <summary>Reads immutable planner-generation publications for backend wrappers.</summary>
    internal VulkanResourcePlannerPublicationReader ResourcePublications { get; }
    public Extent2D DesktopSwapchainExtent { get; private set; }

    /// <summary>Publishes the newly committed desktop output extent to planning consumers.</summary>
    public void PublishDesktopSwapchainExtent(Extent2D extent)
        => DesktopSwapchainExtent = extent;
    public Dictionary<string, XRDataBuffer> TrackedBuffersByName { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public object PlannerReadbackGate { get; } = new();
    public VulkanRenderGraphCompiler Compiler { get; } = new();
    public VulkanFrameOperationScheduler FrameScheduler { get; } = new();
    public VulkanFrameOperationQueue Operations { get; } = new();
    public FramePlanBuilder FramePlanBuilder { get; } = new();
    public VulkanFramePlannerMutableState<
        VulkanFrameOpPlannerStateKey,
        FrameOpResourcePlannerSwitchingState,
        VulkanQueueOwnershipConfigCacheEntry,
        MergedFrameOpRegistryCacheEntry,
        FrameOpRegistryCacheSource,
        ActivePassMetadataFilterCacheEntry> MutableState { get; } =
        new(VulkanFrameOpPlannerStateKeyComparer.Instance, partitionCapacity: 12);
    public VulkanRenderGraphPlan CurrentPlan { get; private set; } = VulkanRenderGraphPlan.Empty;
    public EVulkanQueueOverlapMode AutoQueueOverlapMode = EVulkanQueueOverlapMode.GraphicsOnly;
    public EVulkanQueueOverlapMode LastResolvedQueueOverlapMode = EVulkanQueueOverlapMode.GraphicsOnly;
    public int QueueOverlapPromotionStabilityFrames;
    public int QueueOverlapFramesInMode;
    public long LastQueueOverlapSampleTimestamp;
    public ulong LastQueueOverlapSampleFrameId = ulong.MaxValue;
    public ulong LastQueueOverlapPolicyFrameId = ulong.MaxValue;
    public double QueueOverlapFrameDeltaEmaMilliseconds = -1.0;
    public double QueueOverlapModeStartFrameDeltaMilliseconds = -1.0;
    public ulong QueueOwnershipConfigCacheFrameId = ulong.MaxValue;
    public ulong LastResourcePlanReplacementRevision;
    public ulong LastResourcePlanReplacementSignature;
    public ulong LastResourcePlanReplacementAllocationSignature;
    public int LastResourcePlanReplacementRetiredImageCount;
    public int LastResourcePlanReplacementRetiredBufferCount;

    public ulong NextFrameContextId()
        => unchecked((ulong)Interlocked.Increment(ref _frameContextId));

    public bool IsResourcePlanFrozen => Volatile.Read(ref _frozenPlanReaders) > 0;

    public void AddFrozenPlanReader(ulong resourcePlanRevision)
    {
        FrozenResourcePlanRevision = resourcePlanRevision;
        Interlocked.Increment(ref _frozenPlanReaders);
    }

    public void RemoveFrozenPlanReader()
    {
        if (Interlocked.Decrement(ref _frozenPlanReaders) == 0)
            FrozenResourcePlanRevision = 0;
    }

    /// <summary>Reserves a unique generation for one immutable barrier publication.</summary>
    public ulong NextBarrierPlanGeneration()
        => unchecked((ulong)Interlocked.Increment(ref _publishedBarrierGeneration));

    /// <summary>
    /// Publishes a plan already frozen against the resource-planner generation
    /// that owns it. Native recording must never reconstruct this association
    /// from whichever context happened to publish most recently.
    /// </summary>
    public void PublishPlan(VulkanRenderGraphPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        CurrentPlan = plan;
    }

    public VulkanFramePlanningSnapshot CaptureSnapshot()
        => new(CurrentPlan, FrozenResourcePlanRevision, IsResourcePlanFrozen);

    public ResourcePlannerRuntimeGeneration GetPublishedResourcePlannerGeneration()
        => Volatile.Read(ref _publishedResourcePlannerGeneration)
            ?? throw new InvalidOperationException("The Vulkan resource-planner runtime generation has not been initialized.");

    public void PublishResourcePlannerGeneration(ResourcePlannerRuntimeGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        Volatile.Write(ref _publishedResourcePlannerGeneration, generation);
    }

    public void ReleaseCaches()
    {
        Compiler.ReleaseCaches();
        FrameScheduler.ReleaseCaches();
    }
}

/// <summary>Immutable planning values safe to pass from planning into command scheduling.</summary>
internal readonly record struct VulkanFramePlanningSnapshot(
    VulkanRenderGraphPlan RenderGraphPlan,
    ulong FrozenResourcePlanRevision,
    bool IsResourcePlanFrozen);
