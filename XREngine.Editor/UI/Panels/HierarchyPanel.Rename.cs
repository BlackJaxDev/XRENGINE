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
            if (ReferenceEquals(row.RenameInput, input) && IsHierarchyRowCurrent(row) &&
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
        IUICanvasInputSource? input = GetCanvasInput();
        HierarchyRow? editingRow = null;
        foreach (HierarchyRow row in _hierarchyRows.Values)
            if (row.RenameSession == _renameSession && ReferenceEquals(row.Node, _nodePendingRename))
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
        if (input is not null && editingRow is not null &&
            ReferenceEquals(input.FocusedComponent, editingRow.RenameInput) &&
            (!restoreFocus || !input.TryFocusPresented(editingRow.Button)) &&
            CanUseHierarchy && _renameSession == finishedSession &&
            ReferenceEquals(input.FocusedComponent, editingRow.RenameInput))
            input.FocusedComponent = null;
        if (CanUseHierarchy && _renameSession == finishedSession)
            SaveHierarchyState();
    }

    private void BindRenameSession(HierarchyRow row, SceneNode? node)
    {
        ulong session = _renameSession;
        bool editing = node is not null && ReferenceEquals(_nodePendingRename, node);
        row.LabelTransform.Visibility = editing ? EVisibility.Hidden : EVisibility.Visible;
        if (!IsHierarchyRowCurrent(row) || _renameSession != session || !ReferenceEquals(row.Node, node))
            return;
        row.RenameTransform.Visibility = editing ? EVisibility.Visible : EVisibility.Hidden;
        if (!IsHierarchyRowCurrent(row) || _renameSession != session || !ReferenceEquals(row.Node, node))
            return;
        if (!editing)
        {
            row.RenameSession = 0;
            return;
        }
        if (row.RenameSession != session)
        {
            row.RenameSession = session;
            row.RenameInput.Text = _renameDraft;
        }
        if (IsHierarchyRowCurrent(row) && _renameSession == session && _renameFocusRequested)
            ScheduleHierarchyWork();
    }

    private void UpdateRenameDraft(HierarchyRow row)
    {
        if (IsHierarchyRowCurrent(row) && row.RenameSession == _renameSession &&
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
            if (row.RenameSession != session || !ReferenceEquals(row.Node, _nodePendingRename))
                continue;
            bool focused = input.TryFocusPresented(row.RenameInput);
            if (!CanUseHierarchy || session != _renameSession)
                return;
            if (focused)
                _renameFocusRequested = false;
            break;
        }
        if (_renameFocusRequested && CanUseHierarchy)
            ScheduleHierarchyWork();
    }
}
