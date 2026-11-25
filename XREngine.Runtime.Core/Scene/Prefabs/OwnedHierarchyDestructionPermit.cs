namespace XREngine.Scene.Prefabs;

/// <summary>Admits one exact synchronous destruction before public callbacks run.</summary>
internal sealed class OwnedHierarchyDestructionPermit(OwnedHierarchyRetirement retirement, XRWorldObjectBase target)
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private int _state;

    internal OwnedHierarchyRetirement Retirement { get; } = retirement;
    internal XRWorldObjectBase Target { get; } = target;
    internal bool Active => Volatile.Read(ref _state) == 1 && Environment.CurrentManagedThreadId == _thread;
    internal bool Returned => Volatile.Read(ref _state) == 2;

    internal bool TryEnter()
        => Environment.CurrentManagedThreadId == _thread && Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    internal void AdvanceLifetime(long previous, long current)
    {
        if (!Active)
            throw new InvalidOperationException("Only the consumed exact destruction permit can advance lifetime progress.");
        Retirement.AdvanceLifetime(Target, previous, current);
    }

    internal void Exit()
    {
        if (!Active || Interlocked.CompareExchange(ref _state, 2, 1) != 1)
            throw new InvalidOperationException("The exact destruction permit is not active.");
    }
}
