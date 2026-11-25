using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public partial class HierarchyPanel
{
    private HierarchyRowFocusReceipt? _rowFocusReceipt;
    private bool _suppressSavedRenameFocus;

    private void CaptureHierarchyRowFocus(bool destroying)
    {
        if (destroying)
        {
            _rowFocusReceipt = null;
            return;
        }
        if (_hierarchyStopped || _releasingHierarchy)
            return;
        _suppressSavedRenameFocus = _nodePendingRename is not null;
        _rowFocusReceipt = null;
        if (SceneNode.IsTransformNull || SceneNode.Transform is not UIBoundableTransform panel ||
            GetCanvasInput() is not { FocusIntentRevision: not 0, HasPendingFocusRequest: false,
                FocusedComponent: { } focused } input ||
            input is not XRComponent inputComponent || inputComponent.SceneNode.IsTransformNull ||
            panel.GetCanvasComponent() is not { } canvas || canvas.SceneNode.IsTransformNull ||
            _treeListTransform is not { } list)
            return;
        foreach (HierarchyRow row in _hierarchyRows.Values)
        {
            bool rename = IsHierarchyRenameRowCurrent(row) && ReferenceEquals(row.RenameInput, focused) &&
                ReferenceEquals(row.Node, _nodePendingRename) && row.RenameSession == _renameSession &&
                !row.RenameInput.IsComposing && !row.RenameInput.CommitOnSubmit && row.RenameInput.SceneNode is { IsTransformNull: false } renameNode &&
                ReferenceEquals(renameNode.Transform, row.RenameTransform);
            if ((!ReferenceEquals(row.Button, focused) && !rename) || !IsHierarchyRowCurrent(row) ||
                row.Node is not { IsDestroyed: false, IsDestroyQueued: false, IsTransformNull: false } source ||
                !list.TryGetRealizedVirtualRow(row.ItemId, out UIBoundableTransform? current, out UIVirtualRowLease lease) ||
                !ReferenceEquals(current, row.Transform))
                continue;
            List<ObjectConstructionIdentity> identities = [];
            foreach (XRObjectBase owner in new XRObjectBase[] { this, SceneNode, panel,
                inputComponent, inputComponent.SceneNode, inputComponent.SceneNode.Transform,
                canvas, canvas.SceneNode, canvas.SceneNode.Transform, source, source.Transform })
                identities.Add(ObjectConstructionIdentity.Capture(owner));
            if (panel.Parent is { } parent)
                identities.Add(ObjectConstructionIdentity.Capture(parent));
            if ((panel.World as RuntimeWorld)?.TargetWorld is { } document)
                identities.Add(ObjectConstructionIdentity.Capture(document));
            HierarchyRenameDraftReceipt? draft = null;
            if (rename)
            {
                if (_virtualTree is not { } provider || !ReferenceEquals(list.VirtualProvider, provider))
                    return;
                draft = new(row.RenameInput, source, list, provider, row.Transform, lease, row.RenameSession);
            }
            _rowFocusReceipt = new(input, focused, source, canvas, panel, Window,
                unchecked(_hierarchyRevision + 1), identities.ToArray())
            {
                Rename = draft,
            };
            return;
        }
    }

    private bool IsHierarchyFocusOwnerCurrent(HierarchyRowFocusReceipt receipt)
        => IdentitiesCurrent(receipt) && ReferenceEquals(_rowFocusReceipt, receipt) && !_hierarchyDestroying &&
            !IsDestroyed && !IsDestroyQueued && _hierarchyRevision == receipt.Lifecycle &&
            ReferenceEquals(SceneNode, receipt.PanelNode) &&
            !SceneNode.IsTransformNull && ReferenceEquals(SceneNode.Transform, receipt.Panel) &&
            ReferenceEquals(receipt.InputComponent.SceneNode, receipt.InputNode) &&
            !receipt.InputNode.IsTransformNull && ReferenceEquals(receipt.InputNode.Transform, receipt.InputTransform) &&
            ReferenceEquals(receipt.Canvas.SceneNode, receipt.CanvasNode) &&
            !receipt.CanvasNode.IsTransformNull && ReferenceEquals(receipt.CanvasNode.Transform, receipt.CanvasTransform) &&
            ReferenceEquals(receipt.Panel.Parent, receipt.Parent) &&
            ReferenceEquals(receipt.Panel.World, receipt.World) && ReferenceEquals(Window, receipt.Window) &&
            ReferenceEquals(receipt.Panel.GetCanvasComponent(), receipt.Canvas) &&
            !receipt.Canvas.IsDestroyed && !receipt.Canvas.IsDestroyQueued &&
            receipt.Canvas.PublicationLifetime == receipt.CanvasLifetime &&
            ReferenceEquals(GetCanvasInput(), receipt.Input) &&
            !receipt.Source.IsDestroyed && !receipt.Source.IsDestroyQueued &&
            !receipt.Source.IsTransformNull && ReferenceEquals(receipt.Source.Transform, receipt.SourceTransform) &&
            ReferenceEquals(receipt.Source.World, receipt.SourceWorld) &&
            (receipt.Rename is not { } rename ||
                receipt.Source.Name == rename.SourceName &&
                rename.ParentCurrent &&
                ReferenceEquals(receipt.SourceTransform.Parent, rename.SourceParent) &&
                (rename.CaptureSettled || (rename.ReleaseStarted
                    ? rename.OriginalReleaseCurrent
                    : rename.OriginalDraftCurrent && IsCapturedRenameRowCurrent(receipt, rename)))) &&
            (receipt.RuntimeWorld is null || !receipt.RuntimeWorld.IsDisposing &&
                ReferenceEquals(receipt.RuntimeWorld.TargetWorld, receipt.Document));

    private bool IsCapturedRenameRowCurrent(HierarchyRowFocusReceipt receipt, HierarchyRenameDraftReceipt rename)
        => rename.OriginalBindingCurrent && ReferenceEquals(_treeListTransform, rename.OriginalList) &&
            ReferenceEquals(_virtualTree, rename.OriginalProvider) &&
            _hierarchyRows.TryGetValue(rename.OriginalRow, out HierarchyRow? row) && !row.Released &&
            ReferenceEquals(row.Node, receipt.Source) && ReferenceEquals(row.RenameInput, rename.OriginalInput) &&
            ReferenceEquals(row.RenameTransform, rename.OriginalTransform) &&
            row.ItemId == rename.OriginalLease.ItemId && row.RenameSession == rename.OriginalSession &&
            _renameSession == rename.OriginalSession && ReferenceEquals(_nodePendingRename, receipt.Source);

    private static bool IdentitiesCurrent(HierarchyRowFocusReceipt receipt)
    {
        foreach (ObjectConstructionIdentity identity in receipt.Identities)
            if (!identity.IsCurrent || identity.Target.IsDestroyed || identity.Target.IsDestroyQueued)
                return false;
        return true;
    }

    private void ClearCapturedHierarchyRowFocus()
    {
        HierarchyRowFocusReceipt? receipt = _rowFocusReceipt;
        if (receipt is null)
            return;
        bool Current() => IsHierarchyFocusOwnerCurrent(receipt);
        ulong clearedIntent = 0;
        bool cleared = Current() && (receipt.Rename is null
            ? receipt.Input.TryClearFocus(receipt.Target, receipt.CapturedIntent, out clearedIntent)
            : receipt.Input.TryReleaseFocusPreservingDraft(receipt.Target, receipt.CapturedIntent,
                Current, out clearedIntent));
        if (cleared && Current())
            receipt.ClearedIntent = clearedIntent;
        else if (ReferenceEquals(_rowFocusReceipt, receipt))
            _rowFocusReceipt = null;
    }

    private void RestoreHierarchyRowFocus()
    {
        if (_rowFocusReceipt is not { } receipt)
            return;
        if (!IsHierarchyFocusOwnerCurrent(receipt) || !OwnsRestoredRenameSession(receipt) ||
            receipt.ClearedIntent == 0 || !CanUseHierarchy)
        {
            _rowFocusReceipt = null;
            return;
        }
        // One request owns the input continuation. Never resubmit after a later focus intent.
        if (receipt.Requested)
        {
            if (!receipt.Input.HasPendingFocusRequest)
                _rowFocusReceipt = null;
            return;
        }
        if (receipt.Input.FocusIntentRevision != receipt.ClearedIntent ||
            receipt.Input.FocusedComponent is not null || receipt.Input.HasPendingFocusRequest ||
            _treeListTransform is not { } list || _virtualTree is not { } provider ||
            !_hierarchyIds.TryGetValue(receipt.Source.ID, out ulong id) ||
            !ReferenceEquals(_hierarchyNodes.GetValueOrDefault(id), receipt.Source) ||
            !provider.TryGetIndexOfItemId(id, out _))
        {
            _rowFocusReceipt = null;
            return;
        }
        // Layout realizes the retained viewport. Do not focus an unpresented replacement row.
        if (!list.TryGetRealizedVirtualRow(id, out UIBoundableTransform? transform, out UIVirtualRowLease lease))
            return;
        if (transform is null || !_hierarchyRows.TryGetValue(transform, out HierarchyRow? row) ||
            !IsHierarchyRowCurrent(row) || !ReferenceEquals(row.Node, receipt.Source))
        {
            _rowFocusReceipt = null;
            return;
        }
        RequestHierarchyRowFocus(receipt, list, provider, id, transform, lease, row);
    }

    private bool OwnsRestoredRenameSession(HierarchyRowFocusReceipt receipt)
        => receipt.Rename is { Prepared: true } rename
            ? ReferenceEquals(_nodePendingRename, receipt.Source) && _renameSession == rename.Session
            : _nodePendingRename is null;

    private void BeginCapturedRenameRelease()
    {
        if (_rowFocusReceipt is not { Rename: { } rename } receipt)
            return;
        if (!IsHierarchyFocusOwnerCurrent(receipt) || !rename.OriginalDraftCurrent ||
            receipt.ClearedIntent == 0)
            _rowFocusReceipt = null;
        else
            rename.ReleaseStarted = true;
    }

    private void SettleCapturedRenameDraft()
    {
        if (_rowFocusReceipt is not { Rename: { } rename } receipt)
            return;
        if (!IsHierarchyFocusOwnerCurrent(receipt) || !rename.OriginalReleaseComplete)
            _rowFocusReceipt = null;
        else
            rename.CaptureSettled = true;
    }

    private void RequestHierarchyRowFocus(HierarchyRowFocusReceipt receipt, UIListTransform list,
        UIVirtualTreeProvider provider, ulong id, UIBoundableTransform transform,
        UIVirtualRowLease lease, HierarchyRow row)
    {
        ulong incarnation = list.VirtualProviderIncarnation;
        bool Current() => CanUseHierarchy && IsHierarchyFocusOwnerCurrent(receipt) &&
            OwnsRestoredRenameSession(receipt) && ReferenceEquals(_treeListTransform, list) &&
            ReferenceEquals(_virtualTree, provider) && ReferenceEquals(list.VirtualProvider, provider) &&
            list.VirtualProviderIncarnation == incarnation && IsHierarchyRowCurrent(row) &&
            ReferenceEquals(row.Node, receipt.Source) && row.ItemId == id &&
            ReferenceEquals(_hierarchyNodes.GetValueOrDefault(id), receipt.Source) &&
            list.TryGetRealizedVirtualRow(id, out UIBoundableTransform? current, out UIVirtualRowLease currentLease) &&
            ReferenceEquals(current, transform) && currentLease == lease;
        UIInteractableComponent target = row.Button;
        if (receipt.Rename is { } rename)
        {
            if (!PrepareHierarchyRenameDraft(receipt, row, Current))
                return;
            target = row.RenameInput;
        }
        bool FocusCurrent() => Current() && (receipt.Rename is not { } restored ||
            restored.DestinationOwnerCurrent &&
            ReferenceEquals(restored.ReplacementInput, row.RenameInput) &&
            row.RenameSession == restored.Session &&
            !row.RenameInput.SceneNode.IsTransformNull &&
            ReferenceEquals(row.RenameInput.SceneNode.Transform, row.RenameTransform) &&
            ReferenceEquals(row.RenameTransform.Parent, row.Transform) &&
            row.RenameInput.TextWriteIntentRevision == restored.AcceptedTextIntent &&
            row.RenameInput.CaretWriteIntentRevision == restored.AcceptedCaretIntent &&
            row.RenameInput.Text == restored.Text && row.RenameInput.CursorPosition == restored.Cursor &&
            row.RenameInput.SelectionAnchor == restored.Anchor &&
            restored.AcceptedDisplay is { IsDestroyed: false, IsDestroyQueued: false } display &&
            ReferenceEquals(display.SceneNode, row.RenameInput.SceneNode) &&
            row.RenameInput.SceneNode.Components.Contains(display) &&
            (row.RenameInput.IsFocused
                ? ReferenceEquals(receipt.Input.FocusedComponent, row.RenameInput)
                : display.TextWriteIntentRevision == restored.AcceptedDisplayIntent));
        receipt.Requested = true;
        if (!receipt.Input.RequestVirtualItemFocus(list, id, target, receipt.ClearedIntent, FocusCurrent) &&
            ReferenceEquals(_rowFocusReceipt, receipt))
            _rowFocusReceipt = null;
    }
}
