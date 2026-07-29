using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

/// <summary>
/// Reached Unity asset in a prefab dependency closure.
/// </summary>
public sealed class UnityDependencyNode
{
    public required string SourcePath { get; init; }
    public string? SourceGuid { get; init; }
    public required string PortablePath { get; init; }
    public UnityImportDependencyKind Kind { get; internal set; }
    public UnityImportConversionOutcome Outcome { get; set; } = UnityImportConversionOutcome.Pending;
    public string? OutputAssetPath { get; set; }
    public List<UnityDependencyEdge> OutgoingEdges { get; } = [];
}
