namespace XREngine.Editor;

/// <summary>Retains one release callback while a scope or history action owns its resource.</summary>
internal sealed class UndoHistoryResource(Action release)
{
    internal object? Owner { get; set; }
    internal Undo.StructuralUndoStep? StructuralOwner { get; set; }
    internal UndoHistoryResource? Next { get; set; }
    internal bool Queued { get; set; }
    internal bool Releasing { get; set; }
    internal bool IsReleased { get; set; }
    internal Exception? ReleaseFailure { get; set; }

    internal void Release() => release();
}
