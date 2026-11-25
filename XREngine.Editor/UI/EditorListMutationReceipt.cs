namespace XREngine.Editor.UI;

/// <summary>Replays a complete list edit only when all source lists still match.</summary>
internal sealed class EditorListMutationReceipt(EditorListMutationEntry[] entries,
    EditorListMutationContext? context)
{
    private bool _applied = true;
    private readonly WeakReference<EditorListMutationContext>? _context = context is null ? null : new(context);
    private ulong _rowId;

    public void AdvanceInitial()
    {
        if (_context?.TryGetTarget(out EditorListMutationContext? context) == true)
            _rowId = context.Advance(entries[0].Operation, entries[0].Index, true);
    }

    public void PublishInitial()
    {
        if (_context?.TryGetTarget(out EditorListMutationContext? context) == true)
            context.PublishRefresh();
    }

    public void Undo() => Replay(toApplied: false);
    public void Redo() => Replay(toApplied: true);

    private void Replay(bool toApplied)
    {
        if (_applied == toApplied)
            throw new InvalidOperationException("The list edit is already in the requested state.");
        EditorListMutation.Replay(entries, toApplied);
        _applied = toApplied;
        try
        {
            if (_context?.TryGetTarget(out EditorListMutationContext? context) == true)
            {
                _rowId = context.Advance(entries[0].Operation, entries[0].Index, toApplied, _rowId);
                context.PublishRefresh();
            }
        }
        catch (Exception error) { Debug.LogException(error, "Could not refresh replayed list rows."); }
        finally { EditorListMutation.MarkOwners(entries); }
    }
}
