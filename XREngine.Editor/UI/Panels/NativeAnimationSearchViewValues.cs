namespace XREngine.Editor;

/// <summary>Stores an unsubmitted search draft separately from its committed query.</summary>
internal readonly record struct NativeAnimationSearchViewValues(string Text, int Cursor, int Anchor, bool Focused)
{
    internal bool IsValid => Text is not null && Cursor >= 0 && Cursor <= Text.Length &&
        Anchor >= 0 && Anchor <= Text.Length;
}
