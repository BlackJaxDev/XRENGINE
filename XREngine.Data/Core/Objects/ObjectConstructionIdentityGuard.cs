using System.Runtime.CompilerServices;

namespace XREngine.Data.Core;

/// <summary>Protects exact construction identities until publication or rollback finishes.</summary>
internal sealed class ObjectConstructionIdentityGuard
{
    private const int Constructing = 0;
    private const int Publishing = 1;
    private const int Finished = 2;
    private static readonly ConditionalWeakTable<XRObjectBase, ObjectConstructionIdentityGuard> Guards = new();
    private static int _activeConstructions;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private int _phase = Constructing;

    internal ObjectConstructionIdentityGuard() => Interlocked.Increment(ref _activeConstructions);

    internal void Attach(XRObjectBase value) => Guards.Add(value, this);

    internal void BeginPublication() => Volatile.Write(ref _phase, Publishing);

    internal static void RequireLifetimeOwner(XRObjectBase value)
    {
        if (TryGetActive(value, out ObjectConstructionIdentityGuard? guard, out _))
            guard!.RequireOwnerThread();
    }

    internal static void RequireIdentityMutation(XRObjectBase value, bool allowPublicationCollision = false)
    {
        if (!TryGetActive(value, out ObjectConstructionIdentityGuard? guard, out int phase))
            return;
        guard!.RequireOwnerThread();
        if (phase == Publishing && !allowPublicationCollision)
            throw new InvalidOperationException("An owned object's identity cannot change during cache publication.");
    }

    internal static void RequireOwningBatch(XRObjectBase value, ObjectConstructionIdentityGuard? current,
        bool requirePublishing = false)
    {
        if (!TryGetActive(value, out ObjectConstructionIdentityGuard? guard, out int phase))
            return;
        guard!.RequireOwnerThread();
        if (!ReferenceEquals(guard, current))
            throw new InvalidOperationException("An owned object cannot enter another publication batch before construction completes.");
        if (requirePublishing && phase != Publishing)
            throw new InvalidOperationException("An owned object cannot publish before its construction batch completes.");
    }

    private static bool TryGetActive(XRObjectBase value, out ObjectConstructionIdentityGuard? guard, out int phase)
    {
        guard = null;
        phase = Finished;
        if (Volatile.Read(ref _activeConstructions) == 0 || !Guards.TryGetValue(value, out guard))
            return false;
        phase = Volatile.Read(ref guard._phase);
        return phase != Finished;
    }

    private void RequireOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("An object's construction identity belongs to the synchronous factory thread.");
    }

    internal void Release(IReadOnlyList<XRObjectBase> objects)
    {
        RequireOwnerThread();
        if (Interlocked.Exchange(ref _phase, Finished) == Finished)
            return;
        // The transaction is settled before any guard is removed. The count remains
        // nonzero until every exact table entry from this construction is gone.
        foreach (XRObjectBase value in objects)
            if (Guards.TryGetValue(value, out ObjectConstructionIdentityGuard? current) && ReferenceEquals(current, this))
                Guards.Remove(value);
        Interlocked.Decrement(ref _activeConstructions);
    }
}
