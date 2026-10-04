namespace XREngine.Rendering;

/// <summary>
/// Additive target companion for the original canonical Uber material graph.
/// It retains the resolved desktop variant axes instead of assigning guessed PBR fields.
/// </summary>
public sealed record UberBaseMaterialProfile
{
    public const uint NormalMap = 1;
    public const uint AlphaMasks = 2;
    public const uint AdvancedSpecular = 4;
    public const uint Emission = 8;
    public const uint RenderTime = 16;
    public const uint SupportedFeatures = NormalMap | AlphaMasks | AdvancedSpecular | Emission | RenderTime;
    public const int RoleCount = 7;
    public const int MaximumSourceTextures = 64;

    public int Version { get; init; } = 1;
    public uint Features { get; init; }
    public UberMaterialVariantRequest Variant { get; init; } = UberMaterialVariantRequest.Empty;
    public UberMaterialAuthoredState AuthoredState { get; init; } = UberMaterialAuthoredState.Empty;
    public string SourceIdentity { get; init; } = string.Empty;
    public string PreparedFragmentSource { get; init; } = string.Empty;
    public string CookedArtifactIdentity { get; init; } = string.Empty;
    public UberBasePassArtifact[] PassArtifacts { get; init; } = [];

    /// <summary>Resolved canonical lighting axes, independent of authored surface-feature bits.</summary>
    public uint PipelineFlags
    {
        get
        {
            uint flags = 15;
            foreach (string macro in Variant.PipelineMacros)
                flags &= macro switch
                {
                    "XRENGINE_UBER_DISABLE_FORWARD_LIGHTING" => ~1u,
                    "XRENGINE_UBER_DISABLE_FORWARD_SHADOWS" => ~2u,
                    "XRENGINE_UBER_DISABLE_FORWARD_AMBIENT_OCCLUSION" => ~4u,
                    "XRENGINE_UBER_DISABLE_FORWARD_PBR_RESOURCES" => ~8u,
                    _ => uint.MaxValue,
                };
            return flags;
        }
    }
    /// <summary>Original source shader graph retained for provenance and desktop reauthoring.</summary>
    public XRShader[] SourceShaders { get; init; } = [];
    /// <summary>Seven ordered role entries; inactive roles are null rather than substituted images.</summary>
    public UberBaseTextureBinding?[] TextureBindings { get; init; } = [];
    /// <summary>Every real authored source image, including disabled feature inputs and aliases.</summary>
    public UberBaseTextureBinding[] SourceTextureBindings { get; init; } = [];

    public static string SamplerName(int role) => role switch
    {
        0 => "_MainTex", 1 => "_BumpMap", 2 => "_AlphaMask", 3 => "_PBRMetallicMaps",
        4 => "_PBRSmoothnessMaps", 5 => "_SpecularMap", 6 => "_EmissionMap",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static bool IsRoleActive(int role, uint features) => role switch
    {
        0 => true,
        1 => (features & NormalMap) != 0,
        2 => (features & AlphaMasks) != 0,
        3 or 4 or 5 => (features & AdvancedSpecular) != 0,
        6 => (features & Emission) != 0,
        _ => false,
    };

    /// <summary>Maps actual resolved canonical feature IDs; unqualified features fail by name.</summary>
    public static bool TryResolveFeatures(ReadOnlySpan<string> ids, out uint features, out string? reason)
    {
        features = 0;
        for (int index = 0; index < ids.Length; index++)
        {
            string id = ids[index];
            uint bit = id switch
            {
                "normal-map" => NormalMap, "alpha-masks" => AlphaMasks,
                "advanced-specular" => AdvancedSpecular, "emission" => Emission,
                "render-time" => RenderTime, _ => 0,
            };
            if (bit == 0)
            {
                reason = $"UberBase.FeatureUnsupported: canonical feature '{id}' has no admitted base WebGPU lowering.";
                return false;
            }
            if ((features & bit) != 0)
            {
                reason = $"UberBase.FeatureDuplicate: resolved feature '{id}' occurs more than once.";
                return false;
            }
            features |= bit;
        }
        reason = null;
        return true;
    }

    /// <summary>Validates original image identity and all aliases before any decoded settings are restored.</summary>
    public bool TryValidateTextureTable(XRMaterial source, out string? reason)
    {
        reason = "UberBase.TextureProfileInvalid: the companion requires seven exact role entries and a complete bounded source table.";
        if (Version != 1 || (Features & ~SupportedFeatures) != 0 || TextureBindings is null ||
            TextureBindings.Length != RoleCount || SourceTextureBindings is null ||
            source.Textures.Count > MaximumSourceTextures || SourceTextureBindings.Length > MaximumSourceTextures)
            return false;
        int realImages = 0;
        for (int slot = 0; slot < source.Textures.Count; slot++)
        {
            XRTexture? texture = source.Textures[slot];
            if (texture is null) continue;
            if (texture.GetType() != typeof(XRTexture2D)) return false;
            realImages++;
            int matches = 0;
            foreach (UberBaseTextureBinding? binding in SourceTextureBindings)
            {
                if (binding is null) return false;
                if (binding.SourceTextureSlot != slot) continue;
                if (!ReferenceEquals(binding.Texture, texture) || binding.SamplerName != texture.ResolveSamplerName(slot) ||
                    !ValidSettings(binding.Settings)) return false;
                matches++;
            }
            if (matches != 1) return false;
        }
        if (realImages != SourceTextureBindings.Length) return false;
        for (int index = 0; index < SourceTextureBindings.Length; index++)
            for (int other = index + 1; other < SourceTextureBindings.Length; other++)
                if (ReferenceEquals(SourceTextureBindings[index].Texture, SourceTextureBindings[other].Texture) &&
                    SourceTextureBindings[index].Settings != SourceTextureBindings[other].Settings) return false;

        for (int role = 0; role < RoleCount; role++)
        {
            UberBaseTextureBinding? binding = TextureBindings[role];
            if (!IsRoleActive(role, Features))
            {
                if (binding is not null) return false;
                continue;
            }
            string name = SamplerName(role);
            reason = "UberBase.TextureRoleInvalid: each active canonical sampler must retain its unique original image, source slot and sampling settings.";
            if (binding is null || binding.SamplerName != name || binding.Texture is null ||
                binding.Texture.SamplerName != name || (uint)binding.SourceTextureSlot >= (uint)source.Textures.Count ||
                !ReferenceEquals(source.Textures[binding.SourceTextureSlot], binding.Texture) || !ValidSettings(binding.Settings)) return false;
            int matches = 0;
            foreach (UberBaseTextureBinding complete in SourceTextureBindings)
            {
                if (complete.SamplerName != name) continue;
                if (complete.SourceTextureSlot != binding.SourceTextureSlot || !ReferenceEquals(complete.Texture, binding.Texture) ||
                    complete.Settings != binding.Settings) return false;
                matches++;
            }
            if (matches != 1) return false;
        }
        reason = null;
        return true;
    }

    /// <summary>Applies preserved settings only to a detached reflection-decoded graph.</summary>
    public void RestoreDecodedTextures(XRMaterial source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!TryValidateTextureTable(source, out string? reason)) throw new InvalidDataException(reason);
        foreach (UberBaseTextureBinding binding in SourceTextureBindings)
            binding.Settings.ApplyTo(binding.Texture);
    }

    private static bool ValidSettings(PublishedStandardLitTextureSettings settings)
        => !settings.EnableComparison && float.IsFinite(settings.MaxAnisotropy) &&
            settings.MaxAnisotropy is >= 1 and <= 16 && settings.MaxAnisotropy == MathF.Truncate(settings.MaxAnisotropy) &&
            Enum.IsDefined(settings.CompareFunc) && Enum.IsDefined(settings.ImportedColorSpace) && Enum.IsDefined(settings.ImportedUsage);
}
