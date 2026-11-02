using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

/// <summary>
/// One serialized GUID reference from a reached Unity source document.
/// </summary>
public sealed class SourceDependencyEdge
{
    public required string SourcePath { get; init; }
    public required string TargetGuid { get; init; }
    public long TargetFileId { get; init; }
    public string? TargetPath { get; init; }
    /// <summary>
    /// Local fileID of the source object whose prefab modification owns this
    /// reference. Null for ordinary serialized asset references.
    /// </summary>
    public long? ReferringObjectFileId { get; init; }
    public required string ReferringProperty { get; init; }
    /// <summary>Names the material texture slot that contains the serialized reference.</summary>
    public string? ReferringMaterialTextureSlot { get; init; }
    /// <summary>Identifies the slot and the original serialized property for diagnostics.</summary>
    public string ReferringPropertyPath => ReferringMaterialTextureSlot is { } slot
        ? $"{slot}.{ReferringProperty}"
        : ReferringProperty;
    public SourceImportDependencyKind Kind { get; internal set; }
    /// <summary>Records why the selected shader cannot read this missing texture.</summary>
    public string? InactiveTextureReason { get; internal set; }
    internal string? InactiveMaterialSha256 { get; set; }
    internal string? InactiveShaderPath { get; set; }
    internal string? InactiveShaderSha256 { get; set; }
    public bool IsCycle { get; internal set; }
}
