namespace XREngine.Data.Core;

public abstract partial class XRObjectBase
{
    internal void EnterLifetimeMutation(EObjectLifetimeMutation kind) => OnLifetimeMutationBeginning(kind);
    internal void ExitLifetimeMutation(EObjectLifetimeMutation kind) => OnLifetimeMutationEnded(kind);

    /// <summary>
    /// Admits a lifetime operation before its field, queue, or cache effects.
    /// Overrides must be nonblocking and must not call application callbacks.
    /// The default path needs no allocation or synchronization.
    /// </summary>
    protected virtual void OnLifetimeMutationBeginning(EObjectLifetimeMutation kind) { }

    /// <summary>Releases admission after all operation callbacks and cache effects finish.</summary>
    protected virtual void OnLifetimeMutationEnded(EObjectLifetimeMutation kind) { }
}
