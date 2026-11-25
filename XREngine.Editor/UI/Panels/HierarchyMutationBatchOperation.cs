using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Runs an ordered clone forest through one structural history action.</summary>
internal sealed class HierarchyMutationBatchOperation : IHierarchyMutationWork
{
    private readonly HierarchyMutationOperation[] _members;
    private readonly HierarchyPlacement[] _before;
    private readonly HierarchyBatchReplayExpectation _values;
    private readonly bool[] _beforeActive;
    private readonly bool[] _expectedActive;
    private readonly bool[] _afterActive;
    private readonly string?[] _names;
    private readonly bool[] _placed;
    private readonly SceneNode[] _nodes;
    private readonly Action _requireIdentity;
    private int _changing = -1;
    private bool _replaying;
    private HierarchyCloneCustody? _custody;

    internal HierarchyMutationBatchOperation(IReadOnlyList<SceneNode> nodes,
        IReadOnlyList<HierarchyPlacement> before, IReadOnlyList<HierarchyPlacement> after,
        IReadOnlyList<bool> activeAfter, Action requireIdentity)
    {
        if (nodes.Count < 2 || nodes.Count != before.Count || nodes.Count != after.Count ||
            nodes.Count != activeAfter.Count)
            throw new InvalidOperationException("A batch requires matching ordered clone roots and placements.");
        _nodes = [.. nodes];
        _before = [.. before];
        _values = new(before.Select(placement => placement.Transform).ToArray());
        _beforeActive = new bool[nodes.Count];
        _expectedActive = new bool[nodes.Count];
        _afterActive = [.. activeAfter];
        _names = nodes.Select(node => node.Name).ToArray();
        _placed = new bool[nodes.Count];
        _members = new HierarchyMutationOperation[nodes.Count];
        _requireIdentity = requireIdentity;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (!ReferenceEquals(nodes[i], before[i].Node) || !ReferenceEquals(nodes[i], after[i].Node))
                throw new InvalidOperationException("The batch placement does not match its ordered clone.");
            _beforeActive[i] = nodes[i].IsActiveSelf;
            _expectedActive[i] = _beforeActive[i];
        }
        for (int i = 0; i < nodes.Count; i++)
        {
            int index = i;
            _members[i] = new(before[i], after[i], preserveWorld: false,
                activeAfter: activeAfter[i], requireIdentity: () => RequirePeers(index));
        }
        RequireBefore(true);
    }

    public IReadOnlyList<SceneNode> Nodes => _nodes;
    internal IReadOnlyList<HierarchyMutationOperation> Members => _members;

    internal void SetPropertyReplayGuard(HierarchyCloneCustody custody)
    {
        _custody = custody;
        IUndoPropertyReplayGuard guard = new HierarchyBatchPropertyReplayGuard(this, custody);
        foreach (HierarchyMutationOperation member in _members)
            member.SetPropertyReplayGuard(guard);
    }

    private void RequirePeers(int self)
    {
        _requireIdentity();
        for (int i = 0; i < _nodes.Length; i++)
        {
            if (i == self) continue;
            bool current = _placed[i] ? _members[i].After.Matches() : _before[i].Matches();
            if (!current || _nodes[i].IsActiveSelf != _expectedActive[i] ||
                _nodes[i].Name != _names[i] || !_values.Matches(i))
                throw new InvalidOperationException("A peer clone changed before the next owned write.");
        }
    }

    public void RequireBefore(bool includeProperties)
    {
        _requireIdentity();
        for (int i = 0; i < _members.Length; i++)
            _members[i].RequireBefore(includeProperties);
    }

    public void RequireAfter(bool includeProperties)
    {
        _requireIdentity();
        for (int i = 0; i < _members.Length; i++)
            _members[i].RequireAfter(includeProperties);
    }

    public void PrepareInitial()
    {
        RequireBefore(true);
        foreach (HierarchyMutationOperation member in _members)
        {
            member.PrepareInitial();
            RequireBefore(true);
        }
    }

    public void ApplyInitial()
    {
        for (int i = 0; i < _members.Length; i++)
        {
            _changing = i;
            RequirePeers(i);
            _members[i].ApplyInitial();
            _values.CaptureApplied(i);
            _expectedActive[i] = _afterActive[i];
            _placed[i] = true;
            _changing = -1;
            RequirePeers(-1);
        }
        RequireAfter(true);
    }

    public bool RestoreInitial()
    {
        bool restored = true;
        for (int i = _members.Length - 1; i >= 0; i--)
        {
            if (!_placed[i] && _changing != i) continue;
            try
            {
                RequirePeers(i);
                if (!_placed[i])
                {
                    try
                    {
                        _members[i].RequireBefore(includeProperties: true);
                        _changing = -1;
                        continue;
                    }
                    catch (InvalidOperationException) { }
                }
                if (!_members[i].RestoreInitial()) restored = false;
                else
                {
                    _placed[i] = false;
                    _values.RestoreBefore(i);
                    _expectedActive[i] = _beforeActive[i];
                    if (_changing == i) _changing = -1;
                }
                RequirePeers(-1);
            }
            catch { restored = false; break; }
            if (!restored) break;
        }
        if (!restored) return false;
        try { RequireBefore(true); return true; }
        catch { return false; }
    }

    public void CaptureFailure()
    {
        for (int i = 0; i < _members.Length; i++)
            if (_placed[i] || _changing == i)
                _members[i].CaptureFailure();
    }

    public void MarkDirty()
    {
        Exception? failure = null;
        for (int i = 0; i < _members.Length; i++)
        {
            try { RequirePeers(-1); _members[i].MarkDirty(); RequirePeers(-1); }
            catch (Exception exception) { failure ??= exception; break; }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal void MarkDirtyAfterFailedCompensation(HierarchyCloneDocument document)
    {
        Exception? failure = null;
        foreach (HierarchyMutationOperation member in _members)
        {
            try { member.Before.MarkDirtyCapturedDocument(document); }
            catch (Exception exception) { failure ??= exception; }
            try { member.After.MarkDirtyCapturedDocument(document); }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void ReconcileProperties(StructuralChangeHandle handle)
    {
        foreach (HierarchyMutationOperation member in _members)
            member.ReconcileProperties(handle);
    }

    public void RequireUndoEntry()
    {
        RequireAfter(true);
        foreach (HierarchyMutationOperation member in _members) member.RequireUndoEntry();
        _values.BeginReplay(undo: true);
        _replaying = true;
    }

    public void UndoMembership()
    {
        if (!_replaying) throw new InvalidOperationException("The batch has no admitted Undo replay.");
        _values.RequireComplete();
        for (int i = _members.Length - 1; i >= 0; i--)
        {
            RequirePeers(i);
            _members[i].UndoMembership();
            _placed[i] = false;
            _values.RestoreBefore(i);
            _expectedActive[i] = _beforeActive[i];
            RequirePeers(-1);
        }
        RequireBefore(true);
        _values.EndReplay();
        _replaying = false;
    }

    public void RedoMembership()
    {
        RequireBefore(true);
        for (int i = 0; i < _members.Length; i++)
        {
            RequirePeers(i);
            _members[i].RedoMembership();
            _placed[i] = true;
            RequirePeers(-1);
        }
        _values.BeginReplay(undo: false);
        _replaying = true;
    }

    public void CompleteRedo()
    {
        if (!_replaying) throw new InvalidOperationException("The batch has no admitted Redo replay.");
        _values.RequireComplete();
        for (int i = 0; i < _members.Length; i++)
        {
            RequirePeers(i);
            _members[i].CompleteRedo();
            _values.RestoreAfter(i);
            _expectedActive[i] = _afterActive[i];
            RequirePeers(-1);
        }
        RequireAfter(true);
        _values.EndReplay();
        _replaying = false;
    }

    internal void BeginPropertyReplay(TransformBase target, string propertyName)
    {
        _requireIdentity();
        if (!_replaying || _custody?.State is not (EHierarchyCloneCustodyState.Undoing or EHierarchyCloneCustodyState.Redoing))
            throw new InvalidOperationException("The clone forest has no admitted property replay.");
        RequirePeers(-1);
        _values.BeginProperty(target, propertyName);
    }

    internal void RequirePropertyReplay()
    {
        _requireIdentity();
        if (!_replaying || _custody?.State is not (EHierarchyCloneCustodyState.Undoing or EHierarchyCloneCustodyState.Redoing))
            throw new InvalidOperationException("The clone forest has no admitted property replay.");
        _values.RequireCurrent();
        RequirePeers(-1);
    }

    internal void EndPropertyReplay() => _values.EndProperty();

}
