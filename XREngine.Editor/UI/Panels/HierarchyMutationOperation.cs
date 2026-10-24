using System.Numerics;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Applies and compensates one captured hierarchy placement without taking a new owner's links.</summary>
internal sealed class HierarchyMutationOperation
{
    private readonly HierarchyTransformState _beforeTransform;
    private HierarchyTransformState? _afterTransform;
    private readonly bool _beforeActive;
    private bool _afterActive;
    private readonly bool _preserveWorld;
    private Matrix4x4 _worldMatrix;
    private RuntimeHierarchyRootRoutes _currentRoutes;
    private IReadOnlyList<XRScene> _currentEditorRoutes;
    private HierarchyPlacement _currentScenePlacement;
    private bool _incompleteFailure;
    private bool _applyComplete;
    private HierarchyMutationFailureState? _initialFailure;
    private HierarchyMutationFailureState? _undoFailure;
    private HierarchyMutationFailureState? _redoFailure;
    private bool? _uncertainActiveValue;
    private bool _uncertainLocalValues;
    private readonly Action? _requireIdentity;
    private IUndoPropertyReplayGuard? _propertyReplayGuard;

    internal void SetPropertyReplayGuard(IUndoPropertyReplayGuard guard)
    {
        if (_propertyReplayGuard is not null)
            throw new InvalidOperationException("The hierarchy property replay guard is already assigned.");
        _propertyReplayGuard = guard;
    }

    public HierarchyMutationOperation(HierarchyPlacement before, HierarchyPlacement destination,
        bool preserveWorld, bool? activeAfter = null, Action? requireIdentity = null)
    {
        _requireIdentity = requireIdentity;
        _requireIdentity?.Invoke();
        Before = before;
        After = destination;
        _beforeTransform = new(before.Transform);
        _beforeActive = before.Node.IsActiveSelf;
        _afterActive = activeAfter ?? _beforeActive;
        _preserveWorld = preserveWorld;
        _currentRoutes = before.RuntimeRoutes;
        _currentEditorRoutes = before.EditorRoutes;
        _currentScenePlacement = before;
        _requireIdentity?.Invoke();
    }

    public void PrepareInitial()
    {
        RequireRepairOwner(Before, After);
        RequireBefore(includeProperties: true);
        if (_preserveWorld)
        {
            HierarchyTransformState.RefreshAncestors(Before.Transform, () => RequireRepairOwner(Before, After));
            RequireRepairOwner(Before, After);
            RequireBefore(includeProperties: true);
            if (After.Parent is not null)
            {
                HierarchyTransformState.RefreshAncestors(After.Parent, () => RequireRepairOwner(Before, After));
                RequireRepairOwner(Before, After);
                RequireBefore(includeProperties: true);
            }
        }
        _worldMatrix = Before.Transform.WorldMatrix;
    }

    public HierarchyPlacement Before { get; }
    public HierarchyPlacement After { get; private set; }
    public SceneNode Node => Before.Node;

    public void ReconcileProperties(StructuralChangeHandle handle)
    {
        try
        {
            RequireRepairOwner(Before, After);
            if (!(Before.Matches() && Node.IsActiveSelf == _beforeActive && _beforeTransform.Matches()))
            {
                if (_incompleteFailure)
                    RequireFailureState(_initialFailure);
                else
                    RequireAfter(includeProperties: true);
            }
        }
        catch (InvalidOperationException) { return; }
        // Activation must follow structural attachment in both directions.
        handle.RemoveOwnedProperty(Node, nameof(SceneNode.IsActiveSelf));
        _beforeTransform.ReconcileProperties(handle, _propertyReplayGuard);
    }

    public void RequireBefore(bool includeProperties)
    {
        _requireIdentity?.Invoke();
        if (!Before.Matches() || includeProperties &&
            (Node.IsActiveSelf != _beforeActive || !_beforeTransform.Matches()))
            throw new InvalidOperationException("The hierarchy edit's original owner or values changed.");
    }

    public void RequireAfter(bool includeProperties)
    {
        _requireIdentity?.Invoke();
        if (!After.Matches() || includeProperties &&
            (Node.IsActiveSelf != _afterActive || _afterTransform is null || !_afterTransform.Matches()))
            throw new InvalidOperationException("The hierarchy edit's destination owner or values changed.");
    }

    public void ApplyInitial()
    {
        RequireBefore(includeProperties: true);
        MoveTo(After, Before, _preserveWorld);
        SetActive(_afterActive);
        RequireAfter(includeProperties: false);
        if (_preserveWorld)
            HierarchyTransformState.RefreshAncestors(Before.Transform, () => RequireAfter(includeProperties: false));
        RequireAfter(includeProperties: false);
        if (Node.IsActiveSelf != _afterActive || _preserveWorld && !Near(_worldMatrix, Before.Transform.WorldMatrix))
            throw new InvalidOperationException("The hierarchy edit did not preserve its requested values.");
        After = HierarchyPlacement.Capture(Node, Before.World);
        _afterTransform = new(Before.Transform);
        _applyComplete = true;
    }

    /// <summary>
    /// Gets whether the last <see cref="RestoreInitial"/> found a newer edit from a callback and
    /// kept it instead of restoring the original values.
    /// </summary>
    public bool PreservedNewerEdit { get; private set; }

    public void RequireUndoEntry()
    {
        if (_uncertainLocalValues)
            throw new InvalidOperationException("A hierarchy value callback did not complete. Recover or reissue the edit explicitly.");
        if (_undoFailure is not null)
            RequireFailureState(_undoFailure);
        else if (_incompleteFailure)
            RequireFailureState(_initialFailure);
        else
            RequireAfter(includeProperties: true);
    }

    public void UndoMembership()
    {
        try
        {
            MoveTo(Before, After, preserveWorld: false);
            RestoreProperties(Before, _beforeTransform, _beforeActive);
            MarkDirtyForReplay();
            RequireBefore(includeProperties: true);
            _undoFailure = null;
        }
        catch
        {
            _undoFailure = TryCaptureFailureState();
            throw;
        }
    }

    public void RedoMembership()
    {
        if (_uncertainLocalValues)
            throw new InvalidOperationException("A hierarchy value callback did not complete. Recover or reissue the edit explicitly.");
        // An incomplete callback failure can be recovered, but it is not a valid
        // scene placement that can be applied again.
        if (_incompleteFailure)
            throw new InvalidOperationException("The incomplete hierarchy edit must be issued again after recovery.");
        if (_redoFailure is not null)
            RequireFailureState(_redoFailure);
        else
            RequireBefore(includeProperties: true);
        try { MoveTo(After, Before, preserveWorld: false); }
        catch
        {
            _redoFailure = TryCaptureFailureState();
            throw;
        }
    }

    public void CompleteRedo()
    {
        if (_uncertainLocalValues)
            throw new InvalidOperationException("A hierarchy value callback did not complete. Recover or reissue the edit explicitly.");
        try
        {
            RequireAfter(includeProperties: false);
            RestoreProperties(After, _afterTransform!, _afterActive);
            MarkDirtyForReplay();
            RequireAfter(includeProperties: true);
            _redoFailure = null;
        }
        catch
        {
            _redoFailure = TryCaptureFailureState();
            throw;
        }
    }

    /// <summary>
    /// Compensates a failed initial edit. Before the apply completes, any partial state is this
    /// edit's own write and is repaired. After it completes, a placement, activation, or local
    /// value that differs from the applied result was written by a callback. Restoring it would
    /// discard that newer edit, so the method keeps it and returns false for recovery.
    /// </summary>
    public bool RestoreInitial()
    {
        PreservedNewerEdit = false;
        if (_applyComplete && !IsUnchangedSinceApply())
        {
            PreservedNewerEdit = true;
            return false;
        }
        try { MoveTo(Before, After, preserveWorld: false); }
        catch { }
        try { RestoreProperties(Before, _beforeTransform, _beforeActive); }
        catch { }
        try { _requireIdentity?.Invoke(); }
        catch { return false; }
        return !_uncertainLocalValues && _uncertainActiveValue is null && Before.Matches() &&
            Node.IsActiveSelf == _beforeActive && _beforeTransform.Matches();
    }

    private bool IsUnchangedSinceApply()
    {
        try
        {
            _requireIdentity?.Invoke();
            return !_uncertainLocalValues && _uncertainActiveValue is null && After.Matches() &&
                Node.IsActiveSelf == _afterActive && _afterTransform?.Matches() == true;
        }
        catch { return false; }
    }

    public void CaptureFailure()
    {
        _incompleteFailure = true;
        _initialFailure = TryCaptureFailureState();
        try
        {
            RequireRepairOwner(Before, After);
            HierarchyPlacement remaining = HierarchyPlacement.Capture(Node, Before.World);
            if (!remaining.Matches() || _uncertainLocalValues || _uncertainActiveValue is not null)
                return;
            After = remaining;
            _afterActive = Node.IsActiveSelf;
            _afterTransform = new(Before.Transform);
            _incompleteFailure = false;
        }
        catch { }
    }

    public void MarkDirty()
    {
        Exception? failure = null;
        try { Before.MarkDirty(); }
        catch (Exception exception) { failure = exception; }
        try { After.MarkDirty(); }
        catch (Exception exception) { failure ??= exception; }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void MarkDirtyForReplay()
    {
        try { MarkDirty(); }
        catch (Exception exception) { Debug.LogException(exception, "A hierarchy dirty-state observer failed during Undo/Redo."); }
    }

    private void RestoreProperties(HierarchyPlacement placement, HierarchyTransformState values, bool active)
    {
        void RequirePlacement()
        {
            RequireRepairOwner(Before, After);
            if (!placement.Matches())
                throw new InvalidOperationException("A hierarchy value callback changed its owner.");
        }
        RequirePlacement();
        try { values.Restore(RequirePlacement); }
        catch
        {
            _uncertainLocalValues = true;
            throw;
        }
        RequirePlacement();
        SetActive(active);
        RequirePlacement();
        if (Node.IsActiveSelf != active || !values.Matches() || _uncertainActiveValue is not null)
            throw new InvalidOperationException("A hierarchy value could not be restored.");
    }

    private void SetActive(bool active)
    {
        bool previous = Node.IsActiveSelf;
        try { Node.IsActiveSelf = active; }
        catch
        {
            if (Node.IsActiveSelf != previous)
                _uncertainActiveValue = Node.IsActiveSelf;
            throw;
        }
        if (previous != active && Node.IsActiveSelf == active)
            _uncertainActiveValue = null;
    }

    private HierarchyMutationFailureState? TryCaptureFailureState()
    {
        try
        {
            RequireRepairOwner(Before, After);
            return new(Before, After);
        }
        catch { return null; }
    }

    private void RequireFailureState(HierarchyMutationFailureState? state)
    {
        RequireRepairOwner(Before, After);
        if (state?.Matches() != true)
            throw new InvalidOperationException("The incomplete hierarchy edit changed before recovery.");
    }

    private void RequireRepairOwner(HierarchyPlacement goal, HierarchyPlacement other)
    {
        _requireIdentity?.Invoke();
        if (!goal.CanRepairWith(other) || !_currentScenePlacement.HasExactScenes() ||
            !goal.World.HasHierarchyRootRoutes(_currentRoutes) ||
            goal.Integration?.HasHierarchySourceRoutes(Node, _currentEditorRoutes) == false)
            throw new InvalidOperationException("A hierarchy callback changed the captured owner.");
    }

    private void RestoreListsAndRoutes(HierarchyPlacement goal, HierarchyPlacement other)
    {
        goal.RestoreListsAndRoutes(other, _currentRoutes, _currentEditorRoutes);
        _currentRoutes = goal.RuntimeRoutes;
        _currentEditorRoutes = goal.EditorRoutes;
        _currentScenePlacement = goal;
    }

    private void MoveTo(HierarchyPlacement goal, HierarchyPlacement other, bool preserveWorld)
    {
        RequireRepairOwner(goal, other);
        SceneNode node = goal.Node;
        RuntimeWorld world = goal.World;
        if (goal.RuntimeRoutes.Scenes.Count == 0)
            RestoreListsAndRoutes(goal, other);

        if (world.RootNodes.Contains(node) && (!goal.InWorldRoots || !goal.IsPublishedRoot()))
        {
            world.RootNodes.Remove(node);
            RequireRepairOwner(goal, other);
        }

        if (!ReferenceEquals(goal.Transform.Parent, goal.Parent))
        {
            goal.Transform.SetParent(goal.Parent, preserveWorld, EParentAssignmentMode.Immediate);
            RequireRepairOwner(goal, other);
        }
        if (goal.Parent is not null && !goal.Parent.Children.Contains(goal.Transform))
        {
            goal.Parent.Children.Add(goal.Transform);
            RequireRepairOwner(goal, other);
        }
        if (!ReferenceEquals(goal.Transform.Parent, goal.Parent))
            throw new InvalidOperationException("A hierarchy parent assignment was refused.");

        RestoreListsAndRoutes(goal, other);
        if (goal.InWorldRoots)
        {
            if (!world.RootNodes.Contains(node))
                world.RootNodes.Add(node);
        }
        else
            node.SetWorldContext(goal.Parent?.World);
        RequireRepairOwner(goal, other);
        if (!goal.Matches() || other.Parent is not null && !ReferenceEquals(other.Parent, goal.Parent) &&
            other.Parent.Children.Contains(goal.Transform))
            throw new InvalidOperationException("The hierarchy placement did not reach its complete destination.");
    }

    private static bool Near(Matrix4x4 left, Matrix4x4 right)
    {
        const float epsilon = 0.0001f;
        return MathF.Abs(left.M11 - right.M11) <= epsilon && MathF.Abs(left.M12 - right.M12) <= epsilon &&
            MathF.Abs(left.M13 - right.M13) <= epsilon && MathF.Abs(left.M14 - right.M14) <= epsilon &&
            MathF.Abs(left.M21 - right.M21) <= epsilon && MathF.Abs(left.M22 - right.M22) <= epsilon &&
            MathF.Abs(left.M23 - right.M23) <= epsilon && MathF.Abs(left.M24 - right.M24) <= epsilon &&
            MathF.Abs(left.M31 - right.M31) <= epsilon && MathF.Abs(left.M32 - right.M32) <= epsilon &&
            MathF.Abs(left.M33 - right.M33) <= epsilon && MathF.Abs(left.M34 - right.M34) <= epsilon &&
            MathF.Abs(left.M41 - right.M41) <= epsilon && MathF.Abs(left.M42 - right.M42) <= epsilon &&
            MathF.Abs(left.M43 - right.M43) <= epsilon && MathF.Abs(left.M44 - right.M44) <= epsilon;
    }
}
