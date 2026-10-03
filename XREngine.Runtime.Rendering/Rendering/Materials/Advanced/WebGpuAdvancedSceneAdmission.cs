using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>Cold authored native-scene checks using the canonical material publisher's layouts, packing and resource metadata.</summary>
public static class WebGpuAdvancedSceneAdmission
{
    /// <summary>Returns the exact material-local texture closure and kernel cohort identity without creating API objects.</summary>
    public static bool TryInspectMaterial(XRMaterial material, int pass, RenderingParameters options,
        out string cohort, out AdvancedGpuResourceBindingSource[] resources, out string resource, out string reason)
    {
        cohort = string.Empty;
        resources = [];
        resource = "material-source";
        EAdvancedMaterialSourceContract sourceContract = AdvancedMaterialSourceContract.Classify(material);
        if (WebGpuAdvancedMaterialContract.GetSourceRejection(sourceContract) is { } sourceReason)
        {
            if (sourceContract == EAdvancedMaterialSourceContract.UnsupportedTextureSemantics)
                resource = $"Metallic/{material.GetSurfaceTexture(EMaterialTextureSemantic.Metallic)?.Texture?.Name ?? "<unbound>"},Roughness/{material.GetSurfaceTexture(EMaterialTextureSemantic.Roughness)?.Texture?.Name ?? "<unbound>"}";
            reason = sourceReason;
            return false;
        }
        if (material.IsTransparentLike())
        {
            reason = "The selected native opaque pass cannot consume a transparent or refractive material; retain its late raster pass.";
            return false;
        }
        resource = "material-layout";
        if (!MaterialBindingLayouts.TryGetDefaultForRenderPass(pass, out MaterialBindingLayout layout))
        {
            reason = "The selected scene pass has no canonical native material layout.";
            return false;
        }
        if (ReferenceEquals(layout, MaterialBindingLayouts.OpaqueDeferred) &&
            material.GetEffectiveTransparencyMode() is ETransparencyMode.Masked or ETransparencyMode.AlphaToCoverage)
            layout = MaterialBindingLayouts.MaskedForward;
        if (!AdvancedGpuMaterialPublisher.TryTranslateLayout(layout, out AdvancedMaterialLayoutTranslation translation, out reason))
            return false;
        if (WebGpuAdvancedRasterStateContract.GetRejection(WebGpuAdvancedRasterStateContract.Capture(options)) is { } rasterReason)
        {
            resource = "native-raster-state";
            reason = rasterReason;
            return false;
        }
        bool doubleSided = options.CullMode == ECullMode.None;
        EAdvancedMaterialRenderStateClass state = translation.RequiredCoverage == EAdvancedMaterialCoverageMode.Masked
            ? doubleSided ? EAdvancedMaterialRenderStateClass.MaskedDoubleSided : EAdvancedMaterialRenderStateClass.MaskedSingleSided
            : doubleSided ? EAdvancedMaterialRenderStateClass.OpaqueDoubleSided : EAdvancedMaterialRenderStateClass.OpaqueSingleSided;
        MaterialBindingSourceSnapshot snapshot = MaterialBindingSourceEncoder.Encode(material);
        uint[] words = new uint[layout.RowWordCount];
        if (!MaterialBindingRowPacker.TryWriteOpaqueDeferred(layout, snapshot.Entry, snapshot.EmissionColor,
            snapshot.EmissionStrength, snapshot.EmissionTextureMetadata, snapshot.EmissionUvScaleOffset,
            snapshot.EmissionUvRotation, words, out reason))
            return false;
        AdvancedMaterialDatabase database = new(1, 1, 1, 64,
            maximumConstantWordsPerMaterial: layout.RowWordCount, maximumTextureBindingsPerMaterial: (uint)layout.Textures.Count);
        AdvancedGpuMaterialPublisher publisher = new(database, 1);
        AdvancedMaterialTextureBinding[] bindings = new AdvancedMaterialTextureBinding[layout.Textures.Count];
        if (!publisher.TryPreflight(material, layout, translation.RequiredCoverage, state, words, bindings, out reason))
            return false;
        AdvancedGpuResourceBindingSource[] companionSources = new AdvancedGpuResourceBindingSource[AdvancedEngineSurfaceRecord.RoleCount];
        if (!AdvancedEngineSurfaceSourceEncoder.TryEncode(material, out AdvancedEngineSurfaceRecord engineSurface, companionSources, out reason))
        {
            resource = "engine-surface-companion";
            return false;
        }
        if (sourceContract is EAdvancedMaterialSourceContract.StandardSurface or EAdvancedMaterialSourceContract.EngineGeneratedSurface && engineSurface.SchemaVersion == 0)
        {
            resource = "engine-surface-companion";
            reason = "The engine material has no exact typed native surface companion.";
            return false;
        }
        XRTexture?[] textures = [snapshot.Albedo, snapshot.Normal, snapshot.RM, snapshot.Emissive,
            companionSources[0].Texture, companionSources[1].Texture, companionSources[2].Texture, companionSources[3].Texture];
        EAdvancedResourceFallback[] fallbacks = [EAdvancedResourceFallback.White, EAdvancedResourceFallback.FlatNormal,
            EAdvancedResourceFallback.White, EAdvancedResourceFallback.Black,
            EAdvancedResourceFallback.White, EAdvancedResourceFallback.FlatNormal, EAdvancedResourceFallback.White, EAdvancedResourceFallback.White];
        string[] names = ["Albedo", "Normal", "RM", "Emissive", "EngineBaseColor", "EngineNormal", "EngineMetallic", "EngineRoughness"];
        List<AdvancedGpuResourceBindingSource> pairs = [];
        for (int index = 0; index < textures.Length; index++)
        {
            resource = $"{names[index]}/{textures[index]?.Name ?? "<unbound>"}";
            if (!AdvancedGpuResourceSourceEncoder.TryEncode(textures[index], fallbacks[index],
                out AdvancedGpuResourceBindingSource source, out _, out reason))
                return false;
            if (source.Texture is null) continue;
            if (!TryInspectTexture(in source, allowDepthComparison: false, out reason))
                return false;
            if (translation.RequiredCoverage == EAdvancedMaterialCoverageMode.Masked && index == 0 &&
                source.TextureRecord.Dimension != EAdvancedTextureDimension.Texture2D)
            {
                reason = "Masked visibility requires an exact 2D base-color texture/default-sampler pair.";
                return false;
            }
            if (pairs.Any(pair => ReferenceEquals(pair.Texture, source.Texture) && pair.SamplerRecord.Equals(source.SamplerRecord)))
                continue;
            if (pairs.Count(pair => pair.TextureRecord.Dimension == source.TextureRecord.Dimension) >=
                WebGpuAdvancedMaterialContract.GetTextureDimensionCapacity(source.TextureRecord.Dimension) ||
                pairs.Count == WebGpuAdvancedMaterialContract.TextureSlotCount)
            {
                reason = "Native material texture closure exceeds ten 2D pairs, one cube pair, or one 2D-array pair.";
                return false;
            }
            pairs.Add(source);
        }
        cohort = $"{layout.LayoutHash}:{translation.RequiredCoverage}:{state}";
        resources = [.. pairs];
        resource = string.Empty;
        reason = string.Empty;
        return true;
    }

    /// <summary>Checks an exact authored source without creating API resources or claiming producer readiness.</summary>
    public static bool TryInspectTexture(in AdvancedGpuResourceBindingSource source, bool allowDepthComparison, out string reason)
    {
        reason = WebGpuAdvancedMaterialContract.GetTexturePairRejection(source.TextureRecord, source.SamplerRecord,
            allowDepthComparison, out bool depthComparison) ?? string.Empty;
        if (reason.Length != 0) return false;
        if (source.Texture is null)
        {
            reason = "A selected native sampled resource has no authored source.";
            return false;
        }
        string format;
        try { format = WebGpuTextureFormatContract.Map(GetFormat(source.Texture)); }
        catch (NotSupportedException error) { reason = error.Message; return false; }
        reason = WebGpuAdvancedMaterialContract.GetSamplingRejection(format, 1, null, depthComparison) ?? string.Empty;
        return reason.Length == 0;
    }

    private static ESizedInternalFormat GetFormat(XRTexture texture) => texture switch
    {
        XRTexture2D image => image.SizedInternalFormat,
        XRTextureCube cube => cube.SizedInternalFormat,
        XRTexture2DArray array when array.Textures.Length != 0 => array.Textures[0].SizedInternalFormat,
        _ => throw new NotSupportedException("The texture has no exact native sampled format."),
    };
}
