using System.Collections;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene;

/// <summary>Owns a scene's root links without exposing mutable backing storage.</summary>
public sealed class SceneRootCollection : IList<SceneNode>, IReadOnlyList<SceneNode>
{
    internal static readonly Lock MutationGate = new();
    private readonly List<SceneNode> _nodes;
    private bool _current;
    private ulong _revision;

    internal SceneRootCollection(XRScene owner, IEnumerable<SceneNode>? values = null)
    {
        Owner = owner;
        _nodes = values is null ? [] : [.. values];
    }

    public XRScene Owner { get; }
    internal ulong Revision
    {
        get { lock (MutationGate) return _revision; }
    }
    public bool IsReadOnly => false;
    public bool IsCurrent
    {
        get { lock (MutationGate) return _current && Owner.HasSceneRoots(this); }
    }
    public int Count
    {
        get { lock (MutationGate) return _nodes.Count; }
    }

    public SceneNode this[int index]
    {
        get { lock (MutationGate) return _nodes[index]; }
        set
        {
            lock (MutationGate)
            {
                RequireCurrent();
                RequireAdmission(value);
                SceneNode previous = _nodes[index];
                previous.RequireStructuralMutation();
                if (ReferenceEquals(previous, value))
                    return;
                value.PrepareSceneRootOccurrence(this);
                value.AddSceneRootOccurrence(this);
                previous.RemoveSceneRootOccurrence(this);
                _nodes[index] = value;
                _revision++;
            }
        }
    }

    public void Add(SceneNode node)
    {
        lock (MutationGate)
        {
            RequireCurrent();
            RequireAdmission(node);
            _nodes.EnsureCapacity(checked(_nodes.Count + 1));
            node.PrepareSceneRootOccurrence(this);
            node.AddSceneRootOccurrence(this);
            _nodes.Add(node);
            _revision++;
        }
    }

    public void AddRange(IEnumerable<SceneNode> nodes) => InsertRangeCore(null, nodes);
    public void InsertRange(int index, IEnumerable<SceneNode> nodes) => InsertRangeCore(index, nodes);

    private void InsertRangeCore(int? requestedIndex, IEnumerable<SceneNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ulong revision;
        lock (MutationGate)
        {
            RequireCurrent();
            revision = _revision;
        }
        // Enumeration can run application code. Do it before the callback-free write transaction.
        SceneNode[] added = [.. nodes];
        lock (MutationGate)
        {
            RequireCurrent();
            if (_revision != revision)
                throw new InvalidOperationException("The scene roots changed while a range was enumerated.");
            int index = requestedIndex ?? _nodes.Count;
            if ((uint)index > (uint)_nodes.Count)
                throw new ArgumentOutOfRangeException(nameof(requestedIndex));
            foreach (SceneNode node in added)
                RequireAdmission(node);
            _nodes.EnsureCapacity(checked(_nodes.Count + added.Length));
            foreach (SceneNode node in added)
                node.PrepareSceneRootOccurrence(this);
            foreach (SceneNode node in added)
                node.AddSceneRootOccurrence(this);
            _nodes.InsertRange(index, added);
            if (added.Length > 0)
                _revision++;
        }
    }

    public void Insert(int index, SceneNode node)
    {
        lock (MutationGate)
        {
            RequireCurrent();
            RequireAdmission(node);
            if ((uint)index > (uint)_nodes.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            _nodes.EnsureCapacity(checked(_nodes.Count + 1));
            node.PrepareSceneRootOccurrence(this);
            node.AddSceneRootOccurrence(this);
            _nodes.Insert(index, node);
            _revision++;
        }
    }

    public bool Remove(SceneNode node)
    {
        lock (MutationGate)
        {
            RequireCurrent();
            int index = IndexOfCore(node);
            if (index < 0)
                return false;
            RemoveAtCore(index);
            return true;
        }
    }

    public void RemoveAt(int index)
    {
        lock (MutationGate)
        {
            RequireCurrent();
            RemoveAtCore(index);
        }
    }

    private void RemoveAtCore(int index)
    {
        SceneNode node = _nodes[index];
        node.RequireStructuralMutation();
        node.RemoveSceneRootOccurrence(this);
        _nodes.RemoveAt(index);
        _revision++;
    }

    public void Clear()
    {
        lock (MutationGate)
        {
            RequireCurrent();
            ValidateRelease();
            foreach (SceneNode node in _nodes)
                node.RemoveSceneRootOccurrence(this);
            _nodes.Clear();
            _revision++;
        }
    }

    public bool Contains(SceneNode node) => IndexOf(node) >= 0;
    public int IndexOf(SceneNode node)
    {
        lock (MutationGate)
            return IndexOfCore(node);
    }
    private int IndexOfCore(SceneNode node)
    {
        for (int index = 0; index < _nodes.Count; index++)
            if (ReferenceEquals(_nodes[index], node))
                return index;
        return -1;
    }

    public void CopyTo(SceneNode[] array, int arrayIndex)
    {
        lock (MutationGate)
            _nodes.CopyTo(array, arrayIndex);
    }

    /// <summary>Creates a serialization copy. Changes to the copy do not change scene ownership.</summary>
    internal List<SceneNode> ToSerializationList()
    {
        lock (MutationGate)
            return [.. _nodes];
    }

    public List<SceneNode>.Enumerator GetEnumerator() => _nodes.GetEnumerator();
    IEnumerator<SceneNode> IEnumerable<SceneNode>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void ValidatePending()
    {
        foreach (SceneNode node in _nodes)
            RequireAdmission(node);
    }

    internal void Attach()
    {
        ValidatePending();
        foreach (SceneNode node in _nodes)
            node.PrepareSceneRootOccurrence(this);
        foreach (SceneNode node in _nodes)
            node.AddSceneRootOccurrence(this);
        _current = true;
    }

    internal void Retire()
    {
        if (!_current)
            return;
        ValidateRelease();
        foreach (SceneNode node in _nodes)
            node.RemoveSceneRootOccurrence(this);
        _current = false;
        _revision++;
    }

    private static void RequireAdmission(SceneNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.RequireStructuralMutation();
        HierarchyConstructionScope.RequireSceneRootAdmission(node);
    }

    internal void ValidateRelease()
    {
        foreach (SceneNode node in _nodes)
            node.RequireStructuralMutation();
    }

    internal void RequireCurrent()
    {
        if (!_current || !Owner.HasSceneRoots(this))
            throw new InvalidOperationException("The scene root collection no longer belongs to its live scene.");
    }
}
