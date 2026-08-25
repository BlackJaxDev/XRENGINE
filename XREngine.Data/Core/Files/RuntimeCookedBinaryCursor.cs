namespace XREngine.Core.Files;

/// <summary>
/// Shared cursor state for stack-confined cooked readers and writers. The serializer
/// contract passes those ref structs by value, so copies must observe one cursor.
/// Keeping only the numeric position here preserves span lifetime confinement while
/// preventing a copied reader or writer from replaying the same field.
/// </summary>
internal sealed class RuntimeCookedBinaryCursor
{
    internal int Position;
}
