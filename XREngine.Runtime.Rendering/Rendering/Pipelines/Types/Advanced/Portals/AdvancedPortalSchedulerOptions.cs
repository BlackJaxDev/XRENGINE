namespace XREngine.Rendering;

/// <summary>
/// Bounded scheduling policy for <see cref="AdvancedPortalViewScheduler"/>.
/// Limits stay within the 5-bit scene stencil budget (<see cref="AdvancedStencilBitLayout.MaxPortalNodeId"/>)
/// and the fixed portal visibility pool ceiling.
/// </summary>
public readonly record struct AdvancedPortalSchedulerOptions
{
    /// <summary>Hard ceiling on mirror/portal recursion depth.</summary>
    public const int HardMaxRecursionDepth = 4;

    /// <summary>Hard ceiling on active portal nodes per primary eye layer (5-bit stencil IDs 1..31).</summary>
    public const int HardMaxPortalViewsPerEye = (int)AdvancedStencilBitLayout.MaxPortalNodeId;

    /// <summary>Hard ceiling on total active portal sub-views uploaded in one frame publication.</summary>
    public const int HardMaxTotalPortalViewsPerFrame = 32;

    /// <summary>Default scheduling policy (depth 1, up to 4 per eye, 8 total per frame).</summary>
    public static AdvancedPortalSchedulerOptions Default => new();

    /// <summary>Creates scheduling options initialized to the default portal policy.</summary>
    public AdvancedPortalSchedulerOptions()
    {
    }

    /// <summary>Maximum recursion depth (<c>0..4</c>; default <c>1</c>).</summary>
    public int MaxRecursionDepth { get; init; } = 1;

    /// <summary>Maximum scheduled portal nodes per primary eye (<c>1..31</c>; default <c>4</c>).</summary>
    public int MaxPortalViewsPerEye { get; init; } = 4;

    /// <summary>Maximum total scheduled portal sub-views per frame (<c>1..32</c>; default <c>8</c>).</summary>
    public int MaxTotalPortalViewsPerFrame { get; init; } = 8;

    /// <summary>Minimum clamped projected screen area in pixels required to admit a portal (default <c>256</c>, 16x16).</summary>
    public int MinProjectedPixelArea { get; init; } = 256;

    /// <summary>
    /// Small offset (in world units, default 1 mm) pushing the clip plane behind the mirror
    /// plane to prevent seam cracks where scene geometry touches the mirror frame.
    /// </summary>
    public float ClipPlaneGuardBias { get; init; } = 0.001f;

    /// <summary>Returns an options copy clamped to the engine's hard safety ceilings.</summary>
    public AdvancedPortalSchedulerOptions ClampToHardLimits()
        => new()
        {
            MaxRecursionDepth = Math.Clamp(MaxRecursionDepth, 0, HardMaxRecursionDepth),
            MaxPortalViewsPerEye = Math.Clamp(MaxPortalViewsPerEye, 0, HardMaxPortalViewsPerEye),
            MaxTotalPortalViewsPerFrame = Math.Clamp(MaxTotalPortalViewsPerFrame, 0, HardMaxTotalPortalViewsPerFrame),
            MinProjectedPixelArea = Math.Max(1, MinProjectedPixelArea),
            ClipPlaneGuardBias = float.IsFinite(ClipPlaneGuardBias) ? Math.Max(0.0f, ClipPlaneGuardBias) : 0.001f,
        };
}
