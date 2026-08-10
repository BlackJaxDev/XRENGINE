namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Per-command-buffer publication state for prepared reusable frame data.
/// The owner-only path is admitted only after one complete refresh proved that
/// every mesh request uses frequency-owned storage.
/// </summary>
internal sealed class VulkanReusableFrameDataRefreshState
{
    private ulong _stableMeshSignature;
    private int _meshRequestCount = -1;
    private bool _ownerOnlyRefreshPublished;
    private int[] _fallbackRequestIndices = [];
    private int _fallbackRequestCount;
    private readonly Dictionary<VulkanReusableFrameOwnerSlotKey, ulong>
        _publishedOwnerGenerations = [];

    internal bool CanUseOwnerOnlyRefresh(
        in VulkanReusableFrameDataRefreshBatchInfo batch)
        => _ownerOnlyRefreshPublished &&
           batch.MeshRequestCount > 0 &&
           _meshRequestCount == batch.MeshRequestCount &&
           _stableMeshSignature == batch.StableMeshSignature;

    internal ReadOnlySpan<int> FallbackRequestIndices
        => _fallbackRequestIndices.AsSpan(0, _fallbackRequestCount);

    internal void BeginFullRefresh(
        in VulkanReusableFrameDataRefreshBatchInfo batch)
    {
        _stableMeshSignature = batch.StableMeshSignature;
        _meshRequestCount = batch.MeshRequestCount;
        _ownerOnlyRefreshPublished = false;
        _fallbackRequestCount = 0;
        _publishedOwnerGenerations.Clear();
    }

    /// <summary>
    /// Returns whether the exact frequency owner generation is already visible
    /// in this frame slot. This is deliberately checked before renderer and
    /// arena locks so unchanged Frame, Material, Object, and Instance owners do
    /// not pay the uniform publication pipeline on every reused frame.
    /// </summary>
    internal bool IsOwnerGenerationPublished(
        uint frameSlot,
        in VulkanReusableFrameOwnerKey ownerKey)
    {
        if (ownerKey.PublicationLayoutSignature == 0 ||
            ownerKey.OwnerIdentity == 0 ||
            ownerKey.Frequency is <= EVulkanBindingFrequency.Unknown or
                >= EVulkanBindingFrequency.Count)
        {
            return false;
        }

        VulkanReusableFrameOwnerSlotKey slotKey = new(
            ownerKey.PublicationLayoutSignature,
            ownerKey.Frequency,
            ownerKey.OwnerIdentity,
            frameSlot);
        return _publishedOwnerGenerations.TryGetValue(
                   slotKey,
                   out ulong publishedGeneration) &&
               publishedGeneration == ownerKey.ContentGeneration;
    }

    /// <summary>
    /// Records a successful owner publication for the exact mapped frame slot.
    /// A later content generation replaces the value without growing the map.
    /// </summary>
    internal void PublishOwnerGeneration(
        uint frameSlot,
        in VulkanReusableFrameOwnerKey ownerKey)
    {
        if (ownerKey.PublicationLayoutSignature == 0 ||
            ownerKey.OwnerIdentity == 0)
        {
            return;
        }

        VulkanReusableFrameOwnerSlotKey slotKey = new(
            ownerKey.PublicationLayoutSignature,
            ownerKey.Frequency,
            ownerKey.OwnerIdentity,
            frameSlot);
        _publishedOwnerGenerations[slotKey] = ownerKey.ContentGeneration;
    }

    internal void AddFallbackRequestIndex(int requestIndex)
    {
        if (_fallbackRequestIndices.Length <= _fallbackRequestCount)
        {
            int capacity = Math.Max(
                _fallbackRequestCount + 1,
                Math.Max(4, _fallbackRequestIndices.Length * 2));
            Array.Resize(ref _fallbackRequestIndices, capacity);
        }

        _fallbackRequestIndices[_fallbackRequestCount++] = requestIndex;
    }

    internal void CommitFullRefresh()
        => _ownerOnlyRefreshPublished = true;

    /// <summary>
    /// Publishes a structurally new batch after its current requests proved
    /// that every mutable mesh value has a frequency-owned arena range.
    /// Unlike a full refresh, no fallback request indices are retained.
    /// </summary>
    internal void CommitDirectOwnerOnlyRefresh(
        in VulkanReusableFrameDataRefreshBatchInfo batch)
    {
        _stableMeshSignature = batch.StableMeshSignature;
        _meshRequestCount = batch.MeshRequestCount;
        _ownerOnlyRefreshPublished = true;
        _fallbackRequestCount = 0;
    }

    internal void Invalidate()
    {
        _stableMeshSignature = 0;
        _meshRequestCount = -1;
        _ownerOnlyRefreshPublished = false;
        _fallbackRequestCount = 0;
        _publishedOwnerGenerations.Clear();
    }
}
