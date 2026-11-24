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
    /// <summary>
    /// Clip-to-framebuffer texture coordinates increase downward on Y. This
    /// is sealed with the view so compute sampling follows the viewport
    /// convention that rendered the depth attachment.
    /// </summary>
    FramebufferTextureYDown = 1u << 6,
    /// <summary>
    /// The view producer proved a compatible preceding output and camera history.
    /// </summary>
    TemporalHistoryValid = 1u << 7,
    /// <summary>
    /// Excludes projective mirror display materials while rendering a mirror
    /// capture, preventing a capture from sampling its own reflection output.
    /// </summary>
    ExcludeProjectiveMirrors = 1u << 8,
    /// <summary>
    /// The camera of the view enables GTAO normal-map micro-occlusion
    /// (<see cref="GroundTruthAmbientOcclusionSettings.UseMaterialNormals"/>). Native shading then
    /// scales the GTAO value by how far the normal-mapped normal bends from the surface normal.
    /// </summary>
    GtaoMaterialNormals = 1u << 9,
    /// <summary>
    /// Four-bit field (bits 10 through 13) encoding the visibility raster MSAA sample count (2, 4, or 8;
    /// 0 or 1 means single-sample). Vulkan GLSL does not expose <c>gl_NumSamples</c>, so masked
    /// alpha-to-coverage reads the active sample count from the view record.
    /// </summary>
    MsaaSampleCountBit0 = 1u << 10,
    MsaaSampleCountBit1 = 1u << 11,
    MsaaSampleCountBit2 = 1u << 12,
    MsaaSampleCountBit3 = 1u << 13,
}
