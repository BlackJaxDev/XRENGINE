namespace XREngine.Rendering.Vulkan;

internal sealed class CommandChainSchedule
{
    private RenderPassChainGroup[] _groups = [];
    private int _groupCount;
    private CommandRecordingDependencySignature _dependencySignature;

    public CommandChainSchedule()
    {
    }

    public CommandChainSchedule(
        ulong structuralSignature,
        ulong resourcePlanRevision,
        ReadOnlyMemory<RenderPassChainGroup> groups,
        bool requiresFreshPrimary = false,
        int inlineFrameOpCount = 0)
        => Reset(
            structuralSignature,
            resourcePlanRevision,
            groups.Span,
            requiresFreshPrimary,
            inlineFrameOpCount);

    public ulong StructuralSignature { get; private set; }
    public ulong ResourcePlanRevision { get; private set; }
    public CommandChainScheduleCacheIdentity CacheIdentity { get; private set; }
    public CommandRecordingDependencySignature DependencySignature
    {
        get => _dependencySignature;
        private set => _dependencySignature = value;
    }
    internal ref readonly CommandRecordingDependencySignature DependencySignatureReference
        => ref _dependencySignature;
    public long ArtifactMutationGeneration { get; private set; }
    public int ScheduledChainCount { get; private set; }
    /// <summary>
    /// True when inline operations publish per-frame GPU data or submission state.
    /// The primary must be recorded again, while its immutable secondary islands
    /// remain eligible for reuse.
    /// </summary>
    public bool RequiresFreshPrimary { get; private set; }
    public int InlineFrameOpCount { get; private set; }
    public ReadOnlyMemory<RenderPassChainGroup> Groups => _groups.AsMemory(0, _groupCount);

    public RenderPassChainGroup RentGroup(int index)
    {
        EnsureGroupCapacity(index + 1);
        return _groups[index] ??= new RenderPassChainGroup();
    }

    public void Reset(
        ulong structuralSignature,
        ulong resourcePlanRevision,
        ReadOnlySpan<RenderPassChainGroup> groups,
        bool requiresFreshPrimary = false,
        int inlineFrameOpCount = 0)
    {
        EnsureGroupCapacity(groups.Length);
        groups.CopyTo(_groups);
        _groupCount = groups.Length;
        StructuralSignature = structuralSignature;
        ResourcePlanRevision = resourcePlanRevision;
        RequiresFreshPrimary = requiresFreshPrimary;
        InlineFrameOpCount = Math.Max(0, inlineFrameOpCount);
        ScheduledChainCount = CountScheduledChains(groups);
    }

    public void PublishDependencySignature(in CommandRecordingDependencySignature signature)
        => _dependencySignature = signature;

    public void PublishCacheIdentity(in CommandChainScheduleCacheIdentity identity)
        => CacheIdentity = identity;

    public void PublishArtifactMutationGeneration(long generation)
        => ArtifactMutationGeneration = generation;

    private static int CountScheduledChains(ReadOnlySpan<RenderPassChainGroup> groups)
    {
        int count = 0;
        for (int i = 0; i < groups.Length; i++)
            count += groups[i].ChainKeys.Length;
        return count;
    }

    private void EnsureGroupCapacity(int required)
    {
        if (_groups.Length >= required)
            return;

        int capacity = Math.Max(required, _groups.Length == 0 ? 8 : _groups.Length * 2);
        Array.Resize(ref _groups, capacity);
    }
}
