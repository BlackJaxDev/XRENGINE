using XREngine.Rendering.Resources;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>
/// Owns mutable registry, switching, and pass-filter planning caches for one
/// planner-key domain. The renderer supplies only domain types and comparers.
/// </summary>
internal sealed class VulkanFramePlannerMutableState<TKey, TSwitchingState, TQueueCacheEntry, TMergedRegistryEntry, TRegistryCacheSource, TActiveFilterEntry>
    where TKey : notnull
    where TSwitchingState : class, new()
{
    public TSwitchingState DefaultSwitchingState { get; } = new();
    public List<TQueueCacheEntry> QueueOwnershipCache { get; } = [];
    public List<TMergedRegistryEntry> MergedRegistryCache { get; } = [];
    public List<TKey> PlannerStateEvictionScratch { get; } = [];
    public List<RenderResourceRegistry> RegistryScratch { get; } = [];
    public List<TRegistryCacheSource> RegistryCacheSourceScratch { get; } = [];
    public List<XRFrameBuffer> FrameBufferScratch { get; } = [];
    public List<TActiveFilterEntry> ActivePassMetadataFilterCache { get; } = [];
    public int ActivePassMetadataFilterCacheReplacementIndex;
    public IReadOnlyCollection<RenderPassMetadata>? LastActiveFilterSourcePassMetadata;
    public IReadOnlyCollection<RenderPassMetadata>? LastActiveFilterResult;
    public RenderResourceRegistry? LastActiveFilterResourceRegistry;
    public int LastActiveFilterResourceRegistryRevision = int.MinValue;
    public int LastActiveFilterPassSetSignature = int.MinValue;
    public int LastActiveFilterResourceSetSignature = int.MinValue;
    public bool LastActiveFilterConstrainToActivePassSet;
}
