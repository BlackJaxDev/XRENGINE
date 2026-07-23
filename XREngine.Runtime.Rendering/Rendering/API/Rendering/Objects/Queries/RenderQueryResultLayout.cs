using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Allocation-free description of the native and engine result shape.
/// </summary>
public readonly record struct RenderQueryResultLayout(
    ERenderQueryKind Kind,
    uint ValuesPerQuery,
    uint QueryCount,
    uint ViewSlotCount,
    int AvailabilityValueOffset,
    ERenderQueryIntegerWidth IntegerWidth,
    ERenderQueryAggregation Aggregation,
    ERenderPipelineStatistics Statistics = ERenderPipelineStatistics.None,
    ERenderQueryProperty Property = ERenderQueryProperty.None)
{
    public uint ValueCount => checked(ValuesPerQuery * QueryCount);

    public uint NativeValuesPerQuery => checked(ValuesPerQuery + (AvailabilityValueOffset >= 0 ? 1u : 0u));

    public uint NativeValueCount => checked(NativeValuesPerQuery * QueryCount);

    public uint NativeStrideBytes => checked(NativeValuesPerQuery * (uint)IntegerWidth);

    public uint NativeSizeBytes => checked(NativeValueCount * (uint)IntegerWidth);

    /// <summary>
    /// Returns whether caller-owned storage can hold the complete native result,
    /// including availability values.
    /// </summary>
    public bool FitsNativeResult(int destinationLength)
        => destinationLength >= 0 && (uint)destinationLength >= NativeValueCount;

    public ERenderQueryField GetField(uint valueIndex)
    {
        if (valueIndex >= ValuesPerQuery)
            return ERenderQueryField.None;

        if (Kind == ERenderQueryKind.PipelineStatistics)
            return GetPipelineStatisticField(valueIndex, Statistics);

        return Kind switch
        {
            ERenderQueryKind.Occlusion => ERenderQueryField.SamplesPassed,
            ERenderQueryKind.Timestamp => ERenderQueryField.TimestampTicks,
            ERenderQueryKind.ElapsedTime => ERenderQueryField.TimestampTicks,
            ERenderQueryKind.TransformFeedback => valueIndex == 0u
                ? ERenderQueryField.PrimitivesWritten
                : ERenderQueryField.PrimitivesNeeded,
            ERenderQueryKind.PrimitivesGenerated => ERenderQueryField.PrimitivesGenerated,
            ERenderQueryKind.MeshPrimitivesGenerated => ERenderQueryField.MeshPrimitivesGenerated,
            ERenderQueryKind.AccelerationStructureProperty or ERenderQueryKind.MicromapProperty => ERenderQueryField.PropertyValue,
            ERenderQueryKind.PerformanceCounter => ERenderQueryField.PerformanceCounter,
            ERenderQueryKind.VideoResultStatus => ERenderQueryField.VideoStatus,
            _ => ERenderQueryField.None,
        };
    }

    public static uint CountStatistics(ERenderPipelineStatistics statistics)
        => (uint)BitOperations.PopCount((uint)statistics);

    private static ERenderQueryField GetPipelineStatisticField(
        uint valueIndex,
        ERenderPipelineStatistics statistics)
    {
        uint ordinal = 0u;
        for (uint bit = 0u; bit < 13u; bit++)
        {
            if (((uint)statistics & (1u << (int)bit)) == 0u)
                continue;
            if (ordinal++ != valueIndex)
                continue;

            return bit switch
            {
                0 => ERenderQueryField.InputAssemblyVertices,
                1 => ERenderQueryField.InputAssemblyPrimitives,
                2 => ERenderQueryField.VertexShaderInvocations,
                3 => ERenderQueryField.GeometryShaderInvocations,
                4 => ERenderQueryField.GeometryShaderPrimitives,
                5 => ERenderQueryField.ClippingInvocations,
                6 => ERenderQueryField.ClippingPrimitives,
                7 => ERenderQueryField.FragmentShaderInvocations,
                8 => ERenderQueryField.TessellationControlShaderPatches,
                9 => ERenderQueryField.TessellationEvaluationShaderInvocations,
                10 => ERenderQueryField.ComputeShaderInvocations,
                11 => ERenderQueryField.TaskShaderInvocations,
                12 => ERenderQueryField.MeshShaderInvocations,
                _ => ERenderQueryField.None,
            };
        }

        return ERenderQueryField.None;
    }
}
