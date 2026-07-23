namespace XREngine.Rendering;

/// <summary>
/// Identifies typed fields in a raw query result layout.
/// </summary>
public enum ERenderQueryField
{
    None,
    SamplesPassed,
    TimestampTicks,
    TimestampNanoseconds,
    ElapsedNanoseconds,
    PrimitivesWritten,
    PrimitivesNeeded,
    PrimitivesGenerated,
    MeshPrimitivesGenerated,
    PropertyValue,
    PerformanceCounter,
    VideoStatus,
    InputAssemblyVertices,
    InputAssemblyPrimitives,
    VertexShaderInvocations,
    GeometryShaderInvocations,
    GeometryShaderPrimitives,
    ClippingInvocations,
    ClippingPrimitives,
    FragmentShaderInvocations,
    TessellationControlShaderPatches,
    TessellationEvaluationShaderInvocations,
    ComputeShaderInvocations,
    TaskShaderInvocations,
    MeshShaderInvocations,
}
