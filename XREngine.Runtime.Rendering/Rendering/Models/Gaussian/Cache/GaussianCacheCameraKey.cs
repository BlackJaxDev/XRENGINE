using System.Numerics;

namespace XREngine.Rendering.Models.Gaussian.Cache;

/// <summary>One key of a camera path.</summary>
/// <param name="TimeSeconds">The key time in seconds.</param>
/// <param name="Position">The camera position.</param>
/// <param name="Rotation">The camera rotation.</param>
public readonly record struct GaussianCacheCameraKey(float TimeSeconds, Vector3 Position, Quaternion Rotation);
