using System.Text;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Durable imported-entity identity, using a producer key when available and an explicit
/// hierarchy/ordinal fallback otherwise.
/// </summary>
public sealed class ImportedEntityKey
{
    public ImportedEntityKey(string value, bool isStable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Value = value.Normalize(NormalizationForm.FormC);
        IsStable = isStable;
    }

    public string Value { get; }
    public bool IsStable { get; }

    public override string ToString() => Value;
}
