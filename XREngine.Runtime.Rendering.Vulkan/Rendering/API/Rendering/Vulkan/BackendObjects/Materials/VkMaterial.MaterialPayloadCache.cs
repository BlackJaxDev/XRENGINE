namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMaterial
{
    private const int MaximumCachedAutoUniformMaterialPlans = 4096;
    private MaterialUniformBindingCacheKey _materialUniformBindingPayloadKey;
    private MaterialUniformBindingPayload? _materialUniformBindingPayload;
    private bool _hasMaterialUniformBindingPayload;
    private readonly Dictionary<
        AutoUniformMaterialWritePlanCacheKey,
        AutoUniformMaterialWritePlan> _autoUniformMaterialWritePlans = [];
    private ulong _autoUniformMaterialPlanLayoutVersion;
    private ulong _autoUniformMaterialPlanValueVersion;

    /// <summary>
    /// Retrieves the immutable numeric payload owned by this exact material
    /// revision. The cache belongs to the material wrapper so transient or
    /// distinct compatible program wrappers cannot force redundant packing.
    /// </summary>
    internal bool TryGetMaterialUniformBindingPayload(
        in MaterialUniformBindingCacheKey key,
        out MaterialUniformBindingPayload? payload)
    {
        lock (_stateSync)
        {
            if (_hasMaterialUniformBindingPayload &&
                _materialUniformBindingPayloadKey.Equals(key))
            {
                payload = _materialUniformBindingPayload;
                return payload is not null;
            }

            payload = null;
            return false;
        }
    }

    internal void CacheMaterialUniformBindingPayload(
        in MaterialUniformBindingCacheKey key,
        MaterialUniformBindingPayload payload)
    {
        lock (_stateSync)
        {
            _materialUniformBindingPayloadKey = key;
            _materialUniformBindingPayload = payload;
            _hasMaterialUniformBindingPayload = true;
        }
    }

    internal void GetMaterialUniformBindingPayloadCacheState(
        in MaterialUniformBindingCacheKey key,
        out bool hasPayload,
        out bool keyMatches)
    {
        lock (_stateSync)
        {
            hasPayload = _hasMaterialUniformBindingPayload &&
                _materialUniformBindingPayload is not null;
            keyMatches = hasPayload &&
                _materialUniformBindingPayloadKey.Equals(key);
        }
    }

    internal bool TryGetAutoUniformMaterialWritePlan(
        in AutoUniformMaterialWritePlanCacheKey key,
        out AutoUniformMaterialWritePlan? plan)
    {
        lock (_stateSync)
            return _autoUniformMaterialWritePlans.TryGetValue(key, out plan);
    }

    internal void CacheAutoUniformMaterialWritePlan(
        in AutoUniformMaterialWritePlanCacheKey key,
        AutoUniformMaterialWritePlan plan)
    {
        lock (_stateSync)
        {
            if (_autoUniformMaterialPlanLayoutVersion !=
                    key.MaterialLayoutVersion ||
                _autoUniformMaterialPlanValueVersion !=
                    key.MaterialValueVersion ||
                _autoUniformMaterialWritePlans.Count >=
                    MaximumCachedAutoUniformMaterialPlans)
            {
                _autoUniformMaterialWritePlans.Clear();
                _autoUniformMaterialPlanLayoutVersion =
                    key.MaterialLayoutVersion;
                _autoUniformMaterialPlanValueVersion =
                    key.MaterialValueVersion;
            }

            _autoUniformMaterialWritePlans[key] = plan;
        }
    }
}
