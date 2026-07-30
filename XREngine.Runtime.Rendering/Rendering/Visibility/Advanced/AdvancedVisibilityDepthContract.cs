namespace XREngine.Rendering;

/// <summary>
/// Depth and jitter policy shared by normal, reversed, mono, stereo, and capture visibility passes.
/// </summary>
public readonly record struct AdvancedVisibilityDepthContract(
    bool DepthZeroToOne,
    bool ReversedDepth,
    bool RasterUsesJitteredProjection,
    bool MotionUsesUnjitteredProjection)
{
    public static AdvancedVisibilityDepthContract Create(
        bool depthZeroToOne,
        bool reversedDepth)
        => new(
            depthZeroToOne,
            reversedDepth,
            RasterUsesJitteredProjection: true,
            MotionUsesUnjitteredProjection: true);

    public float ClearValue => ReversedDepth ? 0.0f : 1.0f;

    public bool IsNearer(float candidate, float current)
        => ReversedDepth ? candidate > current : candidate < current;
}
