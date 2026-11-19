namespace XREngine.Imaging;

/// <summary>Sampling behavior for an image resize performed by an installed image backend.</summary>
public enum RuntimeImageResizeMode
{
    Standard,
    Bilinear,
    Lanczos,
    Adaptive,
    /// <summary>Average each channel without using alpha to weight RGB.</summary>
    IndependentChannels,
}
