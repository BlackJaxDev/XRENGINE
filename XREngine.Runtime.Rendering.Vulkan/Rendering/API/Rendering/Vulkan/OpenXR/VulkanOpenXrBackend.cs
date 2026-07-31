using Silk.NET.Vulkan;
using System.Collections.Generic;
using System.Threading;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns Vulkan-specific OpenXR graphics resources, caches, transient execution
/// state, and diagnostics. Generic OpenXR session and pacing policy remain in
/// the runtime graphics binding.
/// </summary>
internal sealed class VulkanOpenXrBackend
{
    private const int EyeResourcePlannerStateCount = 2;
    private readonly ThreadLocal<VulkanOpenXrThreadExecutionState> _threadExecutionState =
        new(static () => new VulkanOpenXrThreadExecutionState(), trackAllValues: false);
    private object? _primaryCommandBufferVariants;
    private object? _resourcePlannerStates;

    internal readonly Dictionary<RenderResourceRegistry, VulkanOpenXrResourceRegistryWrapperRefreshStamp>
        ResourceRegistryWrapperRefreshStamps = new(ReferenceEqualityComparer.Instance);
    internal long RuntimeSessionStartDirtyWaitStartTimestamp;
    internal long RuntimeSessionStartPendingFrameWaitStartTimestamp;
    internal readonly Dictionary<ulong, VulkanOpenXrSwapchainImageViewCacheEntry> SwapchainImageViews = new();
    internal readonly object PrimaryCommandBufferVariantsLock = new();
    internal readonly CommandPool[] EyeCommandPools = new CommandPool[EyeResourcePlannerStateCount];
    internal readonly object EyeCommandPoolsLock = new();
    internal readonly VulkanOpenXrFrameDataRefreshRequestStorage[]
        EyeFrameDataRefreshRequests =
        [new(), new()];
    internal readonly List<VulkanImportedTexturePendingUpload>[] EyeRecordedTextureUploadsForSubmit = [new(), new()];
    internal readonly List<VulkanImportedTexturePendingUpload> RecordedTextureUploadsForSubmit = new();
    internal readonly VulkanOpenXrDepthTarget[] CachedDepthTargets = new VulkanOpenXrDepthTarget[EyeResourcePlannerStateCount];
    internal readonly Extent2D[] CachedDepthExtents = new Extent2D[EyeResourcePlannerStateCount];
    internal int ExternalSwapchainRenderDepth;
    internal BoundingRectangle ExternalSwapchainTargetRegion;
    internal int ExternalSwapchainPrewarmDepth;
    internal int SynchronousResourceUploadBlockDepth;
    internal readonly object ResourcePlannerStatesLock = new();
    internal IDisposable? EyeRecordWorkerScheduler;

    internal VulkanOpenXrThreadExecutionState CurrentThreadExecutionState =>
        _threadExecutionState.Value
        ?? throw new InvalidOperationException("The Vulkan OpenXR thread execution context is unavailable.");

    internal Dictionary<ulong, List<TVariant>> GetPrimaryCommandBufferVariants<TVariant>()
        where TVariant : class
        => (Dictionary<ulong, List<TVariant>>)(_primaryCommandBufferVariants ??=
            new Dictionary<ulong, List<TVariant>>());

    internal Dictionary<TKey, TState> GetResourcePlannerStates<TKey, TState>()
        where TKey : notnull
        => (Dictionary<TKey, TState>)(_resourcePlannerStates ??= new Dictionary<TKey, TState>());

    internal VulkanOpenXrDiagnosticsSnapshot CaptureDiagnostics<TVariant, TKey, TState>()
        where TVariant : class
        where TKey : notnull
    {
        int primaryVariantCount = 0;
        lock (PrimaryCommandBufferVariantsLock)
        {
            foreach (List<TVariant> variants in GetPrimaryCommandBufferVariants<TVariant>().Values)
                primaryVariantCount += variants.Count;
        }

        int plannerStateCount;
        lock (ResourcePlannerStatesLock)
            plannerStateCount = GetResourcePlannerStates<TKey, TState>().Count;

        return new VulkanOpenXrDiagnosticsSnapshot(
            SwapchainImageViews.Count,
            primaryVariantCount,
            plannerStateCount,
            Volatile.Read(ref ExternalSwapchainRenderDepth),
            Volatile.Read(ref SynchronousResourceUploadBlockDepth),
            Volatile.Read(ref ExternalSwapchainPrewarmDepth),
            Volatile.Read(ref RuntimeSessionStartDirtyWaitStartTimestamp),
            Volatile.Read(ref RuntimeSessionStartPendingFrameWaitStartTimestamp));
    }
}
