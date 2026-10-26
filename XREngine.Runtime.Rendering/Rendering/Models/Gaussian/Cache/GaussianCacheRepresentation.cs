namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>Tells how a cache stores its motion.</summary>
public enum GaussianCacheRepresentation : byte
{
    /// <summary>A driver mesh moves the splats.</summary>
    BakedDriver = 1,

    /// <summary>A trained four-dimensional model. This value is reserved and not readable.</summary>
    Trained4D = 2,
}
