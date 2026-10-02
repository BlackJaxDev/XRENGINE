using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Shadow transform, atlas placement, and residency consumed by native shading.
/// </summary>
[XREngine.Rendering.Shaders.GpuRecord("XRAdvancedShadowRecord")]
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
    public Vector4 UvScaleBias;

    /// <summary>
    /// Receiver bias and filter controls in normalized shadow depth. For
    /// <see cref="EAdvancedShadowType.DirectionalCascade"/>, X is the constant depth
    /// floor and Y is the depth spanned per authored texel of receiver slope, which
    /// native shading scales by tan(theta). For other types, X and Y are the minimum
    /// and maximum depth bias interpolated by <c>1 - N.L</c>. Z is the world-space
    /// normal offset and W is the filter radius in texels.
    /// </summary>
    public Vector4 DepthBiasAndFilter;

    public uint TextureLayer;
    public uint Encoding;
    public uint CascadeOffset;
    public uint CascadeCount;

    public uint ViewMaskLo;
    public uint ViewMaskHi;
    public uint LastRenderedFrameLo;
    public uint LastRenderedFrameHi;

    /// <summary>Variance floor, light-bleed reduction, positive and negative EVSM exponents.</summary>
    public Vector4 MomentParameters;

    /// <summary>Rendered near/far planes and directional cascade split/blend distances.</summary>
    public Vector4 DepthRangeAndCascade;

    /// <summary>Receipt-stamped point/cube light origin and radial depth range.</summary>
    public Vector4 RenderedLightPositionAndFar;
}
