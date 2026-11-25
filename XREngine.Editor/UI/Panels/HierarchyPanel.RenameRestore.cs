using XREngine.Data.Core;
using XREngine.Rendering.UI;

namespace XREngine.Editor;

public partial class HierarchyPanel
{
    private bool PrepareHierarchyRenameDraft(HierarchyRowFocusReceipt receipt,
        HierarchyRow row, Func<bool> isCurrent)
    {
        HierarchyRenameDraftReceipt draft = receipt.Rename!;
        if (draft.Prepared)
            return isCurrent();
        if (!isCurrent() || !draft.DestinationCurrent || !ReferenceEquals(draft.ReplacementInput, row.RenameInput) ||
            receipt.Input.FocusIntentRevision != receipt.ClearedIntent ||
            receipt.Input.FocusedComponent is not null || receipt.Input.HasPendingFocusRequest)
        {
            if (ReferenceEquals(_rowFocusReceipt, receipt))
                _rowFocusReceipt = null;
            return false;
        }

        UITextInputComponent input = row.RenameInput;
        var inputNode = input.SceneNode;
        var inputTransform = inputNode.Transform;
        ObjectConstructionIdentity inputIdentity = ObjectConstructionIdentity.Capture(input);
        ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(inputNode);
        ObjectConstructionIdentity transformIdentity = ObjectConstructionIdentity.Capture(inputTransform);
        UITextComponent display = draft.AcceptedDisplay!;
        ObjectConstructionIdentity displayIdentity = ObjectConstructionIdentity.Capture(display);
        ulong originalDisplayIntent = display.TextWriteIntentRevision;
        ulong displayIntent = originalDisplayIntent;
        bool publishingText = false;
        ulong textIntent = input.TextWriteIntentRevision;
        ulong caretIntent = input.CaretWriteIntentRevision;
        bool complete = false;
        draft.Session = ++_renameSession;
        draft.Prepared = true;
        draft.ReplacementInput = input;
        _nodePendingRename = receipt.Source;
        _renameDraft = draft.Text;
        _renameFocusRequested = false;
        // Bind visibility without the ordinary draft setter. Guarded writes follow below.
        row.RenameSession = draft.Session;
        void Require()
        {
            if (!isCurrent() || receipt.Input.FocusIntentRevision != receipt.ClearedIntent ||
                receipt.Input.FocusedComponent is not null || receipt.Input.HasPendingFocusRequest ||
                !inputIdentity.IsCurrent || !nodeIdentity.IsCurrent || !transformIdentity.IsCurrent ||
                input.IsDestroyed || input.IsDestroyQueued || inputNode.IsDestroyed || inputNode.IsDestroyQueued ||
                inputTransform.IsDestroyed || inputTransform.IsDestroyQueued || inputNode.IsTransformNull ||
                !ReferenceEquals(input.SceneNode, inputNode) ||
                !ReferenceEquals(inputNode.Transform, inputTransform) ||
                !ReferenceEquals(inputTransform.Parent, row.Transform) ||
                !displayIdentity.IsCurrent || display.IsDestroyed || display.IsDestroyQueued ||
                !ReferenceEquals(display.SceneNode, inputNode) || !inputNode.Components.Contains(display) ||
                (display.TextWriteIntentRevision != displayIntent &&
                    (!publishingText || display.TextWriteIntentRevision != originalDisplayIntent)) ||
                !ReferenceEquals(row.RenameInput, input) || row.RenameSession != draft.Session ||
                input.TextWriteIntentRevision != textIntent || input.CaretWriteIntentRevision != caretIntent)
                throw new InvalidOperationException("The hierarchy rename owner changed during restore.");
        }
        try
        {
            Require();
            BindRenameSession(row, receipt.Source);
            Require();
            textIntent = checked(textIntent + 1);
            displayIntent = checked(originalDisplayIntent + 1);
            publishingText = true;
            input.SetTextGuarded(draft.Text, (Action)Require, static require => require());
            publishingText = false;
            Require();
            caretIntent = checked(caretIntent + 1);
            input.SetSelectionGuarded(draft.Cursor, draft.Anchor, (Action)Require,
                static require => require());
            Require();
            draft.AcceptedTextIntent = textIntent;
            draft.AcceptedCaretIntent = caretIntent;
            draft.AcceptedDisplay = display;
            draft.AcceptedDisplayIntent = displayIntent;
            complete = true;
            return true;
        }
        finally
        {
            // Preserve callback edits. A failed projection cannot retry against a later owner.
            if (!complete && ReferenceEquals(_rowFocusReceipt, receipt))
            {
                _rowFocusReceipt = null;
                if (_renameSession == draft.Session &&
                    ReferenceEquals(_nodePendingRename, receipt.Source))
                {
                    _nodePendingRename = null;
                    _renameDraft = string.Empty;
                    _renameFocusRequested = false;
                    _renameSession++;
                }
            }
        }
    }
}
