namespace XREngine.Rendering;

/// <summary>
/// Shadow residency and reuse state.
/// </summary>
[Flags]
public enum EAdvancedShadowRecordFlags : uint
{
    None = 0,
    Resident = 1u << 0,
    StaticCache = 1u << 1,
    StaleFallback = 1u << 2,
    MomentEncoded = 1u << 3,
    /// <summary>Record was captured from the HMD directional-cascade source.</summary>
    HmdSource = 1u << 4,
    DepthZeroToOne = 1u << 5,
    FramebufferTextureYDown = 1u << 6,
    ReversedDepth = 1u << 7,
    /// <summary>Moment storage linearizes perspective depth into the rendered near/far range.</summary>
    LinearizedPerspectiveMoments = 1u << 8,
}
