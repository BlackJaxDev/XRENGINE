namespace XREngine.Data.Core;

/// <summary>Keeps one object's lifetime admission active through callbacks and cache writes.</summary>
internal readonly struct ObjectLifetimeMutationLease : IDisposable
{
    private readonly XRObjectBase _owner;
    private readonly EObjectLifetimeMutation _kind;
    private readonly bool _usesDispatchClaim;

    internal ObjectLifetimeMutationLease(XRObjectBase owner, EObjectLifetimeMutation kind)
    {
        _owner = owner;
        _kind = kind;
        _usesDispatchClaim = owner.EnterLifetimeMutation(kind);
    }

    public void Dispose() => _owner.ExitLifetimeMutation(_kind, _usesDispatchClaim);
}
