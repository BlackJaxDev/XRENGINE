using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>One structural Undo action, possibly containing several clone roots.</summary>
internal interface IHierarchyMutationWork
{
    IReadOnlyList<SceneNode> Nodes { get; }
    void PrepareInitial();
    void ApplyInitial();
    bool RestoreInitial();
    void CaptureFailure();
    void MarkDirty();
    void ReconcileProperties(StructuralChangeHandle handle);
    void RequireBefore(bool includeProperties);
    void RequireAfter(bool includeProperties);
    void RequireUndoEntry();
    void UndoMembership();
    void RedoMembership();
    void CompleteRedo();
}
