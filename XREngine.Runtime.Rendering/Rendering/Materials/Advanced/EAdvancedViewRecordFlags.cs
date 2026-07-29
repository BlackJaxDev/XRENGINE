namespace XREngine.Rendering;

/// <summary>
/// View topology and depth convention encoded in a view record.
/// </summary>
[Flags]
public enum EAdvancedViewRecordFlags : uint
{
    None = 0,
    DepthZeroToOne = 1u << 0,
    ReversedDepth = 1u << 1,
    StereoLeft = 1u << 2,
    StereoRight = 1u << 3,
    Foveated = 1u << 4,
    Mirror = 1u << 5,
}
