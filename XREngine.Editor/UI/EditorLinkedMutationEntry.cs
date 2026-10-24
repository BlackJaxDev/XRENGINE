namespace XREngine.Editor.UI;

/// <summary>Stores one exact list change and all owners of that list.</summary>
internal sealed class EditorLinkedMutationEntry
{
    public required object List { get; init; }
    public required EditorLinkedListAdapter Adapter { get; init; }
    public required EditorLinkedOwnerProof[] Owners { get; init; }
    public required EditorLinkedListSnapshot Before { get; init; }
    public required EditorLinkedListSnapshot After { get; init; }
    public required object BeforeState { get; init; }
    public required object AfterState { get; init; }
    public required EEditorLinkedMutation Operation { get; init; }
    public required int Index { get; init; }
    public required object Node { get; init; }

    public bool IsOwnerCurrent()
    {
        foreach (EditorLinkedOwnerProof owner in Owners)
            if (!owner.IsCurrent())
                return false;
        return true;
    }

    public bool IsOwnerLocationCurrent()
    {
        foreach (EditorLinkedOwnerProof owner in Owners)
            if (!owner.IsLocationCurrent()) return false;
        return true;
    }
}
