using MemoryPack;

namespace XREngine.Animation.Importers;

/// <summary>
/// Portable description of one Unity serialized-property binding. Native XRE
/// targets consume it directly; Unity-only targets require an explicit adapter.
/// </summary>
[MemoryPackable]
public sealed partial record ImportedAnimationBindingDescriptor
{
    public string SourceField { get; init; } = string.Empty;
    public string NodePath { get; init; } = string.Empty;
    public string Attribute { get; init; } = string.Empty;
    /// <summary>
    /// Unity CRC32 path identifier used by packed clip bindings when the
    /// original path string is not serialized. Zero denotes the clip root.
    /// </summary>
    public uint PathHash { get; init; }
    /// <summary>
    /// Unity serialized-property identifier used by packed bindings. Known
    /// transform attributes retain their native values 1 through 4.
    /// </summary>
    public uint AttributeHash { get; init; }
    public int? ClassId { get; init; }
    public SourceAssetReference Script { get; init; }
    public EImportedAnimationBindingValueKind ValueKind { get; init; }
    public int Component { get; init; } = -1;
    public byte CustomType { get; init; }
    public bool IsPPtrCurve { get; init; }
    public bool IsIntCurve { get; init; }
    /// <summary>Raw Unity editable-binding flags, preserved for adapter decisions.</summary>
    public int BindingFlags { get; init; }
    /// <summary>Nested serialized version carried by the editable binding.</summary>
    public int BindingSerializedVersion { get; init; }
    /// <summary>
    /// Unity 2022.2+ binding flag for a field below a managed-reference graph.
    /// Such bindings remain adapter-owned until the target supplies an explicit
    /// managed-reference resolver instead of being mistaken for a native field.
    /// </summary>
    public bool IsSerializeReferenceCurve { get; init; }
    public bool RequiresAdapter { get; init; }

    /// <summary>
    /// Returns the semantic target identity used by animation-member path
    /// registration. Source representation is intentionally excluded so an
    /// editable, dense, or streamed curve targeting the same property shares
    /// one blend slot across clips.
    /// </summary>
    public override string ToString()
        => $"UnityBinding[{NodePath}|{PathHash:X8}|{Attribute}|{AttributeHash:X8}|{ClassId}|" +
            $"{Script.FileId}:{Script.Guid}:{Script.Type}|{ValueKind}|{Component}|{CustomType}|" +
            $"{IsPPtrCurve}|{IsIntCurve}|{IsSerializeReferenceCurve}|{RequiresAdapter}]";
}
