namespace XREngine.Editor.UI;

/// <summary>Replays one grouped dictionary edit with exact source checks.</summary>
internal sealed class EditorDictionaryMutationReceipt(EditorDictionaryMutationEntry[] entries,
    Action? refresh)
{
    private bool _applied = true;
    private readonly EditorLinkedRefreshHandle _refresh = new(refresh);

    public void Undo() => Replay(false);
    public void Redo() => Replay(true);

    private void Replay(bool toApplied)
    {
        if (_applied == toApplied)
            throw new InvalidOperationException("The dictionary edit is already in this state.");
        EditorDictionaryMutation.Replay(entries, toApplied);
        _applied = toApplied;
        try { _refresh.Invoke(); }
        catch (Exception error) { Debug.LogException(error, "Could not refresh dictionary rows."); }
        finally { EditorDictionaryMutation.MarkOwners(entries); }
    }
}
