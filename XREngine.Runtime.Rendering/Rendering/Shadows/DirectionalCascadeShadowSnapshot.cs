using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Shadows;

/// <summary>
/// Copy of one directional cascade as the Advanced shading reads it: the matrix and split that the
/// cascade tile was rendered with, the atlas page texture and layer that hold the tile, the UV
/// rectangle of the tile in that layer, and the residency and fallback state. Diagnostic tools read
/// these values on demand; no frame path creates them.
/// </summary>
/// <param name="Source">The cascade set: <see cref="ShadowRequestSource.Desktop"/> or <see cref="ShadowRequestSource.Hmd"/>.</param>
/// <param name="CascadeIndex">Cascade index in its set.</param>
/// <param name="CascadeCount">Number of cascades that the set publishes.</param>
/// <param name="RenderedWorldToLight">World-to-shadow matrix that the tile was rendered with.</param>
/// <param name="CurrentWorldToLight">World-to-shadow matrix of the current cascade fit.</param>
/// <param name="RenderedSplitBlendBias">Split far distance, blend width and bias values of the rendered tile.</param>
/// <param name="UvScaleBias">UV scale (xy) and offset (zw) of the tile in its atlas layer.</param>
/// <param name="PageTexture">Atlas page texture array, or null when the page has no texture.</param>
/// <param name="PageIndex">Atlas page index, which is also the texture array layer.</param>
/// <param name="Encoding">Depth encoding of the atlas page.</param>
/// <param name="Resident">True when the allocation is resident and has been rendered at least once.</param>
/// <param name="Sampleable">True when the published record marks the tile as sampleable.</param>
/// <param name="StaticCacheBacked">True when the static cache owns the tile.</param>
/// <param name="ActiveFallback">Fallback that the shading uses for the tile.</param>
/// <param name="LastRenderedFrame">Atlas frame of the last tile render.</param>
/// <param name="AtlasFrame">Atlas frame at the time of the snapshot.</param>
public readonly record struct DirectionalCascadeShadowSnapshot(
    ShadowRequestSource Source,
    int CascadeIndex,
    int CascadeCount,
    Matrix4x4 RenderedWorldToLight,
    Matrix4x4 CurrentWorldToLight,
    Vector4 RenderedSplitBlendBias,
    Vector4 UvScaleBias,
    XRTexture2DArray? PageTexture,
    int PageIndex,
    EShadowMapEncoding Encoding,
    bool Resident,
    bool Sampleable,
    bool StaticCacheBacked,
    ShadowFallbackMode ActiveFallback,
    ulong LastRenderedFrame,
    ulong AtlasFrame);
