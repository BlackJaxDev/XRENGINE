namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Preallocated, transactional current-frame mesh staging. Unlike the retained
/// cohort, this stores current draw/context values and lowers their dependencies
/// only after the final recording context is known; it is never a replay cache.
/// </summary>
internal sealed class VulkanPreparedMeshIngress
{
    private const int ResourceUsesPerEntryBudget = 16;

    private readonly VulkanPreparedMeshIngressEntry[] _entries =
        new VulkanPreparedMeshIngressEntry[VulkanMeshOperationRequestQueue.Capacity];
    private readonly FrameOpResourceUse[] _resourceUses =
        new FrameOpResourceUse[
            VulkanMeshOperationRequestQueue.Capacity *
            ResourceUsesPerEntryBudget];
    private int _count;
    private int _resourceUseCount;
    private int _dynamicUiCount;

    internal int Count => _count;
    internal int ResourceUseCapacity => _resourceUses.Length;
    internal bool HasDynamicUiEntries => _dynamicUiCount > 0;
    internal bool IsCohortHit { get; private set; }
    internal int ReusedOperationCount { get; private set; }
    internal int LegacyHoleMaterializationCount { get; private set; }
    internal ref readonly VulkanPreparedMeshIngressEntry GetEntry(int index) => ref _entries[index];

    internal void SetContext(int index, in FrameOpContext context)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));
        _entries[index] = _entries[index] with { Context = context };
    }

    internal void Clear()
    {
        _entries.AsSpan(0, _count).Clear();
        _resourceUses.AsSpan(0, _resourceUseCount).Clear();
        _count = 0;
        _resourceUseCount = 0;
        _dynamicUiCount = 0;
        IsCohortHit = false;
        ReusedOperationCount = 0;
        LegacyHoleMaterializationCount = 0;
    }

    internal bool TryAppend(
        int passIndex,
        XRFrameBuffer? target,
        in PendingMeshDraw draw,
        in FrameOpContext context,
        bool preserveSubmissionOrder,
        bool isDynamicUi)
    {
        if (_count >= _entries.Length)
            return false;
        _entries[_count++] = new(
            passIndex, target, draw, context, preserveSubmissionOrder,
            isDynamicUi, ResourceUseOffset: 0, ResourceUseCount: 0);
        if (isDynamicUi)
            _dynamicUiCount++;
        return true;
    }

    /// <summary>
    /// Commits hit telemetry to this current-frame transaction. The frame loop
    /// publishes it only after context, pass, and resource-use finalization succeeds.
    /// </summary>
    internal void MarkCohortHit(
        int reusedOperationCount,
        int legacyHoleMaterializationCount)
    {
        IsCohortHit = true;
        ReusedOperationCount = reusedOperationCount;
        LegacyHoleMaterializationCount = legacyHoleMaterializationCount;
    }

    /// <summary>
    /// Normalizes pass identities and lowers dependencies from the final
    /// post-coalescing context. No retained resource-use data crosses frames.
    /// </summary>
    internal bool TryFinalize(ref FrameOpResourceUseList scratch)
    {
        _resourceUseCount = 0;
        for (int index = 0; index < _count; index++)
        {
            VulkanPreparedMeshIngressEntry entry = _entries[index];
            int passIndex = VulkanCommandRuntime.EnsureValidPassIndex(
                entry.PassIndex,
                nameof(MeshDrawOp),
                entry.Context.PassMetadata);
            if (passIndex == int.MinValue)
                return false;

            PendingMeshDraw draw = entry.Draw;
            FrameOpContext context = entry.Context;
            VulkanFrameOperationSemantics.LowerMeshDrawResourceUse(
                entry.Target,
                in draw,
                in context,
                ref scratch);
            if (scratch.Count > _resourceUses.Length - _resourceUseCount)
                return false;

            int resourceOffset = _resourceUseCount;
            scratch.CopyTo(
                _resourceUses.AsSpan(resourceOffset, scratch.Count));
            _resourceUseCount += scratch.Count;
            _entries[index] = entry with
            {
                PassIndex = passIndex,
                ResourceUseOffset = resourceOffset,
                ResourceUseCount = scratch.Count,
            };
        }

        return true;
    }

    internal void PublishDrawStats()
    {
        for (int index = 0; index < _count; index++)
        {
            ref readonly VulkanPreparedMeshIngressEntry entry =
                ref _entries[index];
            PendingMeshDraw draw = entry.Draw;
            VulkanFrameOperationSemantics.PublishMeshDrawStats(
                entry.PassIndex,
                in draw);
        }
    }

    internal ReadOnlySpan<FrameOpResourceUse> GetResourceUses(in VulkanPreparedMeshIngressEntry entry)
        => _resourceUses.AsSpan(entry.ResourceUseOffset, entry.ResourceUseCount);
}
