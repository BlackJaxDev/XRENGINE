namespace XREngine.Rendering;

/// <summary>Complete CPU-side blendshape buffer state exchanged at one publication boundary.</summary>
/// <remarks>
/// This is a reference type so one volatile reference exchange publishes the complete
/// generation to readers.
/// </remarks>
public sealed record XRMeshBlendshapeBufferState(
    XRDataBuffer? Counts,
    XRDataBuffer? Indices,
    XRDataBuffer? Deltas,
    XRDataBuffer? SparseShapeRanges,
    XRDataBuffer? SparseRecords,
    XRDataBuffer? QuantizedDeltas,
    XRDataBuffer? QuantizationMetadata,
    BlendshapeShaderVariant ShaderVariant,
    BlendshapeDeltaStorageMode StorageMode,
    BlendshapeDeltaEncoding Encoding,
    int AffectedVertexCount,
    int SparseRecordCount);
