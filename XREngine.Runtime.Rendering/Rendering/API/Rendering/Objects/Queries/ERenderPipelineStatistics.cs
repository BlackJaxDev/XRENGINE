namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral pipeline-statistics counters in Vulkan result order.
/// </summary>
[Flags]
public enum ERenderPipelineStatistics : uint
{
    None = 0,
    InputAssemblyVertices = 1u << 0,
    InputAssemblyPrimitives = 1u << 1,
    VertexShaderInvocations = 1u << 2,
    GeometryShaderInvocations = 1u << 3,
    GeometryShaderPrimitives = 1u << 4,
    ClippingInvocations = 1u << 5,
    ClippingPrimitives = 1u << 6,
    FragmentShaderInvocations = 1u << 7,
    TessellationControlShaderPatches = 1u << 8,
    TessellationEvaluationShaderInvocations = 1u << 9,
    ComputeShaderInvocations = 1u << 10,
    TaskShaderInvocations = 1u << 11,
    MeshShaderInvocations = 1u << 12,
    AllCore = InputAssemblyVertices |
        InputAssemblyPrimitives |
        VertexShaderInvocations |
        GeometryShaderInvocations |
        GeometryShaderPrimitives |
        ClippingInvocations |
        ClippingPrimitives |
        FragmentShaderInvocations |
        TessellationControlShaderPatches |
        TessellationEvaluationShaderInvocations |
        ComputeShaderInvocations,
    All = AllCore | TaskShaderInvocations | MeshShaderInvocations,
}
