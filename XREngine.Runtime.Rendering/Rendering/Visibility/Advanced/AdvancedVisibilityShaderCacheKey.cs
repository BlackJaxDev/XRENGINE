namespace XREngine.Rendering;

/// <summary>
/// Visibility-raster pipeline identity. Material instance identity is deliberately absent.
/// </summary>
public readonly record struct AdvancedVisibilityShaderCacheKey(
    uint PayloadVersion,
    ulong VertexLayoutId,
    EAdvancedMaterialCoverageMode Coverage,
    EAdvancedDeformationExecutionMode DeformationMode,
    EAdvancedVisibilityDisplacementMode DisplacementMode,
    EAdvancedShaderViewMode ViewMode,
    EAdvancedGeometryProducer Producer,
    RuntimeGraphicsApiKind Backend,
    EAdvancedVisibilityTargetEncoding Encoding,
    bool DiagnosticBounds)
{
    public static AdvancedVisibilityShaderCacheKey Create(
        ulong vertexLayoutId,
        EAdvancedMaterialCoverageMode coverage,
        EAdvancedDeformationExecutionMode deformationMode,
        EAdvancedShaderViewMode viewMode,
        EAdvancedGeometryProducer producer,
        RuntimeGraphicsApiKind backend,
        bool diagnosticBounds = false,
        EAdvancedVisibilityDisplacementMode displacementMode = EAdvancedVisibilityDisplacementMode.None)
        => new(
            AdvancedVisibilityBufferContract.PayloadVersion,
            vertexLayoutId,
            coverage,
            deformationMode,
            displacementMode,
            viewMode,
            producer,
            backend,
            AdvancedVisibilityBufferContract.Encoding,
            diagnosticBounds);
}
