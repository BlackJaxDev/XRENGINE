using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Narrow contract for ambient occlusion providers adapted to the Advanced Render Pipeline.
/// Operates directly on final visibility depth and reconstructed analytical normals.
/// </summary>
public interface IAdvancedAmbientOcclusionProvider
{
    /// <summary>
    /// Stable identifier for this AO provider (e.g. GTAO, HBAO+, SSAO, VoxelAO).
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Whether this provider is supported under the current render hardware and API.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Whether this provider renders at half screen resolution.
    /// </summary>
    bool IsHalfResolution { get; }

    /// <summary>
    /// Whether this provider natively supports stereo/multi-view layers.
    /// </summary>
    bool SupportsStereo { get; }

    /// <summary>
    /// The output pixel format (e.g. R8 or R16F).
    /// </summary>
    EPixelInternalFormat OutputFormat { get; }

    /// <summary>
    /// Human-readable diagnosis if this provider cannot run on the active pipeline.
    /// </summary>
    string? UnsupportedReason { get; }
}
