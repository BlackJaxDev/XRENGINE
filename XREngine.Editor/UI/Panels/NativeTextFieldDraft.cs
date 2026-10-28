namespace XREngine.Editor;

/// <summary>
/// Stores an unsubmitted text field draft, its caret and selection anchor, and whether the field
/// had presented focus. The draft is separate from the panel's committed value.
/// </summary>
internal readonly record struct NativeTextFieldDraft(string Text, int Cursor, int Anchor, bool Focused)
{
    internal bool IsValid => Text is not null && Cursor >= 0 && Cursor <= Text.Length &&
        Anchor >= 0 && Anchor <= Text.Length;
}
