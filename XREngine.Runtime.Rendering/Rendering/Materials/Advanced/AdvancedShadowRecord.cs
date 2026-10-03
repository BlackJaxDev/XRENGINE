using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Shadow transform, atlas placement, and residency consumed by native shading.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AdvancedShadowRecord
{
    public uint StableShadowId;
    public uint Generation;
    public EAdvancedShadowType Type;
    public EAdvancedShadowRecordFlags Flags;

    public AdvancedTextureReference Texture;
    public Matrix4x4 WorldToShadow;
    public Matrix4x4 PreviousWorldToShadow;
    /// <summary>
    /// Atlas UV placement. A BrowserStandalonePcss spot instead stores its exact
    /// rendered forward direction in xyz and outer-cone cosine in w; its receiver
    /// uses the full texture directly and does not apply an atlas transform.
    /// </summary>
    public Vector4 UvScaleBias;
    /// <summary>
    /// Depth bias/filter data. BrowserStandalonePcss stores depth/slope/normal
    /// bias in xyz and effective light-source radius in w.
    /// </summary>
    public Vector4 DepthBiasAndFilter;

    public uint TextureLayer;
    public uint Encoding;
    public uint CascadeOffset;
    public uint CascadeCount;

    public uint ViewMaskLo;
    public uint ViewMaskHi;
    /// <summary>Rendered frame, or capture frame while BrowserStandaloneCandidate is set.</summary>
    public uint LastRenderedFrameLo;
    public uint LastRenderedFrameHi;

    /// <summary>
    /// Variance floor, light-bleed reduction, positive and negative EVSM exponents.
    /// BrowserStandalonePcss instead stores blocker radius, filter radius, minimum
    /// penumbra and maximum penumbra; its encoding remains Depth.
    /// </summary>
    public Vector4 MomentParameters;

    /// <summary>
    /// Rendered near/far planes and directional cascade split/blend distances.
    /// BrowserStandalonePcss uses zw for the directional constant depth bias and
    /// world-space normal offset, with no cascade split.
    /// </summary>
    public Vector4 DepthRangeAndCascade;

    /// <summary>Receipt-stamped point/cube light origin and radial depth range.</summary>
    public Vector4 RenderedLightPositionAndFar;
}
