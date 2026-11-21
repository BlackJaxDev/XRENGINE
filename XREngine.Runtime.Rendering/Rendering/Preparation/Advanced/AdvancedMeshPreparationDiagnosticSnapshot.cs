namespace XREngine.Rendering;

/// <summary>Reports cold deformation preparation progress without reading GPU data.</summary>
public readonly record struct AdvancedMeshPreparationDiagnosticSnapshot(
    string? PendingMesh,
    int PendingStage,
    int VertexIndex,
    int VertexCount,
    int ShapeIndex,
    int ShapeCount,
    uint PackedRecords,
    uint RequiredRecords,
    ulong BudgetFrame,
    ulong LastOwnerVisitFrame,
    double BudgetElapsedMilliseconds,
    int ReadyMeshCount,
    bool SourceMatches);
