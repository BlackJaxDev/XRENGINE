namespace XREngine.Rendering;

/// <summary>
/// Execution descriptor and compatibility state for special effects and simulation geometry.
/// </summary>
public sealed record AdvancedSpecialEffectDescriptor
{
    public EAdvancedSpecialEffectLane Lane { get; init; }
    public bool IsSupported { get; init; }
    public bool RequiresDepthPrePass { get; init; }
    public bool DisplacesGeometry { get; init; }
    public string? UnsupportedReason { get; init; }

    public AdvancedSpecialEffectDescriptor(
        EAdvancedSpecialEffectLane lane,
        bool isSupported = true,
        bool requiresDepthPrePass = false,
        bool displacesGeometry = false,
        string? unsupportedReason = null)
    {
        Lane = lane;
        IsSupported = isSupported;
        RequiresDepthPrePass = requiresDepthPrePass;
        DisplacesGeometry = displacesGeometry;
        UnsupportedReason = unsupportedReason;
    }
}
