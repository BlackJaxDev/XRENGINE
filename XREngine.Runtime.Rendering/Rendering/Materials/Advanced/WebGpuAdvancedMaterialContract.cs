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

    public static string? GetSamplingRejection(string format, uint samples, bool? float32Filterable)
        => samples != 1 || WebGpuTextureFormatContract.IsDepth(format) || WebGpuTextureFormatContract.IsInteger(format) ||
            float32Filterable == false && format is ("r32float" or "rg32float" or "rgba32float")
            ? "Native bank sampling requires an exact filterable non-depth floating-point texture."
            : null;

    public static string? GetSourceRejection(EAdvancedMaterialSourceContract source) => source switch
    {
        EAdvancedMaterialSourceContract.StandardSurface or EAdvancedMaterialSourceContract.ProjectiveMirror => null,
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
