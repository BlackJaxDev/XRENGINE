namespace XREngine.Editor.UI;

/// <summary>Invalidates list row callbacks after a structural edit.</summary>
internal sealed class EditorListMutationContext
{
    public EditorListRowSource Rows { get; } = new();
    public ulong Revision { get; private set; } = 1;
    public bool IsActive { get; private set; } = true;
    public Action? RefreshRequested { get; set; }

    public void RevisionChanged() => Revision++;

    public void Retire()
    {
        if (!IsActive) return;
        IsActive = false;
        Revision++;
        RefreshRequested = null;
    }

    public ulong Advance(EEditorListMutation operation, int index, bool toApplied, ulong rowId = 0)
    {
        Revision++;
        ulong affected = rowId;
        try
        {
            switch (operation)
            {
                case EEditorListMutation.Set: Rows.Refresh(); break;
                case EEditorListMutation.Insert when toApplied:
                case EEditorListMutation.Remove when !toApplied:
                    affected = Rows.Insert(index, rowId);
                    break;
                default:
                    affected = Rows.Remove(index);
                    break;
            }
        }
        catch (Exception error)
        {
            Rows.Reset(0);
            Debug.LogException(error, "Could not update list row identities after an edit.");
        }
        return affected;
    }

    public void PublishRefresh()
    {
        try { if (IsActive) RefreshRequested?.Invoke(); }
        catch (Exception error) { Debug.LogException(error, "Could not refresh edited list rows."); }
    }
}
