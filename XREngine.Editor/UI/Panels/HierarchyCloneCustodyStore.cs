namespace XREngine.Editor;

/// <summary>
/// Keeps clone receipts reachable through construction, history, and document custody.
/// An unresolved operation stops new construction; successful history has no new size limit.
/// </summary>
internal sealed class HierarchyCloneCustodyStore
{
    private readonly Lock _sync = new();
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly List<HierarchyCloneCustody> _entries = [];
    private HierarchyCloneCustody? _pending;

    internal static HierarchyCloneCustodyStore Shared { get; } = new();

    internal HierarchyCloneCustody Reserve(RuntimeWorld world)
    {
        RequireOwnerThread();
        lock (_sync)
        {
            if (_pending is not null)
                throw new InvalidOperationException("A clone construction is still in progress.");
            foreach (HierarchyCloneCustody entry in _entries)
            {
                if (entry.State == EHierarchyCloneCustodyState.Document && !entry.Document.IsCurrent())
                    entry.RetainFailure(new InvalidOperationException("The clone document ended before its retained allocations were released."));
                if (entry.BlocksConstruction)
                    throw new InvalidOperationException("A retained clone requires recovery before another clone can be allocated.", entry.Failure);
            }
            HierarchyCloneCustody custody = new(this, new(world));
            _entries.Add(custody);
            _pending = custody;
            return custody;
        }
    }

    internal HierarchyCloneCustody[] CaptureEntries()
    {
        lock (_sync) return [.. _entries];
    }

    internal void CompleteCommand(HierarchyCloneCustody custody)
    {
        RequireOwnerThread();
        lock (_sync)
        {
            if (!ReferenceEquals(_pending, custody))
                throw new InvalidOperationException("The clone command lost its exact construction reservation.");
            _pending = null;
        }
    }

    internal void Settle(HierarchyCloneCustody custody)
    {
        RequireOwnerThread();
        lock (_sync) _entries.Remove(custody);
    }

    internal void RequireOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Clone custody must be changed on its editor owner thread.");
    }
}
