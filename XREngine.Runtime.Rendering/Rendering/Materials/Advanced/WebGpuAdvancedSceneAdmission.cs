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
        if (material.IsTransparentLike())
        {
            reason = "The selected native opaque pass cannot consume a transparent or refractive material.";
            return false;
        }
        if (!WebGpuAdvancedMaterialContract.SupportsCullMode((uint)options.CullMode))
        {
            reason = "Native visibility admits disabled or back-face culling only.";
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
        if (sourceContract == EAdvancedMaterialSourceContract.StandardSurface && engineSurface.SchemaVersion == 0)
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
            string format;
            try { format = WebGpuTextureFormatContract.Map(GetFormat(source.Texture)); }
            catch (NotSupportedException error) { reason = error.Message; return false; }
            if (WebGpuAdvancedMaterialContract.GetSamplingRejection(format, 1, null) is { } sampleReason)
            {
                reason = sampleReason;
                return false;
            }
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

    private static ESizedInternalFormat GetFormat(XRTexture texture) => texture switch
    {
        XRTexture2D image => image.SizedInternalFormat,
        XRTextureCube cube => cube.SizedInternalFormat,
        XRTexture2DArray array when array.Textures.Length != 0 => array.Textures[0].SizedInternalFormat,
        _ => throw new NotSupportedException("The texture has no exact native sampled format."),
    };
}
