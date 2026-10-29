using System.Numerics;

namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>Holds the preferred camera setup for a cache.</summary>
public sealed class GaussianCacheCameraHints
{
    public Vector3 OrbitTarget { get; set; }
    public float MinRadius { get; set; }
    public float MaxRadius { get; set; }
    public float DefaultRadius { get; set; }
    public float DefaultYawDegrees { get; set; }
    public float DefaultPitchDegrees { get; set; }

    /// <summary>The camera path keys, in time order.</summary>
    public GaussianCacheCameraKey[] Path { get; set; } = [];
}
