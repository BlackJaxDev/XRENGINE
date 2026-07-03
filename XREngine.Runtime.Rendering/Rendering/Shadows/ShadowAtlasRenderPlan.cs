namespace XREngine.Rendering.Shadows;

public sealed class ShadowAtlasRenderPlan
{
    private ShadowAtlasRenderPlanEntry[] _entries = [];
    private ShadowAtlasRenderPlanMember[] _members = [];
    private int _entryCount;
    private int _memberCount;

    public ulong FrameId { get; private set; }
    public ulong PlanId { get; private set; }
    public int RequestCount { get; private set; }
    public int EntryCount => _entryCount;
    public int MemberCount => _memberCount;
    public ReadOnlySpan<ShadowAtlasRenderPlanEntry> Entries => _entries.AsSpan(0, _entryCount);
    public ReadOnlySpan<ShadowAtlasRenderPlanMember> Members => _members.AsSpan(0, _memberCount);

    public ShadowAtlasRenderPlanEntry GetEntry(int index)
    {
        if ((uint)index >= (uint)_entryCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _entries[index];
    }

    public ShadowAtlasRenderPlanMember GetMember(int index)
    {
        if ((uint)index >= (uint)_memberCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        return _members[index];
    }

    internal void Clear()
    {
        for (int i = 0; i < _entryCount; i++)
            _entries[i] = default;
        for (int i = 0; i < _memberCount; i++)
            _members[i] = default;

        _entryCount = 0;
        _memberCount = 0;
        FrameId = 0u;
        PlanId = 0u;
        RequestCount = 0;
    }

    internal void SetData(
        ulong frameId,
        ulong planId,
        int requestCount,
        IReadOnlyList<ShadowAtlasRenderPlanEntry> entries,
        IReadOnlyList<ShadowAtlasRenderPlanMember> members)
    {
        EnsureEntryCapacity(entries.Count);
        EnsureMemberCapacity(members.Count);

        for (int i = 0; i < entries.Count; i++)
            _entries[i] = entries[i];
        for (int i = entries.Count; i < _entryCount; i++)
            _entries[i] = default;

        for (int i = 0; i < members.Count; i++)
            _members[i] = members[i];
        for (int i = members.Count; i < _memberCount; i++)
            _members[i] = default;

        _entryCount = entries.Count;
        _memberCount = members.Count;
        FrameId = frameId;
        PlanId = planId;
        RequestCount = requestCount;
    }

    private void EnsureEntryCapacity(int count)
    {
        if (_entries.Length >= count)
            return;

        int next = Math.Max(4, _entries.Length);
        while (next < count)
            next *= 2;

        Array.Resize(ref _entries, next);
    }

    private void EnsureMemberCapacity(int count)
    {
        if (_members.Length >= count)
            return;

        int next = Math.Max(4, _members.Length);
        while (next < count)
            next *= 2;

        Array.Resize(ref _members, next);
    }
}
