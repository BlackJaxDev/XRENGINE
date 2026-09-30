namespace XREngine.Imaging;

/// <summary>Sampling behavior for an image resize performed by an installed image backend.</summary>
public enum RuntimeImageResizeMode
{
    Standard,
    Bilinear,
    Lanczos,
    Adaptive,
}
