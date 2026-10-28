using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>
/// Captures and restores a panel text field draft across a dock transfer. Restore writes the text
/// and selection through guarded writes. It requests presented focus only when the draft had focus
/// and the destination canvas has no focused control and no pending focus request.
/// </summary>
internal static class NativeTextFieldDraftTransfer
{
    /// <summary>Captures the draft of an unbound, non-composing field.</summary>
    internal static bool TryCapture(UITextInputComponent? input, out NativeTextFieldDraft draft)
    {
        draft = default;
        if (!IsTransferable(input))
            return false;
        IUICanvasInputSource? source = input!.UserInterfaceCanvas?.GetInputComponent();
        draft = new(input.Text, input.CursorPosition, input.SelectionAnchor,
            input.IsFocused && ReferenceEquals(source?.FocusedComponent, input));
        return draft.IsValid;
    }

    /// <summary>
    /// Writes <paramref name="draft"/> into <paramref name="input"/>. <paramref name="requireCurrent"/>
    /// throws when the caller's owner changed. When <paramref name="releaseFocus"/> is true and the
    /// draft had no focus, focus that the field holds is released without committing the draft.
    /// Returns true when the requested focus state was accepted.
    /// </summary>
    internal static bool Restore(UITextInputComponent input, NativeTextFieldDraft draft, Action requireCurrent,
        bool releaseFocus = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(requireCurrent);
        if (!draft.IsValid)
            throw new ArgumentException("The text field draft is not valid.", nameof(draft));
        requireCurrent();
        if (!IsTransferable(input) || input.SceneNode is not { } node)
            throw new InvalidOperationException("The destination text field cannot receive a draft.");
        ObjectConstructionIdentity inputIdentity = ObjectConstructionIdentity.Capture(input);
        ObjectConstructionIdentity nodeIdentity = ObjectConstructionIdentity.Capture(node);
        IUICanvasInputSource? source = input.UserInterfaceCanvas?.GetInputComponent();
        void Require()
        {
            requireCurrent();
            if (!inputIdentity.IsCurrent || !nodeIdentity.IsCurrent || !ReferenceEquals(input.SceneNode, node) ||
                input.IsDestroyed || input.IsDestroyQueued || !IsTransferable(input) ||
                !ReferenceEquals(input.UserInterfaceCanvas?.GetInputComponent(), source))
                throw new InvalidOperationException("The destination text field changed during restore.");
        }
        if (input.Text != draft.Text)
        {
            input.SetTextGuarded(draft.Text, (Action)Require, static require => require());
            Require();
        }
        if (input.CursorPosition != draft.Cursor || input.SelectionAnchor != draft.Anchor)
        {
            input.SetSelectionGuarded(draft.Cursor, draft.Anchor, (Action)Require, static require => require());
            Require();
        }
        if (input.Text != draft.Text || input.CursorPosition != draft.Cursor || input.SelectionAnchor != draft.Anchor)
            throw new InvalidOperationException("The destination text field did not accept the draft.");
        if (source is null || source.HasPendingFocusRequest)
            return false;
        bool Current()
        {
            try { Require(); return true; }
            catch (InvalidOperationException) { return false; }
        }
        bool accepted;
        if (draft.Focused)
        {
            if (source.FocusedComponent is not null)
                return false;
            accepted = source.TryFocusPresented(input, source.FocusIntentRevision, Current);
        }
        else
        {
            if (!releaseFocus || !input.IsFocused || !ReferenceEquals(source.FocusedComponent, input))
                return false;
            accepted = source.TryReleaseFocusPreservingDraft(input, source.FocusIntentRevision, Current, out _);
        }
        Require();
        return accepted;
    }

    private static bool IsTransferable(UITextInputComponent? input)
        => input is { IsDestroyed: false, IsDestroyQueued: false, IsComposing: false, CommitOnSubmit: false } &&
            input.Binding is null && input.Property is null && input.Targets is null;
}
