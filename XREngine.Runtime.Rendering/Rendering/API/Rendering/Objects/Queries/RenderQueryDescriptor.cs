namespace XREngine.Rendering;

/// <summary>
/// Immutable description of a render query. The descriptor completely defines
/// native-pool compatibility and result semantics.
/// </summary>
public readonly record struct RenderQueryDescriptor(
    ERenderQueryKind Kind,
    EOcclusionResultMode OcclusionMode = EOcclusionResultMode.AnySamplesPassed,
    ERenderPipelineStatistics Statistics = ERenderPipelineStatistics.None,
    uint StreamIndex = 0,
    ERenderQueryProperty Property = ERenderQueryProperty.None,
    uint ProviderValueCount = 0)
{
    public static RenderQueryDescriptor BooleanOcclusion { get; } = new(
        ERenderQueryKind.Occlusion,
        EOcclusionResultMode.AnySamplesPassed);

    public static RenderQueryDescriptor ConservativeOcclusion { get; } = new(
        ERenderQueryKind.Occlusion,
        EOcclusionResultMode.AnySamplesPassedConservative);

    public static RenderQueryDescriptor ExactOcclusion { get; } = new(
        ERenderQueryKind.Occlusion,
        EOcclusionResultMode.ExactSamplesPassed);

    public static RenderQueryDescriptor Timestamp { get; } = new(ERenderQueryKind.Timestamp);

    public static RenderQueryDescriptor ElapsedTime { get; } = new(ERenderQueryKind.ElapsedTime);

    /// <summary>
    /// Returns the number of native query slots required for one recorded use.
    /// </summary>
    public uint ResolveQueryCount(uint viewSlotCount = 1u)
    {
        uint occupiedViews = Math.Max(viewSlotCount, 1u);
        return Kind switch
        {
            ERenderQueryKind.Occlusion => occupiedViews,
            ERenderQueryKind.ElapsedTime => 2u,
            _ => 1u,
        };
    }
}
