using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

/// <summary>
/// One serialized GUID reference from a reached Unity source document.
/// </summary>
public sealed class UnityDependencyEdge
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
    public UnityImportDependencyKind Kind { get; init; }
    public bool IsCycle { get; internal set; }
}
