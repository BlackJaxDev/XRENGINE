namespace XREngine.Editor.UI;

/// <summary>Owns the live nested detail's path leases and refresh callback.</summary>
internal sealed class EditorNestedListMutationContext(EditorNestedListSourceLease[] paths,
    EditorListMutationContext rows)
{
    public EditorNestedListSourceLease[] Paths { get; } = paths;
    public EditorListMutationContext Rows { get; } = rows;
    public bool IsActive => Rows.IsActive;
    public Action? RefreshRequested { get; set; }
    public void Refresh()
    {
        try { if (IsActive) { Rows.PublishRefresh(); RefreshRequested?.Invoke(); } }
        catch (Exception error) { Debug.LogException(error, "Could not refresh nested list rows."); }
    }
    public void Retire()
    {
        Rows.Retire();
        RefreshRequested = null;
    }
}
