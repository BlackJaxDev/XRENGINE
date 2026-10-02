using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Capture.Lights.Types;

public partial class SpotLightComponent
{
    protected override void OnComponentActivated()
    {
        if (RuntimeEngineMaterialConstructionServices.Target == EngineMaterialConstructionTarget.WebGpuCooked && CastsShadows)
            ValidateCookedShadowConfiguration();
        base.OnComponentActivated();
    }

    /// <summary>The light-owned projected-depth color target sampled by the cooked receiver.</summary>
    internal XRTexture2D? CookedShadowReceiverTexture
        => ShadowMap?.Material?.Textures is { Count: 2 } textures ? textures[1] as XRTexture2D : null;

    /// <summary>Checks the explicit portable spot-shadow contract without changing authored settings.</summary>
    public void ValidateCookedShadowConfiguration()
    {
        if (!float.IsFinite(ShadowNearPlaneDistance) || ShadowNearPlaneDistance < 0.001f)
            throw new NotSupportedException("WebGPU.SpotShadow.NearPlaneUnsupported: ShadowNearPlaneDistance must be finite and at least 0.001.");
        if (!float.IsFinite(Distance) || Distance <= ShadowNearPlaneDistance || Distance < ShadowNearPlaneDistance + 0.001f)
            throw new NotSupportedException("WebGPU.SpotShadow.RangeUnsupported: Distance must be finite and exceed ShadowNearPlaneDistance by at least 0.001 so it is the actual shadow far plane.");
        float outer = OuterCutoffAngleDegrees;
        float inner = InnerCutoffAngleDegrees;
        if (!float.IsFinite(outer) || outer <= 0.0f || outer >= 90.0f)
            throw new NotSupportedException("WebGPU.SpotShadow.OuterConeUnsupported: OuterCutoffAngleDegrees must be finite and strictly between zero and 90 for a nonsingular perspective shadow projection.");
        if (!float.IsFinite(inner) || inner < 0.0f || inner > outer)
            throw new NotSupportedException("WebGPU.SpotShadow.InnerConeUnsupported: InnerCutoffAngleDegrees must be finite, nonnegative, and no greater than OuterCutoffAngleDegrees.");
        if (!float.IsFinite(Exponent) || Exponent <= 0.0f)
            throw new NotSupportedException("WebGPU.SpotShadow.ExponentUnsupported: Exponent must be finite and positive so zero-cosine receivers have defined cone attenuation.");
        if (UseShadowAtlas || ShadowMapEncoding != EShadowMapEncoding.Depth ||
            ShadowMapStorageFormat != EShadowMapStorageFormat.R16Float || EnableContactShadows ||
            SoftShadowMode != ESoftShadowMode.ContactHardeningPcss || BlockerSamples != 8 || FilterSamples != 8 ||
            ShadowMapResolutionWidth is 0 or > 2048 || ShadowMapResolutionHeight is 0 or > 2048)
            throw new NotSupportedException("WebGPU.SpotShadow.ProfileUnsupported: requires standalone R16Float projected depth, PCSS 8/8, no contact shadows, and dimensions at most 2048.");
    }

    private XRMaterial CreateCookedShadowMaterial(uint width, uint height)
    {
        ValidateCookedShadowConfiguration();
        XRTexture2D depth = CreateCookedShadowTexture(width, height, EPixelInternalFormat.DepthComponent24,
            EPixelFormat.DepthComponent, EPixelType.UnsignedInt, EFrameBufferAttachment.DepthAttachment);
        XRTexture2D projected = CreateCookedShadowTexture(width, height, EPixelInternalFormat.R16f,
            EPixelFormat.Red, EPixelType.HalfFloat, EFrameBufferAttachment.ColorAttachment0);
        depth.Name = "Spot shadow raster depth";
        depth.SamplerName = "ShadowRasterDepth";
        projected.Name = "Spot shadow projected depth";
        projected.SamplerName = "ShadowMap";
        XRMaterial material = new([depth, projected]) { EngineSemantic = EngineMaterialSemanticIdentity.OpaqueSpotShadowDepthV1 };
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
        material.RenderOptions.DepthTest = new DepthTest { Enabled = ERenderParamUsage.Enabled, Function = EComparison.Lequal, UpdateDepth = true };
        return material;
    }

    private static XRTexture2D CreateCookedShadowTexture(uint width, uint height, EPixelInternalFormat format,
        EPixelFormat pixels, EPixelType type, EFrameBufferAttachment attachment)
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(width, height, format, pixels, type, attachment);
        texture.SizedInternalFormat = XRTexture2D.DeriveESizedInternalFormat(format);
        texture.Resizable = false;
        texture.AutoGenerateMipmaps = false;
        texture.MinFilter = ETexMinFilter.Nearest;
        texture.MagFilter = ETexMagFilter.Nearest;
        texture.UWrap = texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.LargestMipmapLevel = texture.SmallestAllowedMipmapLevel = 0;
        texture.MaxAnisotropy = 1;
        return texture;
    }
}
