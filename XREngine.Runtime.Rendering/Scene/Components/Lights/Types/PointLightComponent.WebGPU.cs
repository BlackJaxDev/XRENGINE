using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Capture.Lights.Types;

public partial class PointLightComponent
{
    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        if (propName == nameof(ShadowMap) && prev is XRMaterialFrameBuffer previous &&
            ReferenceEquals(previous, _perFaceFboShadowMap) && !ReferenceEquals(previous, ShadowMap) &&
            previous.Material?.EngineSemantic == EngineMaterialSemanticIdentity.OpaquePointShadowDepthV1)
        {
            for (int face = 0; face < _perFaceFbos.Length; face++)
            {
                _perFaceFbos[face]?.Destroy();
                _perFaceFbos[face] = null;
            }
            SetField(ref _perFaceFboShadowMap, null, publishNotifications: false);
        }
        base.OnPropertyChanged(propName, prev, field);
    }

    /// <summary>The color cube stores radial distance; its depth cube only selects nearest surfaces.</summary>
    internal XRTextureCube? CookedShadowReceiverTexture
        => ShadowMap?.Material?.Textures is { Count: 2 } textures ? textures[1] as XRTextureCube : null;

    /// <summary>Checks the explicit portable point-shadow contract without changing authored settings.</summary>
    public void ValidateCookedShadowConfiguration()
    {
        // Match the shared perspective camera's minimum near plane without
        // letting its defensive clamp change the radial receiver's contract.
        if (!float.IsFinite(ShadowNearPlaneDistance) || ShadowNearPlaneDistance < 0.001f)
            throw new NotSupportedException("WebGPU.PointShadow.NearPlaneUnsupported: ShadowNearPlaneDistance must be finite and at least 0.001.");
        if (!float.IsFinite(Radius) || Radius <= ShadowNearPlaneDistance || Radius < ShadowNearPlaneDistance + 0.001f)
            throw new NotSupportedException("WebGPU.PointShadow.RangeUnsupported: Radius must be finite and exceed ShadowNearPlaneDistance by at least 0.001 so it is the actual shadow far plane.");
        if (UseShadowAtlas || ShadowRenderMode != EPointShadowRenderMode.Sequential ||
            ShadowMapEncoding != EShadowMapEncoding.Depth || ShadowMapStorageFormat != EShadowMapStorageFormat.R16Float ||
            EnableContactShadows || SoftShadowMode != ESoftShadowMode.ContactHardeningPcss || BlockerSamples != 8 || FilterSamples != 8 ||
            ShadowMapResolutionWidth == 0 || ShadowMapResolutionHeight == 0)
            throw new NotSupportedException("WebGPU.PointShadow.ProfileUnsupported: requires authored Sequential standalone R16Float radial depth, PCSS 8/8, no contact shadows, and effective square dimensions at most 2048.");
    }

    private XRMaterial CreateCookedShadowMaterial(uint width, uint height)
    {
        ValidateCookedShadowConfiguration();
        if (width != height || width is 0 or > 2048)
            throw new NotSupportedException("WebGPU.PointShadow.ResolutionUnsupported: the six cube faces require positive square effective dimensions at most 2048.");
        XRTextureCube depth = CreateCookedShadowCube(width, EPixelInternalFormat.DepthComponent24,
            EPixelFormat.DepthComponent, EPixelType.UnsignedInt, EFrameBufferAttachment.DepthAttachment);
        XRTextureCube radial = CreateCookedShadowCube(width, EPixelInternalFormat.R16f,
            EPixelFormat.Red, EPixelType.HalfFloat, EFrameBufferAttachment.ColorAttachment0);
        depth.Name = "Point shadow face depth";
        radial.Name = "Point shadow radial distance";
        radial.SamplerName = "ShadowMap";
        XRMaterial material = new([depth, radial]) { EngineSemantic = EngineMaterialSemanticIdentity.OpaquePointShadowDepthV1 };
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
        material.RenderOptions.DepthTest = new DepthTest { Enabled = ERenderParamUsage.Enabled, Function = EComparison.Lequal, UpdateDepth = true };
        return material;
    }

    private static XRTextureCube CreateCookedShadowCube(uint extent, EPixelInternalFormat format,
        EPixelFormat pixels, EPixelType type, EFrameBufferAttachment attachment)
        => new(extent, format, pixels, type, false)
        {
            FrameBufferAttachment = attachment,
            Resizable = false,
            AutoGenerateMipmaps = false,
            MinFilter = ETexMinFilter.Nearest,
            MagFilter = ETexMagFilter.Nearest,
            UWrap = ETexWrapMode.ClampToEdge,
            VWrap = ETexWrapMode.ClampToEdge,
            WWrap = ETexWrapMode.ClampToEdge,
            LargestMipmapLevel = 0,
            SmallestAllowedMipmapLevel = 0,
        };
}
