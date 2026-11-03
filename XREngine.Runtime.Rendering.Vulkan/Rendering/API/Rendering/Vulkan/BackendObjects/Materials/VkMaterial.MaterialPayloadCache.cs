namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMaterial
{
    private const int MaximumCachedAutoUniformMaterialPlans = 4096;
    // Draw recording reads the payload for every draw, so reads take no lock. A
    // new payload publishes a new immutable entry.
    private MaterialUniformBindingPayloadEntry? _materialUniformBindingPayloadEntry;
    // Draw recording reads this table for every auto-uniform block, so reads take
    // no lock. A new plan publishes a new immutable table under the state lock.
    private AutoUniformMaterialWritePlanTable? _autoUniformMaterialWritePlanTable;

    /// <summary>
    /// Retrieves the immutable numeric payload owned by this exact material
    /// revision. The cache belongs to the material wrapper so transient or
    /// distinct compatible program wrappers cannot force redundant packing.
    /// </summary>
    internal bool TryGetMaterialUniformBindingPayload(
        in MaterialUniformBindingCacheKey key,
        out MaterialUniformBindingPayload? payload)
    {
        MaterialUniformBindingPayloadEntry? entry = Volatile.Read(ref _materialUniformBindingPayloadEntry);
        if (entry is not null && entry.Key.Equals(key))
        {
            payload = entry.Payload;
            return true;
        }

        payload = null;
        return false;
    }

    internal void CacheMaterialUniformBindingPayload(
        in MaterialUniformBindingCacheKey key,
        MaterialUniformBindingPayload payload)
        => Volatile.Write(
            ref _materialUniformBindingPayloadEntry,
            new MaterialUniformBindingPayloadEntry(key, payload));

    internal void GetMaterialUniformBindingPayloadCacheState(
        in MaterialUniformBindingCacheKey key,
        out bool hasPayload,
        out bool keyMatches)
    {
        MaterialUniformBindingPayloadEntry? entry = Volatile.Read(ref _materialUniformBindingPayloadEntry);
        hasPayload = entry is not null;
        keyMatches = hasPayload && entry!.Key.Equals(key);
    }

    internal bool TryGetAutoUniformMaterialWritePlan(
        in AutoUniformMaterialWritePlanCacheKey key,
        out AutoUniformMaterialWritePlan? plan)
    {
        AutoUniformMaterialWritePlanTable? table = Volatile.Read(ref _autoUniformMaterialWritePlanTable);
        if (table is null)
        {
            plan = null;
            return false;
        }

        return table.Plans.TryGetValue(key, out plan);
    }

    /// <summary>
    /// Publishes a table that adds <paramref name="plan"/>. A material layout or value
    /// version change, or a full table, starts a new table. Plans are built only on
    /// a cache miss, so the copy happens rarely after warm-up.
    /// </summary>
    internal void CacheAutoUniformMaterialWritePlan(
        in AutoUniformMaterialWritePlanCacheKey key,
        AutoUniformMaterialWritePlan plan)
    {
        lock (_stateSync)
        {
            AutoUniformMaterialWritePlanTable? current = _autoUniformMaterialWritePlanTable;
            bool reuse = current is not null &&
                current.LayoutVersion == key.MaterialLayoutVersion &&
                current.ValueVersion == key.MaterialValueVersion &&
                current.Plans.Count < MaximumCachedAutoUniformMaterialPlans;
            Dictionary<AutoUniformMaterialWritePlanCacheKey, AutoUniformMaterialWritePlan> plans = reuse
                ? new(current!.Plans)
                : new(1);
            plans[key] = plan;
            Volatile.Write(
                ref _autoUniformMaterialWritePlanTable,
                new AutoUniformMaterialWritePlanTable(key.MaterialLayoutVersion, key.MaterialValueVersion, plans));
        }
    }

    /// <summary>Immutable write-plan table for one material layout and value version.</summary>
    private sealed class AutoUniformMaterialWritePlanTable(
        ulong layoutVersion,
        ulong valueVersion,
        Dictionary<AutoUniformMaterialWritePlanCacheKey, AutoUniformMaterialWritePlan> plans)
    {
        internal ulong LayoutVersion { get; } = layoutVersion;
        internal ulong ValueVersion { get; } = valueVersion;
        internal Dictionary<AutoUniformMaterialWritePlanCacheKey, AutoUniformMaterialWritePlan> Plans { get; } = plans;
    }

    /// <summary>Immutable numeric payload for one material uniform binding key.</summary>
    private sealed class MaterialUniformBindingPayloadEntry(
        MaterialUniformBindingCacheKey key,
        MaterialUniformBindingPayload payload)
    {
        internal MaterialUniformBindingCacheKey Key { get; } = key;
        internal MaterialUniformBindingPayload Payload { get; } = payload;
    }
}
