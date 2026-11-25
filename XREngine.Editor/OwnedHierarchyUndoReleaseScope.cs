using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Excludes only synthetic delete records for exact nodes retired by one receipt.</summary>
internal sealed class OwnedHierarchyUndoReleaseScope : IDisposable
{
    [ThreadStatic]
    private static OwnedHierarchyUndoReleaseScope? _current;
    private readonly OwnedHierarchyUndoReleaseScope? _previous;
    private readonly HashSet<SceneNode> _nodes = new(ReferenceEqualityComparer.Instance);
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private bool _disposed;

    internal OwnedHierarchyUndoReleaseScope(ObjectConstructionOwnership ownership)
    {
        foreach (XRObjectBase allocation in ownership.Objects)
            if (allocation is SceneNode node)
                _nodes.Add(node);
        _previous = _current;
        _current = this;
    }

    internal static bool Contains(SceneNode node)
    {
        for (OwnedHierarchyUndoReleaseScope? scope = _current; scope is not null; scope = scope._previous)
            if (scope._nodes.Contains(node))
                return true;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (Environment.CurrentManagedThreadId != _ownerThread || !ReferenceEquals(_current, this))
            throw new InvalidOperationException("Owned hierarchy release scopes must close on their owner thread in reverse order.");
        _current = _previous;
        _disposed = true;
    }
}
