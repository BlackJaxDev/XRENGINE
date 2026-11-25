using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor;

public partial class HierarchyPanel
{
    private string _renameDraft = string.Empty;
    private ulong _renameSession;
    private bool _renameFocusRequested;

    private void BeginRename(SceneNode? node)
    {
        if (!CanUseHierarchy || node is null || node.IsDestroyed || node.IsDestroyQueued ||
            !_hierarchyIds.TryGetValue(node.ID, out ulong id) ||
            !_hierarchyNodes.TryGetValue(id, out SceneNode? current) || !ReferenceEquals(current, node))
            return;
        _rowFocusReceipt = null;
        if (!ReferenceEquals(_nodePendingRename, node))
        {
            _nodePendingRename = node;
            _renameDraft = node.Name ?? string.Empty;
            _renameSession++;
        }
        _renameFocusRequested = true;
        ShowVirtualRename(node);
        if (CanUseHierarchy && ReferenceEquals(_nodePendingRename, node))
        {
            SaveHierarchyState();
            ScheduleHierarchyWork();
        }
    }

    private void ApplyRename(UITextInputComponent input)
    {
        if (!CanUseHierarchy || _nodePendingRename is not { } node || !IsCurrentRenameInput(input))
            return;
        string name = input.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = SceneNode.DefaultName;
        uint revision = _hierarchyRevision;
        FinishRename(restoreFocus: true);
        if (node.Name == name || revision != _hierarchyRevision ||
            TryGetActiveWorld() is not { } world || !IsCurrentHierarchyWorld(world) || !IsLiveHierarchyNode(node, world))
            return;
        EnqueueSceneEdit(() =>
        {
            if (revision != _hierarchyRevision || !IsCurrentHierarchyWorld(world) || !IsLiveHierarchyNode(node, world))
                return;
            using var change = Undo.TrackChange("Rename Node", node);
            node.Name = name;
            MarkSceneHierarchyDirty(node, world);
        });
    }

    private bool IsCurrentRenameInput(UITextInputComponent input)
    {
        if (_nodePendingRename is null)
            return false;
        foreach (HierarchyRow row in _hierarchyRows.Values)
            if (ReferenceEquals(row.RenameInput, input) && IsHierarchyRenameRowCurrent(row) &&
                row.RenameSession == _renameSession && ReferenceEquals(row.Node, _nodePendingRename))
                return true;
        return false;
    }

    private void CancelRename()
    {
        if (CanUseHierarchy)
            FinishRename(restoreFocus: true);
    }

    private void FinishRename(bool restoreFocus)
    {
        if (_nodePendingRename is null)
            return;
        if (_rowFocusReceipt?.Rename is not null)
            _rowFocusReceipt = null;
        IUICanvasInputSource? input = GetCanvasInput();
        HierarchyRow? editingRow = null;
        foreach (HierarchyRow row in _hierarchyRows.Values)
            if (IsHierarchyRenameRowCurrent(row) && row.RenameSession == _renameSession &&
                ReferenceEquals(row.Node, _nodePendingRename))
            {
                editingRow = row;
                break;
            }
        _nodePendingRename = null;
        _renameDraft = string.Empty;
        _renameFocusRequested = false;
        ulong finishedSession = ++_renameSession;
        RefreshVisibleRenameRows();
        if (!CanUseHierarchy || _renameSession != finishedSession)
            return;
        if (input is not null && editingRow is not null && IsHierarchyRenameRowCurrent(editingRow) &&
            ReferenceEquals(input.FocusedComponent, editingRow.RenameInput) &&
            (!restoreFocus || !input.TryFocusPresented(editingRow.Button)) &&
            CanUseHierarchy && _renameSession == finishedSession && IsHierarchyRenameRowCurrent(editingRow) &&
            ReferenceEquals(input.FocusedComponent, editingRow.RenameInput))
            input.FocusedComponent = null;
        if (CanUseHierarchy && _renameSession == finishedSession)
            SaveHierarchyState();
    }

    private bool IsHierarchyRenameRowCurrent(HierarchyRow row)
        => IsHierarchyRowCurrent(row) && _treeListTransform is { } list &&
            ReferenceEquals(row.Transform.Parent, list) &&
            row.RenameInput is { IsDestroyed: false, IsDestroyQueued: false,
                SceneNode: { IsDestroyed: false, IsDestroyQueued: false, IsTransformNull: false } node } &&
            row.RenameTransform is { IsDestroyed: false, IsDestroyQueued: false } &&
            row.Transform is { IsDestroyed: false, IsDestroyQueued: false,
                SceneNode: { IsDestroyed: false, IsDestroyQueued: false, IsTransformNull: false } rowNode } &&
            node.Components.Contains(row.RenameInput) &&
            ReferenceEquals(node.Transform, row.RenameTransform) &&
            ReferenceEquals(row.RenameTransform.SceneNode, node) &&
            ReferenceEquals(row.RenameTransform.Parent, row.Transform) &&
            ReferenceEquals(rowNode.Transform, row.Transform);

    private void BindRenameSession(HierarchyRow row, SceneNode? node)
    {
        if (!IsHierarchyRenameRowCurrent(row) ||
            row.LabelTransform.SceneNode is not { IsDestroyed: false, IsDestroyQueued: false, IsTransformNull: false } labelNode)
            return;
        var owner = (
            Input: ObjectConstructionIdentity.Capture(row.RenameInput),
            InputNode: ObjectConstructionIdentity.Capture(row.RenameInput.SceneNode),
            Transform: ObjectConstructionIdentity.Capture(row.RenameTransform),
            Row: ObjectConstructionIdentity.Capture(row.Transform),
            RowNode: ObjectConstructionIdentity.Capture(row.Transform.SceneNode!),
            Label: ObjectConstructionIdentity.Capture(row.LabelTransform),
            LabelNode: ObjectConstructionIdentity.Capture(labelNode));
        var state = (Panel: this, Row: row, Source: node, Session: _renameSession, Owner: owner,
            LabelIntent: row.LabelTransform.VisibilityWriteIntentRevision,
            RenameIntent: row.RenameTransform.VisibilityWriteIntentRevision);
        static bool Current((HierarchyPanel Panel, HierarchyRow Row, SceneNode? Source, ulong Session,
            (ObjectConstructionIdentity Input, ObjectConstructionIdentity InputNode,
                ObjectConstructionIdentity Transform, ObjectConstructionIdentity Row,
                ObjectConstructionIdentity RowNode, ObjectConstructionIdentity Label,
                ObjectConstructionIdentity LabelNode) Owner, ulong LabelIntent, ulong RenameIntent) binding)
            => binding.Panel.IsHierarchyRenameRowCurrent(binding.Row) &&
                binding.Panel._renameSession == binding.Session && ReferenceEquals(binding.Row.Node, binding.Source) &&
                binding.Owner.Input.IsCurrent && binding.Owner.InputNode.IsCurrent && binding.Owner.Transform.IsCurrent &&
                binding.Owner.Row.IsCurrent && binding.Owner.RowNode.IsCurrent && binding.Owner.Label.IsCurrent &&
                binding.Owner.LabelNode.IsCurrent &&
                ReferenceEquals(binding.Row.RenameInput, binding.Owner.Input.Target) &&
                ReferenceEquals(binding.Row.RenameInput.SceneNode, binding.Owner.InputNode.Target) &&
                ReferenceEquals(binding.Row.RenameTransform, binding.Owner.Transform.Target) &&
                ReferenceEquals(binding.Row.Transform, binding.Owner.Row.Target) &&
                ReferenceEquals(binding.Row.Transform.SceneNode, binding.Owner.RowNode.Target) &&
                binding.Row.LabelTransform is { IsDestroyed: false, IsDestroyQueued: false,
                    SceneNode: { IsDestroyed: false, IsDestroyQueued: false, IsTransformNull: false } currentLabelNode } &&
                ReferenceEquals(binding.Row.LabelTransform, binding.Owner.Label.Target) &&
                ReferenceEquals(currentLabelNode, binding.Owner.LabelNode.Target) &&
                ReferenceEquals(currentLabelNode.Transform, binding.Row.LabelTransform) &&
                ReferenceEquals(binding.Row.LabelTransform.Parent, binding.Row.Transform) &&
                binding.Row.LabelTransform.VisibilityWriteIntentRevision == binding.LabelIntent &&
                binding.Row.RenameTransform.VisibilityWriteIntentRevision == binding.RenameIntent;
        static void Require((HierarchyPanel Panel, HierarchyRow Row, SceneNode? Source, ulong Session,
            (ObjectConstructionIdentity Input, ObjectConstructionIdentity InputNode,
                ObjectConstructionIdentity Transform, ObjectConstructionIdentity Row,
                ObjectConstructionIdentity RowNode, ObjectConstructionIdentity Label,
                ObjectConstructionIdentity LabelNode) Owner, ulong LabelIntent, ulong RenameIntent) binding)
        {
            if (!Current(binding))
                throw new InvalidOperationException("The hierarchy rename binding owner changed.");
        }
        if (!Current(state))
            return;
        if (_rowFocusReceipt is { Rename: { Prepared: false } draft } receipt &&
            ReferenceEquals(node, receipt.Source))
            draft.CaptureDestination(row.RenameInput);
        bool editing = node is not null && ReferenceEquals(_nodePendingRename, node);
        state.LabelIntent = checked(state.LabelIntent + 1);
        row.LabelTransform.SetVisibilityGuarded(editing ? EVisibility.Hidden : EVisibility.Visible, state, Require);
        if (!Current(state))
            return;
        state.RenameIntent = checked(state.RenameIntent + 1);
        row.RenameTransform.SetVisibilityGuarded(editing ? EVisibility.Visible : EVisibility.Hidden, state, Require);
        if (!Current(state))
            return;
        if (!editing)
        {
            row.RenameSession = 0;
            return;
        }
        if (row.RenameSession != state.Session)
        {
            row.RenameSession = state.Session;
            row.RenameInput.SetTextGuarded(_renameDraft, state, Require);
        }
        if (Current(state) && _renameFocusRequested)
            ScheduleHierarchyWork();
    }

    private void UpdateRenameDraft(HierarchyRow row)
    {
        if (IsHierarchyRenameRowCurrent(row) && row.RenameSession == _renameSession &&
            ReferenceEquals(row.Node, _nodePendingRename))
            _renameDraft = row.RenameInput.Text;
    }

    private void ReconcileRenameSource()
    {
        if (_nodePendingRename is { } node &&
            (node.IsDestroyed || node.IsDestroyQueued ||
             !_hierarchyIds.TryGetValue(node.ID, out ulong id) ||
             !_hierarchyNodes.TryGetValue(id, out SceneNode? current) || !ReferenceEquals(current, node)))
            FinishRename(restoreFocus: false);
    }

    private void CompleteRenameFocus()
    {
        if (!_renameFocusRequested || !CanUseHierarchy || _nodePendingRename is null)
            return;
        ulong session = _renameSession;
        IUICanvasInputSource? input = GetCanvasInput();
        if (input is null)
        {
            _renameFocusRequested = false;
            return;
        }
        foreach (HierarchyRow row in _hierarchyRows.Values)
        {
            if (!IsHierarchyRenameRowCurrent(row) || row.RenameSession != session ||
                !ReferenceEquals(row.Node, _nodePendingRename))
                continue;
            bool focused = input.TryFocusPresented(row.RenameInput);
            if (!CanUseHierarchy || session != _renameSession || !IsHierarchyRenameRowCurrent(row))
                return;
            if (focused)
                _renameFocusRequested = false;
            break;
        }
        if (_renameFocusRequested && CanUseHierarchy)
            ScheduleHierarchyWork();
    }
}
