namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Holds the retired resource generations that one descriptor owner drain releases together, and the descriptor
/// sets that still pin one of them.
/// </summary>
/// <remarks>
/// A window resize retires every size-dependent render target of the pipeline, and a drag resize does this for
/// many sizes in a row. The drain must find each descriptor set, material program state, and mesh descriptor
/// allocation that still refers to a retired generation. When the drain handled one generation at a time, it scanned
/// all tracked descriptor sets, materials, and mesh renderers once for each generation, which stopped rendering for
/// many seconds after a resize. A batch lets the drain scan each collection once.
/// </remarks>
internal sealed class VulkanSupersededDescriptorOwnerBatch
{
    private readonly HashSet<VulkanSupersededResourceDescriptorOwner> _pending = [];
    private readonly Dictionary<ulong, ulong> _affectedSetGenerations = [];
    private VulkanDescriptorSetGenerationReference[] _affectedSets = [];

    /// <summary>Gets the number of distinct retired generations in the batch.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Gets the retired generations in the batch.</summary>
    public IEnumerable<VulkanSupersededResourceDescriptorOwner> Pending => _pending;

    /// <summary>Gets the descriptor sets that pinned a retired generation when the batch captured them.</summary>
    public ReadOnlySpan<VulkanDescriptorSetGenerationReference> AffectedSets => _affectedSets;

    /// <summary>Adds one retired generation. A generation that the batch already holds is not added again.</summary>
    public void Add(in VulkanSupersededResourceDescriptorOwner owner)
        => _pending.Add(owner);

    /// <summary>
    /// Returns true when a descriptor set state pins one of the retired generations. Call with the lifetime tracker
    /// lock held, because the pinned references change when the set is written.
    /// </summary>
    public bool PinsPendingGeneration(VulkanDescriptorSetLifetimeRecord state)
    {
        foreach ((VulkanResourceLifetimeKey key, ulong generation) in state.PinnedReferences)
            if (_pending.Contains(new VulkanSupersededResourceDescriptorOwner(key, generation)))
                return true;
        return false;
    }

    /// <summary>
    /// Stores the descriptor sets that pin a retired generation, so that owners can test their sets with one
    /// lookup each.
    /// </summary>
    public void SetAffectedSets(List<VulkanDescriptorSetGenerationReference>? affectedSets)
    {
        _affectedSetGenerations.Clear();
        if (affectedSets is null || affectedSets.Count == 0)
        {
            _affectedSets = [];
            return;
        }

        _affectedSets = [.. affectedSets];
        foreach (VulkanDescriptorSetGenerationReference set in _affectedSets)
            _affectedSetGenerations[set.Set.Handle] = set.Generation;
    }

    /// <summary>
    /// Returns true when a descriptor set is one of the affected sets, with the same set generation, and still pins a
    /// retired generation. The generation check stops a reused native handle from matching. Call with the lifetime
    /// tracker lock held.
    /// </summary>
    public bool IsAffectedSet(ulong handle, VulkanDescriptorSetLifetimeRecord state)
        => _affectedSetGenerations.TryGetValue(handle, out ulong generation) &&
            generation == state.Generation &&
            PinsPendingGeneration(state);
}
