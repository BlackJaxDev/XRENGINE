namespace XREngine.Rendering;

/// <summary>
/// The furthest fixed-cost stage reached by a compact zero-readback material-table pass.
/// </summary>
public enum EZeroReadbackMaterialTableDiagnosticStage
{
    NotVisited = 0,
    Entered,
    GeneratedLayoutReady,
    MissingGeneratedLayout,
    MissingMaterialTable,
    MissingTextureHandleTable,
    ProgramCreationFailed,
    ProgramPending,
    TopologyRejected,
    BucketLoop,
    ActualIndirectDispatch,
}
