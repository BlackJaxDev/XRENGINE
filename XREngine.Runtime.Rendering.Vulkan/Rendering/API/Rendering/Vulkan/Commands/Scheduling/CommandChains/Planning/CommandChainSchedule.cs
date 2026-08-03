namespace XREngine.Rendering.Vulkan;

internal sealed class CommandChainSchedule
{
    private RenderPassChainGroup[] _groups = [];
    private int _groupCount;

    public CommandChainSchedule()
    {
    }

    public CommandChainSchedule(
        ulong structuralSignature,
        ulong resourcePlanRevision,
        ReadOnlyMemory<RenderPassChainGroup> groups)
        => Reset(structuralSignature, resourcePlanRevision, groups.Span);

    public ulong StructuralSignature { get; private set; }
    public ulong ResourcePlanRevision { get; private set; }
    public CommandRecordingDependencySignature DependencySignature { get; private set; }
    public ReadOnlyMemory<RenderPassChainGroup> Groups => _groups.AsMemory(0, _groupCount);

    public RenderPassChainGroup RentGroup(int index)
    {
        EnsureGroupCapacity(index + 1);
        return _groups[index] ??= new RenderPassChainGroup();
    }

    public void Reset(
        ulong structuralSignature,
        ulong resourcePlanRevision,
        ReadOnlySpan<RenderPassChainGroup> groups)
    {
        EnsureGroupCapacity(groups.Length);
        groups.CopyTo(_groups);
        _groupCount = groups.Length;
        StructuralSignature = structuralSignature;
        ResourcePlanRevision = resourcePlanRevision;
    }

    public void PublishDependencySignature(in CommandRecordingDependencySignature signature)
        => DependencySignature = signature;

    private void EnsureGroupCapacity(int required)
    {
        if (_groups.Length >= required)
            return;

        int capacity = Math.Max(required, _groups.Length == 0 ? 8 : _groups.Length * 2);
        Array.Resize(ref _groups, capacity);
    }
}
