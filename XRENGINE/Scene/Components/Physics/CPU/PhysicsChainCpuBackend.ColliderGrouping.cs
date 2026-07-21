namespace XREngine.Components;

public sealed partial class PhysicsChainCpuBackend
{
    private long _sharedColliderBatchGroupCount;
    private long _sharedColliderGroupedInstanceCount;

    /// <summary>
    /// Steps instances sharing the exact pose stream consecutively. This
    /// preserves per-instance independence while keeping shared collider data
    /// hot and avoids allocating or reordering the caller's handle storage.
    /// </summary>
    private void StepSharedColliderGroups(
        ReadOnlySpan<PhysicsChainArenaHandle> handles,
        ref bool allSucceeded)
    {
        for (int firstIndex = 0; firstIndex < handles.Length; ++firstIndex)
        {
            _instances.TryGet(handles[firstIndex], out PhysicsChainCpuRuntimeInstance? first);
            PhysicsChainCpuSharedColliderSet? shared = first!.SharedColliderSet;
            if (shared is null || SharedSetAppearsEarlier(handles, firstIndex, shared))
                continue;

            int groupedCount = 0;
            for (int candidateIndex = firstIndex; candidateIndex < handles.Length; ++candidateIndex)
            {
                _instances.TryGet(handles[candidateIndex], out PhysicsChainCpuRuntimeInstance? candidate);
                if (!ReferenceEquals(candidate!.SharedColliderSet, shared))
                    continue;

                ++groupedCount;
                if (!StepInstance(candidate))
                    allSucceeded = false;
            }

            Interlocked.Increment(ref _sharedColliderBatchGroupCount);
            Interlocked.Add(ref _sharedColliderGroupedInstanceCount, groupedCount);
        }
    }

    private bool SharedSetAppearsEarlier(
        ReadOnlySpan<PhysicsChainArenaHandle> handles,
        int endExclusive,
        PhysicsChainCpuSharedColliderSet shared)
    {
        for (int handleIndex = 0; handleIndex < endExclusive; ++handleIndex)
        {
            _instances.TryGet(handles[handleIndex], out PhysicsChainCpuRuntimeInstance? earlier);
            if (ReferenceEquals(earlier!.SharedColliderSet, shared))
                return true;
        }
        return false;
    }
}
