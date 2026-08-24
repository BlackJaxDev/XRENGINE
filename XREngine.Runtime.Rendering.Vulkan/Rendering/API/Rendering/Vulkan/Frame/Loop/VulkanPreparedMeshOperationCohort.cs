namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Retains the last fully materialized mesh-operation cohort on the render
/// thread.  Raw producer requests are still drained and compared every frame;
/// this cache only avoids rebuilding invariant Vulkan draw state after an exact
/// stable cohort match.
/// </summary>
internal sealed class VulkanPreparedMeshOperationCohort
{
    private readonly VulkanPreparedMeshOperationCohortEntry[] _entries =
        new VulkanPreparedMeshOperationCohortEntry[VulkanMeshOperationRequestQueue.Capacity];
    private readonly VulkanMeshOperationRequest[] _operations =
        new VulkanMeshOperationRequest[VulkanMeshOperationRequestQueue.Capacity];

    internal bool IsValid { get; private set; }
    internal int Count { get; private set; }

    internal ref readonly VulkanPreparedMeshOperationCohortEntry GetEntry(int index)
        => ref _entries[index];

    internal ref readonly VulkanMeshOperationRequest GetOperation(int index)
        => ref _operations[index];

    internal void Publish(
        ReadOnlySpan<VulkanPreparedMeshOperationCohortEntry> entries,
        ReadOnlySpan<VulkanMeshOperationRequest> operations)
    {
        if (entries.Length != operations.Length ||
            entries.Length > _entries.Length)
        {
            Invalidate();
            return;
        }

        Invalidate();
        for (int index = 0; index < entries.Length; index++)
        {
            VulkanPreparedMeshOperationCohortEntry entry = entries[index];
            _entries[index] = entry;
            // Unsafe entries are exact-match holes, not retained draw recipes.
            // Their current operation is materialized directly into the ingress
            // on a stable cohort hit.
            _operations[index] = entry.IsReusable
                ? operations[index]
                : default;
        }
        Count = entries.Length;
        IsValid = Count > 0;
    }

    internal void Invalidate()
    {
        _entries.AsSpan(0, Count).Clear();
        _operations.AsSpan(0, Count).Clear();
        IsValid = false;
        Count = 0;
    }
}
