namespace XREngine.Editor.UI;

/// <summary>Stores one exact dictionary edit and all owners of that dictionary.</summary>
internal sealed class EditorDictionaryMutationEntry
{
    public required object Dictionary { get; init; }
    public required EditorDictionaryAdapter Adapter { get; init; }
    public required EditorDictionaryOwnerProof[] Owners { get; init; }
    public required EditorDictionarySnapshot Before { get; init; }
    public required EditorDictionarySnapshot After { get; init; }
    public required object BeforeState { get; init; }
    public required object AfterState { get; init; }
    public required EEditorDictionaryMutation Operation { get; init; }
    public required object Key { get; init; }
    public required object? BeforeValue { get; init; }
    public required object? AfterValue { get; init; }

    public bool IsOwnerCurrent()
    {
        foreach (EditorDictionaryOwnerProof owner in Owners)
            if (!owner.IsCurrent()) return false;
        return true;
    }

    public bool IsOwnerLocationCurrent()
    {
        foreach (EditorDictionaryOwnerProof owner in Owners)
            if (!owner.IsLocationCurrent()) return false;
        return true;
    }
}
