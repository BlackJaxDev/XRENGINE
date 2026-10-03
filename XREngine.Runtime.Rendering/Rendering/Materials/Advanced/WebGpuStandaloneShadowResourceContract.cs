using System.Numerics;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Describes the authored standalone receiver schema without creating a texture or asserting producer readiness.</summary>
public static class WebGpuStandaloneShadowResourceContract
{
    /// <summary>Returns the authored storage shape; runtime quality independently limits its physical dimensions.</summary>
    public static bool TryDescribe(LightComponent light, out AdvancedTextureRecord texture,
        out AdvancedSamplerRecord sampler, out string format, out string reason)
    {
        texture = default;
        sampler = default;
        format = string.Empty;
        reason = string.Empty;
        EAdvancedTextureDimension dimension;
        EAdvancedTextureFormatClass formatClass;
        bool depth = false;
        try
        {
            switch (light)
            {
                case DirectionalLightComponent directional:
                    if (directional.UseShadowAtlas || directional.EnableCascadedShadows ||
                        directional.ShadowMapEncoding != EShadowMapEncoding.Depth || directional.EnableContactShadows ||
                        directional.ShadowMapStorageFormat is not (EShadowMapStorageFormat.Depth16 or EShadowMapStorageFormat.Depth24 or EShadowMapStorageFormat.Depth32Float) ||
                        directional.SoftShadowMode != ESoftShadowMode.ContactHardeningPcss || directional.BlockerSamples != 8 || directional.FilterSamples != 8 ||
                        directional.ShadowMapResolutionWidth == 0 || directional.ShadowMapResolutionHeight == 0)
                        throw new NotSupportedException("Standalone directional native shadows require non-cascaded depth, PCSS 8/8, no contact shadows, and positive authored dimensions.");
                    dimension = EAdvancedTextureDimension.Texture2D;
                    depth = true;
                    formatClass = directional.ShadowMapStorageFormat switch
                    {
                        EShadowMapStorageFormat.Depth16 => EAdvancedTextureFormatClass.Depth16,
                        EShadowMapStorageFormat.Depth24 => EAdvancedTextureFormatClass.Depth24,
                        _ => EAdvancedTextureFormatClass.Depth32Float,
                    };
                    format = WebGpuTextureFormatContract.Map(LightComponent.GetShadowMapTextureFormat(directional.ShadowMapStorageFormat).SizedInternalFormat);
                    break;
                case PointLightComponent point:
                    point.ValidateCookedShadowConfiguration();
                    dimension = EAdvancedTextureDimension.Cube;
                    formatClass = EAdvancedTextureFormatClass.R16Float;
                    format = WebGpuTextureFormatContract.Map(ESizedInternalFormat.R16f);
                    break;
                case SpotLightComponent spot:
                    spot.ValidateCookedShadowConfiguration();
                    dimension = EAdvancedTextureDimension.Texture2D;
                    formatClass = EAdvancedTextureFormatClass.R16Float;
                    format = WebGpuTextureFormatContract.Map(ESizedInternalFormat.R16f);
                    break;
                default:
                    throw new NotSupportedException("The light has no standalone browser native shadow producer.");
            }
        }
        catch (NotSupportedException error)
        {
            reason = error.Message;
            return false;
        }
        (uint width, uint height) = light.GetShadowMapStorageResolution(light.ShadowMapResolutionWidth, light.ShadowMapResolutionHeight);
        texture = new()
        {
            Dimension = dimension,
            Flags = depth ? EAdvancedTextureRecordFlags.Depth : EAdvancedTextureRecordFlags.None,
            Width = width,
            Height = height,
            DepthOrLayers = dimension == EAdvancedTextureDimension.Cube ? 6u : 1u,
            MipCount = 1,
            FormatClass = (uint)formatClass,
            UvScaleBias = new Vector4(1, 1, 0, 0),
        };
        sampler = new()
        {
            Filter = EAdvancedSamplerFilter.Nearest,
            Flags = EAdvancedSamplerRecordFlags.NearestMinification | EAdvancedSamplerRecordFlags.NearestMagnification |
                (depth ? EAdvancedSamplerRecordFlags.ComparisonEnabled : EAdvancedSamplerRecordFlags.None),
            AddressU = EAdvancedSamplerAddressMode.ClampToEdge,
            AddressV = EAdvancedSamplerAddressMode.ClampToEdge,
            AddressW = EAdvancedSamplerAddressMode.ClampToEdge,
            CompareOperation = depth ? EAdvancedCompareOperation.LessOrEqual : EAdvancedCompareOperation.Never,
            LodBiasMinMaxAnisotropy = new Vector4(0, 0, 0, 1),
            BorderColor = new Vector4(0, 0, 0, 1),
        };
        reason = WebGpuAdvancedMaterialContract.GetTexturePairRejection(in texture, in sampler, true, out bool comparison) ??
            WebGpuAdvancedMaterialContract.GetSamplingRejection(format, 1, null, comparison) ?? string.Empty;
        return reason.Length == 0;
    }
}
