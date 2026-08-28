namespace XREngine.Animation.Importers;

/// <summary>
/// Component-independent identity for one serialized quaternion binding. It is
/// shared by import-time slot grouping and runtime application so four scalar
/// channels are always normalized and blended as one rotation.
/// </summary>
public readonly record struct ImportedAnimationQuaternionBindingKey(
    string NodePath,
    uint PathHash,
    string Attribute,
    uint AttributeHash,
    int? ClassId,
    SourceAssetReference Script,
    byte CustomType,
    bool RequiresAdapter)
{
    public static bool TryCreate(
        ImportedAnimationBindingDescriptor binding,
        out ImportedAnimationQuaternionBindingKey key)
    {
        if (binding.ValueKind != EImportedAnimationBindingValueKind.Quaternion
            || binding.Component is < 0 or > 3
            || binding.IsPPtrCurve
            || binding.IsIntCurve)
        {
            key = default;
            return false;
        }

        string attribute = HasComponentSuffix(binding.Attribute)
            ? binding.Attribute[..^2]
            : binding.Attribute;
        key = new ImportedAnimationQuaternionBindingKey(
            binding.NodePath,
            binding.PathHash,
            attribute,
            binding.AttributeHash,
            binding.ClassId,
            binding.Script,
            binding.CustomType,
            binding.RequiresAdapter);
        return true;
    }

    private static bool HasComponentSuffix(string attribute)
        => attribute.Length >= 2
            && attribute[^2] == '.'
            && "xyzw".Contains(char.ToLowerInvariant(attribute[^1]));
}
