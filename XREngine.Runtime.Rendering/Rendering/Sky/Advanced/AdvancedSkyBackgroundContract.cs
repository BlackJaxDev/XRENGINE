using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Operational contract for visibility-sentinel background pixels and compute sky shading.
/// </summary>
public static class AdvancedSkyBackgroundContract
{
    /// <summary>
    /// Alpha channel written for background/sky pixels (0.0 enables clean XR video passthrough and alpha compositing).
    /// </summary>
    public const float BackgroundAlpha = 0.0f;

    /// <summary>
    /// Alpha channel written for opaque scene geometry (1.0).
    /// </summary>
    public const float SceneOpaqueAlpha = 1.0f;

    /// <summary>
    /// Evaluates whether a DrawId represents a visibility-sentinel background pixel.
    /// </summary>
    public static bool IsBackgroundPixel(uint drawId) => drawId == 0u;
}
