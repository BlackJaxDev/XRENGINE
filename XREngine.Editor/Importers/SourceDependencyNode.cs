using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

/// <summary>
/// Reached Unity asset in a prefab dependency closure.
/// </summary>
public sealed class SourceDependencyNode
{
    public required string SourcePath { get; init; }
    public string? SourceGuid { get; init; }
    public required string PortablePath { get; init; }
    public SourceImportDependencyKind Kind { get; internal set; }
    public SourceImportConversionOutcome Outcome { get; set; } = SourceImportConversionOutcome.Pending;
    public string? OutputAssetPath { get; set; }
    public List<SourceDependencyEdge> OutgoingEdges { get; } = [];
}
