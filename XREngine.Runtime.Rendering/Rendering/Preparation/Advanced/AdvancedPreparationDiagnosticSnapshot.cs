namespace XREngine.Rendering;

/// <summary>Coherent, on-demand diagnostics for shared preparation and its last admission decision.</summary>
public readonly record struct AdvancedPreparationDiagnosticSnapshot(
    AdvancedPreparationPublication Publication,
    string DeferralReason,
    string OutputReuseStatus,
    uint OutputReuseSlot,
    string OutputReuseAuthority,
    AdvancedSharedPreparationTelemetry SharedTelemetry,
    AdvancedDeformedVertexArenaTelemetry ArenaTelemetry,
    AdvancedFrameUploadTelemetrySnapshot FrameUploadTelemetry,
    AdvancedVisibilityContentTelemetry VisibilityContent)
{
    /// <summary>Exact mesh-input generation selected for the last preparation attempt.</summary>
    public ulong DeformationSourceGeneration { get; init; }

    /// <summary>Unique mesh count in the selected deformation source set.</summary>
    public int DeformationSourceMeshCount { get; init; }
    /// <summary>Changed input fields in the last source generation transition.</summary>
    public EAdvancedDeformationSourceChange DeformationSourceChanges { get; init; }

    /// <summary>First mesh with changed inputs in that transition.</summary>
    public string? DeformationSourceChangedMesh { get; init; }
}
