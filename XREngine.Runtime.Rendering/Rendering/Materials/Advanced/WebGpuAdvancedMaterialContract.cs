using XREngine.Rendering.Materials;

namespace XREngine.Rendering;

/// <summary>Native material schema, kernel and finite texture-bank contracts shared by publication and WebGPU execution.</summary>
public static class WebGpuAdvancedMaterialContract
{
    public const int TextureSlotCount = 12;
    public const int MaximumCohorts = 128;
    public const int MaximumMaterialCohorts = MaximumCohorts - 1;
    public static readonly ulong MirrorLayoutHash = Hash(MaterialBindingLayouts.ProjectiveMirror.LayoutHash);
    private static readonly ulong DeferredLayout = Hash(MaterialBindingLayouts.OpaqueDeferred.LayoutHash);
    private static readonly ulong ForwardLayout = Hash(MaterialBindingLayouts.ForwardOpaque.LayoutHash);
    private static readonly ulong MaskedLayout = Hash(MaterialBindingLayouts.MaskedForward.LayoutHash);

    public static bool IsStandard(in AdvancedMaterialRecord material)
        => material.MaterialLayoutHash == DeferredLayout || material.MaterialLayoutHash == ForwardLayout ||
            material.MaterialLayoutHash == MaskedLayout;

    public static int GetTextureDimensionCapacity(EAdvancedTextureDimension dimension) => dimension switch
    {
        EAdvancedTextureDimension.Texture2D => 10,
        EAdvancedTextureDimension.Cube or EAdvancedTextureDimension.Texture2DArray => 1,
        _ => 0,
    };

    public static bool SupportsCullMode(uint mode) => mode <= 1;

    /// <summary>Checks the typed texture/sampler pair independently of physical GPU allocation.</summary>
    public static string? GetTexturePairRejection(in AdvancedTextureRecord texture, in AdvancedSamplerRecord sampler,
        bool allowDepthComparison, out bool depthComparison)
    {
        depthComparison = (texture.Flags & EAdvancedTextureRecordFlags.Depth) != 0;
        bool comparison = (sampler.Flags & EAdvancedSamplerRecordFlags.ComparisonEnabled) != 0;
        if (depthComparison != comparison || depthComparison && (!allowDepthComparison ||
            texture.Dimension != EAdvancedTextureDimension.Texture2D || sampler.CompareOperation != EAdvancedCompareOperation.LessOrEqual))
            return "Native shadows require the exact 2D less-equal depth-comparison companion; material roles and other bank dimensions require ordinary color sampling.";
        if (GetTextureDimensionCapacity(texture.Dimension) == 0)
            return "The texture dimension has no native bank companion.";
        return GetSamplerRejection(in sampler);
    }

    /// <summary>Checks the complete per-material union of global and local sampled resources.</summary>
    public static string? GetTextureBankRejection(int color2D, int depth2D, int cube, int array)
        => color2D < 0 || depth2D < 0 || cube < 0 || array < 0 || depth2D > 1 ||
            color2D + depth2D > GetTextureDimensionCapacity(EAdvancedTextureDimension.Texture2D) ||
            cube > GetTextureDimensionCapacity(EAdvancedTextureDimension.Cube) ||
            array > GetTextureDimensionCapacity(EAdvancedTextureDimension.Texture2DArray) ||
            color2D + depth2D + cube + array > TextureSlotCount
            ? "Native closure exceeds ten 2D pairs (at most one depth-comparison pair), one color cube, and one color 2D-array; the depth companion retains nine independent color 2D pairs within the device's 16 sampled-texture limit."
            : null;

    /// <summary>Checks the exact frozen sampler state consumed by the browser native bank.</summary>
    public static string? GetSamplerRejection(in AdvancedSamplerRecord sampler)
    {
        const string reason = "The frozen sampler requires a comparison other than less-equal, border, LOD bias, or filtering state outside the exact native bank contract.";
        const EAdvancedSamplerRecordFlags known = EAdvancedSamplerRecordFlags.UsesMipmaps |
            EAdvancedSamplerRecordFlags.LinearMipmapInterpolation | EAdvancedSamplerRecordFlags.NearestMinification |
            EAdvancedSamplerRecordFlags.NearestMagnification | EAdvancedSamplerRecordFlags.ComparisonEnabled |
            EAdvancedSamplerRecordFlags.AnisotropyEnabled;
        if (sampler.Filter is not (EAdvancedSamplerFilter.Nearest or EAdvancedSamplerFilter.Linear or EAdvancedSamplerFilter.Anisotropic) ||
            (sampler.Flags & ~known) != 0 ||
            (sampler.Flags & EAdvancedSamplerRecordFlags.ComparisonEnabled) != 0 && sampler.CompareOperation != EAdvancedCompareOperation.LessOrEqual ||
            sampler.LodBiasMinMaxAnisotropy.X != 0 || !float.IsFinite(sampler.LodBiasMinMaxAnisotropy.Y) ||
            !float.IsFinite(sampler.LodBiasMinMaxAnisotropy.Z) ||
            !SupportsAddress(sampler.AddressU) || !SupportsAddress(sampler.AddressV) || !SupportsAddress(sampler.AddressW))
            return reason;
        bool mips = (sampler.Flags & EAdvancedSamplerRecordFlags.UsesMipmaps) != 0;
        float anisotropy = (sampler.Flags & EAdvancedSamplerRecordFlags.AnisotropyEnabled) != 0 ? sampler.LodBiasMinMaxAnisotropy.W : 1;
        if (!float.IsFinite(anisotropy) || anisotropy != MathF.Truncate(anisotropy) || anisotropy is < 1 or > 16 ||
            anisotropy > 1 && ((sampler.Flags & (EAdvancedSamplerRecordFlags.NearestMinification | EAdvancedSamplerRecordFlags.NearestMagnification)) != 0 ||
                (sampler.Flags & EAdvancedSamplerRecordFlags.LinearMipmapInterpolation) == 0))
            return reason;
        float minLod = mips ? Math.Max(0, sampler.LodBiasMinMaxAnisotropy.Y) : 0;
        float maxLod = mips ? Math.Min(32, sampler.LodBiasMinMaxAnisotropy.Z) : 0;
        return minLod > maxLod || minLod > 32 || maxLod < 0 ||
            !mips && (sampler.LodBiasMinMaxAnisotropy.Y > 0 || sampler.LodBiasMinMaxAnisotropy.Z < 0) ? reason : null;

        static bool SupportsAddress(EAdvancedSamplerAddressMode address)
            => address is EAdvancedSamplerAddressMode.Repeat or EAdvancedSamplerAddressMode.MirroredRepeat or EAdvancedSamplerAddressMode.ClampToEdge;
    }

    public static string? GetSamplingRejection(string format, uint samples, bool? float32Filterable, bool depthComparison = false)
        => samples != 1 || WebGpuTextureFormatContract.IsDepth(format) != depthComparison || WebGpuTextureFormatContract.IsInteger(format) ||
            float32Filterable == false && format is ("r32float" or "rg32float" or "rgba32float")
            ? "Native bank sampling requires an exact single-sample filterable color texture or the typed depth-comparison companion."
            : null;

    public static string? GetSourceRejection(EAdvancedMaterialSourceContract source) => source switch
    {
        EAdvancedMaterialSourceContract.StandardSurface or EAdvancedMaterialSourceContract.EngineGeneratedSurface or
            EAdvancedMaterialSourceContract.ProjectiveMirror => null,
        EAdvancedMaterialSourceContract.AuthoredStandardSurface => "An authored raster cook identity does not prove native surface equivalence; verified engine-generated provenance or an executable native shading companion is required.",
        EAdvancedMaterialSourceContract.UnsupportedTextureSemantics => "Independent red-channel Metallic/Roughness textures require an exact native companion; the canonical RM slot samples roughness/metallic from one texture's RG channels.",
        EAdvancedMaterialSourceContract.CustomVertexProgram => "Authored vertex displacement or replacement requires an exact native vertex companion.",
        EAdvancedMaterialSourceContract.CustomSurfaceProgram => "The authored surface program has no exact canonical native shading companion.",
        _ => "The material has no explicit canonical native source contract.",
    };

    public static string? GetVertexFeatureRejection(EAdvancedMaterialFeatureFlags features)
        => (features & EAdvancedMaterialFeatureFlags.VertexDeformation) != 0
            ? "Material displacement requires its exact native vertex companion."
            : null;

    public static bool IsCanonicalKernel(in AdvancedMaterialRecord material, in AdvancedShadingKernelRecord kernel)
    {
        bool mirror = material.MaterialLayoutHash == MirrorLayoutHash;
        if (!mirror && !IsStandard(in material)) return false;
        uint coverage = (uint)material.CoverageMode, state = (uint)material.RenderStateClass;
        ulong identity = unchecked((material.MaterialLayoutHash ^ (((ulong)coverage << 32) | state)) * 1099511628211ul);
        EAdvancedMaterialRequiredAttributeMask attributes = mirror ? EAdvancedMaterialRequiredAttributeMask.Position :
            EAdvancedMaterialRequiredAttributeMask.Position | EAdvancedMaterialRequiredAttributeMask.Normal |
            EAdvancedMaterialRequiredAttributeMask.Tangent | EAdvancedMaterialRequiredAttributeMask.TexCoord0 |
            EAdvancedMaterialRequiredAttributeMask.TexCoord1 | EAdvancedMaterialRequiredAttributeMask.Color0 |
            EAdvancedMaterialRequiredAttributeMask.AnalyticalDerivatives;
        EAdvancedMaterialEligibilityFlags eligibility = EAdvancedMaterialEligibilityFlags.NativeOpaque | EAdvancedMaterialEligibilityFlags.Unlit;
        if (!mirror) eligibility |= EAdvancedMaterialEligibilityFlags.NativeMasked | EAdvancedMaterialEligibilityFlags.LateTransparent | EAdvancedMaterialEligibilityFlags.LateRefractive;
        EAdvancedMaterialFeatureFlags features = EAdvancedMaterialFeatureFlags.DoubleSided;
        if (!mirror) features |= EAdvancedMaterialFeatureFlags.BaseColorTexture | EAdvancedMaterialFeatureFlags.NormalTexture |
            EAdvancedMaterialFeatureFlags.MetallicRoughnessTexture | EAdvancedMaterialFeatureFlags.Emissive |
            EAdvancedMaterialFeatureFlags.ReceivesShadows | EAdvancedMaterialFeatureFlags.CastsShadows |
            EAdvancedMaterialFeatureFlags.VertexDeformation | EAdvancedMaterialFeatureFlags.Animated;
        return kernel.RequiredAttributeMask == attributes && kernel.SupportedEligibility == eligibility &&
            kernel.SupportedFeatures == features && kernel.Flags == 0 &&
            coverage < 32 && state < 32 && kernel.MaterialLayoutHash == material.MaterialLayoutHash &&
            kernel.ShaderIdentityHash == identity && kernel.SupportedCoverageMask == 1u << (int)coverage &&
            kernel.RenderStateClassMask == 1u << (int)state &&
            (material.FeatureFlags & ~kernel.SupportedFeatures) == 0 &&
            (material.EligibilityFlags & ~kernel.SupportedEligibility) == 0;
    }
    private static ulong Hash(string value)
    {
        ulong hash = 14695981039346656037ul;
        foreach (char character in value)
            hash = unchecked((hash ^ character) * 1099511628211ul);
        return hash;
    }
}
