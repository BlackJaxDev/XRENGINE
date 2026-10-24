using System.ComponentModel;
using MemoryPack;
using YamlDotNet.Serialization;

namespace XREngine.Data.Core;

public abstract partial class XRObjectBase
{
    private long _lifetimeRevision;

    /// <summary>Identifies admitted lifetime operations. Generate never resets this value.</summary>
    [YamlIgnore, MemoryPackIgnore, Browsable(false)]
    internal long LifetimeRevision => Volatile.Read(ref _lifetimeRevision);

    internal void EnterLifetimeMutation(EObjectLifetimeMutation kind)
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
        }
        catch
        {
            OnLifetimeMutationEnded(kind);
            throw;
        }
    }
    internal void ExitLifetimeMutation(EObjectLifetimeMutation kind) => OnLifetimeMutationEnded(kind);

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
