namespace XREngine.Rendering;

/// <summary>One immutable storage-publication range bound at a reflected buffer index.</summary>
public readonly record struct ReadOnlyStorageBinding(
    uint Binding,
    ReadOnlyStoragePublication Publication,
    int Offset,
    int Length)
{
    public bool IsValid
        => Publication.IsValid && Offset >= 0 && Length > 0 &&
           Offset <= Publication.Length && Length <= Publication.Length - Offset;
}
