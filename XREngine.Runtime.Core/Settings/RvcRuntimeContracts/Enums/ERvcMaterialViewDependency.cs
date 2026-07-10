namespace XREngine;

/// <summary>
/// Material view dependencies used by the engine.
/// </summary>
[Flags]
public enum ERvcMaterialViewDependency
{
    /// <summary>
    /// No material view dependency.
    /// </summary>
    None = 0,
    /// <summary>
    /// Sharp specular dependency.
    /// </summary>
    SharpSpecular = 1 << 0,
    /// <summary>
    /// Reflection probe parallax dependency.
    /// </summary>
    ReflectionProbeParallax = 1 << 1,
    /// <summary>
    /// Refraction dependency.
    /// </summary>
    Refraction = 1 << 2,
    /// <summary>
    /// Parallax occlusion dependency.
    /// </summary>
    ParallaxOcclusion = 1 << 3,
    /// <summary>
    /// Virtual displacement dependency.
    /// </summary>
    VirtualDisplacement = 1 << 4,
    /// <summary>
    /// Screen space effect dependency.
    /// </summary>
    ScreenSpaceEffect = 1 << 5,
}
