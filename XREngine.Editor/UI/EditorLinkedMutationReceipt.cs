namespace XREngine.Editor.UI;

/// <summary>Replays one grouped linked-list change with exact source checks.</summary>
internal sealed class EditorLinkedMutationReceipt(EditorLinkedMutationEntry[] entries, Action? refresh)
{
    private bool _applied = true;
    private readonly EditorLinkedRefreshHandle _refresh = new(refresh);

    public void Undo() => Replay(false);
    public void Redo() => Replay(true);

    private void Replay(bool toApplied)
    {
        if (_applied == toApplied)
            throw new InvalidOperationException("The linked-list edit is already in this state.");
        EditorLinkedMutation.Replay(entries, toApplied);
        _applied = toApplied;
        try { _refresh.Invoke(); }
        catch (Exception error) { Debug.LogException(error, "Could not refresh linked-list rows."); }
        finally { EditorLinkedMutation.MarkOwners(entries); }
    }
}
