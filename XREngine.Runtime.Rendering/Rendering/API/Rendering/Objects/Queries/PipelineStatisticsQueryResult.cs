namespace XREngine.Rendering;

/// <summary>
/// Typed pipeline-statistics result without dictionaries, reflection, or boxing.
/// </summary>
public readonly record struct PipelineStatisticsQueryResult(
    ERenderPipelineStatistics Present,
    ulong InputAssemblyVertices,
    ulong InputAssemblyPrimitives,
    ulong VertexShaderInvocations,
    ulong GeometryShaderInvocations,
    ulong GeometryShaderPrimitives,
    ulong ClippingInvocations,
    ulong ClippingPrimitives,
    ulong FragmentShaderInvocations,
    ulong TessellationControlShaderPatches,
    ulong TessellationEvaluationShaderInvocations,
    ulong ComputeShaderInvocations,
    ulong TaskShaderInvocations,
    ulong MeshShaderInvocations)
{
    public bool TryGet(ERenderPipelineStatistics statistic, out ulong value)
    {
        value = statistic switch
        {
            ERenderPipelineStatistics.InputAssemblyVertices => InputAssemblyVertices,
            ERenderPipelineStatistics.InputAssemblyPrimitives => InputAssemblyPrimitives,
            ERenderPipelineStatistics.VertexShaderInvocations => VertexShaderInvocations,
            ERenderPipelineStatistics.GeometryShaderInvocations => GeometryShaderInvocations,
            ERenderPipelineStatistics.GeometryShaderPrimitives => GeometryShaderPrimitives,
            ERenderPipelineStatistics.ClippingInvocations => ClippingInvocations,
            ERenderPipelineStatistics.ClippingPrimitives => ClippingPrimitives,
            ERenderPipelineStatistics.FragmentShaderInvocations => FragmentShaderInvocations,
            ERenderPipelineStatistics.TessellationControlShaderPatches => TessellationControlShaderPatches,
            ERenderPipelineStatistics.TessellationEvaluationShaderInvocations => TessellationEvaluationShaderInvocations,
            ERenderPipelineStatistics.ComputeShaderInvocations => ComputeShaderInvocations,
            ERenderPipelineStatistics.TaskShaderInvocations => TaskShaderInvocations,
            ERenderPipelineStatistics.MeshShaderInvocations => MeshShaderInvocations,
            _ => 0ul,
        };
        return statistic != ERenderPipelineStatistics.None && (Present & statistic) == statistic;
    }
}
