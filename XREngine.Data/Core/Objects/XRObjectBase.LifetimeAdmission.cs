using System.ComponentModel;
using System.Runtime.ExceptionServices;
using MemoryPack;
using YamlDotNet.Serialization;

namespace XREngine.Data.Core;

public abstract partial class XRObjectBase
{
    private long _lifetimeRevision;
    private int _lifetimeWritersOrDispatch;
    private int _dispatchThreadId;
    private int _dispatchBaseEntry;
    private int _activeCanonicalDestructions;

    protected bool HasActiveDestructionAdmission =>
        Volatile.Read(ref _activeCanonicalDestructions) != 0 ||
        Volatile.Read(ref _lifetimeWritersOrDispatch) < 0;

    /// <summary>Identifies admitted lifetime operations. Generate never resets this value.</summary>
    [YamlIgnore, MemoryPackIgnore, Browsable(false)]
    internal long LifetimeRevision => Volatile.Read(ref _lifetimeRevision);

    internal bool EnterLifetimeMutation(EObjectLifetimeMutation kind)
    {
        bool usesDispatchClaim = EnterLifetimeActivity(kind);
        try
        {
            OnLifetimeMutationBeginning(kind);
            try
            {
                ObjectConstructionIdentityGuard.RequireLifetimeOwner(this);
                long previous;
                do
                {
                    previous = Volatile.Read(ref _lifetimeRevision);
                    if (previous == long.MaxValue)
                        throw new InvalidOperationException("The object's lifetime revision is exhausted.");
                }
                while (Interlocked.CompareExchange(ref _lifetimeRevision, previous + 1, previous) != previous);
                OnLifetimeMutationAdmitted(kind, previous, previous + 1);
                return usesDispatchClaim;
            }
            catch
            {
                OnLifetimeMutationEnded(kind);
                throw;
            }
        }
        catch
        {
            ReleaseLifetimeActivity(kind, usesDispatchClaim);
            throw;
        }
    }

    internal void ExitLifetimeMutation(EObjectLifetimeMutation kind, bool usesDispatchClaim)
    {
        try { OnLifetimeMutationEnded(kind); }
        finally { ReleaseLifetimeActivity(kind, usesDispatchClaim); }
    }

    private bool EnterLifetimeActivity(EObjectLifetimeMutation kind)
    {
        while (true)
        {
            int state = Volatile.Read(ref _lifetimeWritersOrDispatch);
            if (state < 0)
            {
                if (Volatile.Read(ref _dispatchThreadId) != Environment.CurrentManagedThreadId ||
                    Volatile.Read(ref _dispatchBaseEntry) != 0 ||
                    kind is not (EObjectLifetimeMutation.DestroyNow or EObjectLifetimeMutation.QueueDestruction))
                    throw new InvalidOperationException("The queued destruction owns this exact lifetime.");
                if (kind == EObjectLifetimeMutation.DestroyNow &&
                    Interlocked.CompareExchange(ref _dispatchBaseEntry, 1, 0) != 0)
                    throw new InvalidOperationException("The queued destruction entered its canonical body twice.");
                if (kind == EObjectLifetimeMutation.DestroyNow)
                    Interlocked.Increment(ref _activeCanonicalDestructions);
                return true;
            }
            if (state == int.MaxValue)
                throw new InvalidOperationException("The object's lifetime admission is exhausted.");
            if (Interlocked.CompareExchange(ref _lifetimeWritersOrDispatch, state + 1, state) == state)
            {
                if (kind == EObjectLifetimeMutation.DestroyNow)
                    Interlocked.Increment(ref _activeCanonicalDestructions);
                return false;
            }
        }
    }

    private void ReleaseLifetimeActivity(EObjectLifetimeMutation kind, bool usesDispatchClaim)
    {
        if (kind == EObjectLifetimeMutation.DestroyNow)
            Interlocked.Decrement(ref _activeCanonicalDestructions);
        if (usesDispatchClaim)
        {
            if (kind == EObjectLifetimeMutation.DestroyNow)
                Volatile.Write(ref _dispatchBaseEntry, 2);
        }
        else
            Interlocked.Decrement(ref _lifetimeWritersOrDispatch);
    }

    private bool TryEnterQueuedDispatch(long token)
    {
        if (Interlocked.CompareExchange(ref _lifetimeWritersOrDispatch, -1, 0) != 0)
            return false;
        Volatile.Write(ref _dispatchThreadId, Environment.CurrentManagedThreadId);
        Volatile.Write(ref _dispatchBaseEntry, 0);
        bool began = false;
        bool admitted = false;
        ExceptionDispatchInfo? failure = null;
        try
        {
            if (Volatile.Read(ref _pendingDestructionToken) != token)
                return false;
            if (IsDestroyed)
            {
                ClearDestroyQueuedFlag(token);
                return false;
            }
            OnLifetimeMutationBeginning(EObjectLifetimeMutation.DispatchDestruction);
            began = true;
            ObjectConstructionIdentityGuard.RequireLifetimeOwner(this);
            admitted = true;
        }
        catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        finally
        {
            if (!admitted)
            {
                try { if (began) OnLifetimeMutationEnded(EObjectLifetimeMutation.DispatchDestruction); }
                catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
                finally
                {
                    Volatile.Write(ref _dispatchThreadId, 0);
                    Volatile.Write(ref _lifetimeWritersOrDispatch, 0);
                }
            }
        }
        failure?.Throw();
        return admitted;
    }

    private void ExitQueuedDispatch()
    {
        try { OnLifetimeMutationEnded(EObjectLifetimeMutation.DispatchDestruction); }
        finally
        {
            Volatile.Write(ref _dispatchThreadId, 0);
            Volatile.Write(ref _lifetimeWritersOrDispatch, 0);
        }
    }

    /// <summary>
    /// Admits a lifetime operation before its field, queue, or cache effects.
    /// Overrides must be nonblocking and must not call application callbacks.
    /// The default admission hook needs no allocation or synchronization.
    /// </summary>
    protected virtual void OnLifetimeMutationBeginning(EObjectLifetimeMutation kind) { }

    /// <summary>Advances exact permitted progress before operation callbacks or field writes.</summary>
    protected virtual void OnLifetimeMutationAdmitted(EObjectLifetimeMutation kind, long previous, long current) { }

    /// <summary>Releases admission after all operation callbacks and cache effects finish.</summary>
    protected virtual void OnLifetimeMutationEnded(EObjectLifetimeMutation kind) { }
}
