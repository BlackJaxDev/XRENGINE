using XREngine.Rendering.Shaders.Generation;

namespace XREngine.Rendering;

/// <summary>Uses the canonical variant builder's resolved feature/property axes without adopting a new source shader.</summary>
public static class UberBaseSourcePreparation
{
    public static UberBaseMaterialProfile Prepare(XRMaterial source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.GetType() != typeof(XRMaterial))
            throw new NotSupportedException($"UberBase.SourceTypeUnsupported: '{source.GetType().FullName}' has additional authored state that the ordinary Uber target carrier does not project.");
        if (!source.TryGetUberMaterialState(out XRShader? canonical, out ShaderUiManifest manifest) || canonical is null)
            throw new NotSupportedException("UberBase.CanonicalSourceMissing: the original canonical Uber fragment and authored UI manifest are required.");
        long authoredRevision = source.UberStateRevision, shaderRevision = source.ShaderStateRevision;
        UberShaderVariantBuilder.PreparedUberVariant prepared = UberShaderVariantBuilder.PrepareVariant(source, canonical, manifest);
        if (authoredRevision != source.UberStateRevision || shaderRevision != source.ShaderStateRevision)
            throw new InvalidOperationException("UberBase.SourceChangedDuringPreparation: retry after the authored source stabilizes.");
        if (!UberBaseMaterialProfile.TryResolveFeatures(prepared.Request.EnabledFeatures, out uint features, out string? reason))
            throw new NotSupportedException(reason);
        string generated = prepared.FragmentShader.Source?.Text
            ?? throw new InvalidDataException("UberBase.PreparedSourceMissing: the canonical builder did not produce its requested source.");
        XRShader? vertex = source.Shaders.FirstOrDefault(static shader => shader.Type == EShaderType.Vertex);
        if (vertex is null || source.Shaders.Count != 2)
            throw new NotSupportedException("UberBase.VertexSourceUnsupported: this profile requires the explicit canonical mono Uber vertex and fragment stages.");
        List<UberBaseTextureBinding> complete = [];
        for (int slot = 0; slot < source.Textures.Count; slot++)
        {
            if (source.Textures[slot] is null) continue;
            if (source.Textures[slot]?.GetType() != typeof(XRTexture2D))
                throw new NotSupportedException($"UberBase.SourceTextureUnsupported: source slot {slot} requires a static XRTexture2D.");
            XRTexture2D texture = (XRTexture2D)source.Textures[slot]!;
            complete.Add(new() { SourceTextureSlot = slot, SamplerName = texture.ResolveSamplerName(slot), Texture = texture,
                Settings = PublishedStandardLitTextureSettings.Capture(texture) });
        }
        UberBaseTextureBinding?[] roles = new UberBaseTextureBinding?[UberBaseMaterialProfile.RoleCount];
        for (int role = 0; role < roles.Length; role++)
        {
            if (!UberBaseMaterialProfile.IsRoleActive(role, features)) continue;
            string name = UberBaseMaterialProfile.SamplerName(role);
            foreach (UberBaseTextureBinding binding in complete)
                if (binding.SamplerName == name)
                {
                    if (roles[role] is not null) throw new NotSupportedException($"UberBase.SourceSamplerAmbiguous: '{name}' occurs in multiple original source slots.");
                    roles[role] = binding;
                }
            if (roles[role] is null) throw new NotSupportedException($"UberBase.SourceSamplerMissing: '{name}' has no original authored texture.");
        }
        UberBaseMaterialProfile profile = new()
        {
            Features = features, Variant = prepared.Request,
            SourceIdentity = EngineUberBaseShaderContract.NormalizedHash(generated), PreparedFragmentSource = generated,
            AuthoredState = new UberMaterialAuthoredState([.. source.UberAuthoredState.Features], [.. source.UberAuthoredState.Properties]),
            SourceShaders = [vertex, canonical],
            TextureBindings = roles, SourceTextureBindings = [.. complete],
        };
        if (!UberBaseSurfaceBinding.TryCreate(source, profile, out _, out reason))
            throw new NotSupportedException(reason);
        return profile;
    }
}
