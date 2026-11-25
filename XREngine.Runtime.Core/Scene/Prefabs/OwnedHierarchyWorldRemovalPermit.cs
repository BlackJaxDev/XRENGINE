using XREngine.Scene.Transforms;

namespace XREngine.Scene.Prefabs;

/// <summary>Admits one exact source-world removal from a reserved hierarchy object.</summary>
internal sealed class OwnedHierarchyWorldRemovalPermit(
    OwnedHierarchyRetirement retirement, XRWorldObjectBase target, IRuntimeWorldContext source) : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private int _state;

    internal OwnedHierarchyRetirement Retirement { get; } = retirement;
    internal XRWorldObjectBase Target { get; } = target;
    internal IRuntimeWorldContext Source { get; } = source;
    internal bool Active => _state is 1 or 2 && Environment.CurrentManagedThreadId == _thread;
    internal bool Completed => _state is 3 or 5;

    internal void Enter(XRWorldObjectBase target, IRuntimeWorldContext? next)
    {
        lock (SceneRootCollection.MutationGate)
        {
            if (_state != 0 || Environment.CurrentManagedThreadId != _thread ||
                !ReferenceEquals(Target, target) || !ReferenceEquals(target.World, Source) || next is not null)
                throw new InvalidOperationException("World removal requires its exact source and target.");
            Retirement.Validate();
            Target.EnterReservedHierarchyMutation(Retirement.Reservation);
            _state = 1;
            Retirement.MarkWorldRemovalEntered(Target);
        }
    }

    internal void RequireCommit(XRWorldObjectBase target, IRuntimeWorldContext? next)
    {
        if (_state != 1 || !ReferenceEquals(target, Target) ||
            !ReferenceEquals(target.World, Source) || next is not null)
            throw new InvalidOperationException("The exact world removal cannot commit.");
        Retirement.Validate();
    }

    internal void Commit()
    {
        if (_state != 1)
            throw new InvalidOperationException("The world removal did not enter.");
        _state = 2;
        Retirement.MarkWorldRemovalCommitted(Target);
    }

    internal void MarkStoreDetached(TransformBase transform)
    {
        if (_state != 2 || !ReferenceEquals(Target, transform) ||
            transform.HierarchyStore is not null || transform.HierarchyHandle != default)
            throw new InvalidOperationException("The transform did not release its exact world store.");
        Retirement.MarkWorldStoreDetached(transform);
    }

    internal void Validate() => Retirement.Validate();

    internal void Complete()
    {
        if (_state != 2)
            throw new InvalidOperationException("The world removal did not commit.");
        Retirement.Validate();
        _state = 3;
        Retirement.MarkWorldNotificationsComplete(Target);
    }

    public void Dispose()
    {
        lock (SceneRootCollection.MutationGate)
        {
            if (_state is 1 or 2 or 3)
            {
                Target.ExitStructuralMutation();
                _state = _state == 3 ? 5 : 4;
            }
        }
    }
}
