using XREngine.Data.Core;

namespace XREngine.Scene.Prefabs;

/// <summary>
/// Retains constructor allocations until exact detached graph cleanup succeeds.
/// Cleanup runs once. A rejected or incomplete attempt keeps its ledger and failure;
/// it never retries destruction or infers lifetime ownership from constructor origin alone.
/// The caller must retain this receipt while it holds allocations.
/// </summary>
public sealed class OwnedHierarchyLifetime
{
    private readonly Lock _sync = new();
    private ObjectConstructionOwnership? _ownership;
    private EOwnedHierarchyLifetimeState _state;
    private Exception? _failure;
    private OwnedHierarchyRetirement? _retirement;

    public OwnedHierarchyLifetime(ObjectConstructionOwnership ownership, Exception? constructionFailure = null)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        _ownership = ownership;
        ConstructionFailure = constructionFailure;
    }

    public Exception? ConstructionFailure { get; }
    public EOwnedHierarchyLifetimeState State { get { lock (_sync) return _state; } }
    public Exception? Failure { get { lock (_sync) return _failure; } }
    public ObjectConstructionOwnership? RetainedOwnership { get { lock (_sync) return _ownership; } }

    /// <summary>
    /// Attempts synchronous cleanup of an exact detached structural forest.
    /// Unknown allocations and foreign links reject the attempt before teardown.
    /// No lock spans application callbacks.
    /// </summary>
    public bool TryRetire()
    {
        ObjectConstructionOwnership ownership;
        lock (_sync)
        {
            if (_state != EOwnedHierarchyLifetimeState.Retained)
                return _state == EOwnedHierarchyLifetimeState.Retired;
            ownership = _ownership!;
            _state = EOwnedHierarchyLifetimeState.Retiring;
        }
        try
        {
            _retirement = new OwnedHierarchyRetirement(ownership);
            _retirement.Execute();
            lock (_sync)
            {
                _ownership = null;
                _retirement = null;
                _state = EOwnedHierarchyLifetimeState.Retired;
            }
            return true;
        }
        catch (Exception failure)
        {
            lock (_sync)
            {
                _failure = failure;
                _state = EOwnedHierarchyLifetimeState.RecoveryRequired;
            }
            return false;
        }
    }
}
