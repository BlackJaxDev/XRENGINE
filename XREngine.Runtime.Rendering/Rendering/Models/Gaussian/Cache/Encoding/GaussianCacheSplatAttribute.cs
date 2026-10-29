namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>Names one per-splat attribute substream in a splat streams section.</summary>
public enum GaussianCacheSplatAttribute : byte
{
    Binding = 1,
    Triangle = 2,
    Rotation = 3,
    Scale = 4,
    Color = 5,
    ChildCount = 6,
}
