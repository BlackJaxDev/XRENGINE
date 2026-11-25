namespace XREngine.Editor.UI;

/// <summary>Replays nested list snapshots after validating durable outer paths.</summary>
internal sealed class EditorNestedListMutationReceipt(EditorNestedListMutationEntry[] entries,
    EditorNestedListMutationContext context)
{
    private readonly WeakReference<EditorNestedListMutationContext> _context = new(context);
    private bool _applied = true;
    private ulong _rowId;

    public void AdvanceInitial()
    {
        if (_context.TryGetTarget(out EditorNestedListMutationContext? context))
            _rowId = context.Rows.Advance(entries[0].Operation, entries[0].Index, true);
    }

    public void PublishInitial()
    {
        if (_context.TryGetTarget(out EditorNestedListMutationContext? context)) context.Refresh();
    }

    public void Undo() => Replay(toApplied: false);
    public void Redo() => Replay(toApplied: true);

    private void Replay(bool toApplied)
    {
        if (_applied == toApplied)
            throw new InvalidOperationException("The nested list edit is already in the requested state.");
        EditorNestedListMutation.Replay(entries, toApplied);
        _applied = toApplied;
        try
        {
            if (_context.TryGetTarget(out EditorNestedListMutationContext? context) && context.IsActive)
            {
                _rowId = context.Rows.Advance(entries[0].Operation, entries[0].Index, toApplied, _rowId);
                context.Refresh();
            }
        }
        catch (Exception error) { Debug.LogException(error, "Could not refresh replayed nested list rows."); }
    }
}
