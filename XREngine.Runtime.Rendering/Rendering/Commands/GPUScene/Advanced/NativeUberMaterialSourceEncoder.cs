using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>Captures named native Uber values and resources in a fixed canonical row.</summary>
public static class NativeUberMaterialSourceEncoder
{
    private static readonly string[] EmissionParams = ["_EmissionSlotParams0", "_EmissionSlotParams1", "_EmissionSlotParams2", "_EmissionSlotParams3"];
    private static readonly string[] EmissionUvs = ["_EmissionSlotUv0", "_EmissionSlotUv1", "_EmissionSlotUv2", "_EmissionSlotUv3"];
    private static readonly string[] EmissionModifiers = ["_EmissionSlotModifiers0", "_EmissionSlotModifiers1", "_EmissionSlotModifiers2", "_EmissionSlotModifiers3"];
    private static readonly string[] DecalBlendParams = ["_DecalBlendParams0", "_DecalBlendParams1", "_DecalBlendParams2", "_DecalBlendParams3"];
    private static readonly string[] DecalUvs = ["_DecalUvMode0", "_DecalUvMode1", "_DecalUvMode2", "_DecalUvMode3"];
    private static readonly string[] DecalModifiers = ["_DecalSlotModifiers0", "_DecalSlotModifiers1", "_DecalSlotModifiers2", "_DecalSlotModifiers3"];
    private static readonly string[] DecalFx = ["_DecalSlotFx0", "_DecalSlotFx1", "_DecalSlotFx2", "_DecalSlotFx3"];

    /// <summary>Identifies source operations that can remove fragments from an opaque pass.</summary>
    public static bool RequiresCoverageDiscard(XRMaterial material)
    {
        if (material.Parameter<ShaderInt>("_FaceDiscard") is { Value: 1 or 2 })
            return true;
        if (material.Parameter<ShaderFloat>("_UvTileDiscard") is { Value: > 0.5f } &&
            material.Parameter<ShaderInt>("_UDIMDiscardMode") is { Value: 1 })
        {
            foreach (string name in NativeUberMaterialSchema.UdimCellNames)
                if (material.Parameter<ShaderFloat>(name) is { Value: > 0.001f })
                    return true;
        }
        if (material.IsUberFeatureEnabled("dissolve", false))
            return true;
        return material.Parameter<ShaderInt>("_Mode") is { Value: 1 };
    }

    public static bool TryEncode(
        XRMaterial material,
        Span<uint> words,
        Span<AdvancedGpuResourceBindingSource> resources,
        out EAdvancedCanonicalCompatibilityReason compatibilityReason,
        out string reason)
    {
        compatibilityReason = EAdvancedCanonicalCompatibilityReason.None;
        reason = string.Empty;
        MaterialBindingLayout layout = NativeUberMaterialSchema.Opaque;
        if (words.Length != layout.RowWordCount || resources.Length != NativeUberMaterialSchema.TextureRoleCount)
        {
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceBinding;
            reason = "The native Uber destination does not match its fixed schema.";
            return false;
        }

        words.Clear();
        resources.Clear();
        if (material.EngineSemantic != EngineMaterialSemanticIdentity.NativeUberV1 ||
            !material.TryGetUberMaterialState(out _, out _))
        {
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceBinding;
            reason = "Native Uber requires a recognized authored Uber source.";
            return false;
        }
        if (material.RenderOptions?.AlphaToCoverage == ERenderParamUsage.Enabled)
        {
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceBinding;
            reason = "Native Uber alpha-to-coverage requires a matching multisample coverage path.";
            return false;
        }
        uint features = 0u;
        foreach (UberMaterialFeatureState feature in material.UberAuthoredState.Features)
        {
            if (!feature.Enabled) continue;
            if (feature.Id is "render-time" or "forward-ambient-occlusion" or "forward-lighting" or
                "forward-shadows" or "forward-contact-shadows") continue;
            int index = NativeUberMaterialSchema.FeatureIndex(feature.Id);
            if (index < 0)
            {
                compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceBinding;
                reason = $"Native Uber feature '{feature.Id}' has no canonical bit.";
                return false;
            }
            if (index > 17 && feature.Id is not ("glitter" or "global-masks-themes" or "layered-decals"))
            {
                compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceBinding;
                reason = $"Native Uber feature '{feature.Id}' has no matching native operation.";
                return false;
            }
            features |= 1u << index;
        }
        if (!ValidateModes(material, out reason))
        {
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceBinding;
            return false;
        }
        if (!ValidateActiveModifiers(material, out reason))
        {
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceBinding;
            return false;
        }
        words[NativeUberMaterialSchema.FeatureWordOffset] = features;

        foreach (ShaderVar parameter in material.Parameters)
        {
            if (parameter is null || parameter.Name is not { } name) continue;
            int index = NativeUberMaterialSchema.ParameterIndex(name);
            if (index < 0) continue;
            Span<uint> value = words.Slice(NativeUberMaterialSchema.ParameterWordOffset(index), 4);
            if (!TryWriteParameter(parameter, value))
            {
                compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceBinding;
                reason = $"Native Uber parameter '{name}' has an unsupported type or non-finite value.";
                return false;
            }
        }

        return TryEncodeTextureRoles(material, resources, out compatibilityReason, out reason);
    }

    /// <summary>Encodes the current fixed sampler roles without reading scalar values.</summary>
    internal static bool TryEncodeTextureRoles(
        XRMaterial material,
        Span<AdvancedGpuResourceBindingSource> resources,
        out EAdvancedCanonicalCompatibilityReason compatibilityReason,
        out string reason)
    {
        compatibilityReason = EAdvancedCanonicalCompatibilityReason.None;
        reason = string.Empty;
        if (resources.Length != NativeUberMaterialSchema.TextureRoleCount)
        {
            compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceBinding;
            reason = "The native Uber texture destination does not match its fixed schema.";
            return false;
        }
        for (int role = 0; role < resources.Length; ++role)
        {
            string name = NativeUberMaterialSchema.TextureRoles[role];
            XRTexture? texture = null;
            for (int slot = 0; slot < material.Textures.Count; ++slot)
            {
                XRTexture? candidate = material.Textures[slot];
                if (candidate is null || candidate.ResolveSamplerName(slot) != name) continue;
                if (texture is not null)
                {
                    compatibilityReason = EAdvancedCanonicalCompatibilityReason.UnsupportedResourceBinding;
                    reason = $"Native Uber sampler '{name}' has duplicate source bindings.";
                    return false;
                }
                texture = candidate;
            }
            EAdvancedResourceFallback fallback = name == "_BumpMap"
                ? EAdvancedResourceFallback.FlatNormal
                : name.Contains("Emission", StringComparison.Ordinal) ? EAdvancedResourceFallback.Black
                : EAdvancedResourceFallback.White;
            if (!AdvancedGpuResourceSourceEncoder.TryEncode(texture, fallback,
                    out resources[role], out compatibilityReason, out reason))
                return false;
        }
        return true;
    }

    private static bool TryWriteParameter(ShaderVar parameter, Span<uint> words)
    {
        switch (parameter)
        {
            case ShaderFloat value when float.IsFinite(value.Value):
                words[0] = BitConverter.SingleToUInt32Bits(value.Value); return true;
            case ShaderInt value:
                words[0] = unchecked((uint)value.Value); return true;
            case ShaderUInt value:
                words[0] = value.Value; return true;
            case ShaderVector2 value when IsFinite(value.Value):
                words[0] = BitConverter.SingleToUInt32Bits(value.Value.X);
                words[1] = BitConverter.SingleToUInt32Bits(value.Value.Y); return true;
            case ShaderVector3 value when IsFinite(value.Value):
                words[0] = BitConverter.SingleToUInt32Bits(value.Value.X);
                words[1] = BitConverter.SingleToUInt32Bits(value.Value.Y);
                words[2] = BitConverter.SingleToUInt32Bits(value.Value.Z); return true;
            case ShaderVector4 value when IsFinite(value.Value):
                words[0] = BitConverter.SingleToUInt32Bits(value.Value.X);
                words[1] = BitConverter.SingleToUInt32Bits(value.Value.Y);
                words[2] = BitConverter.SingleToUInt32Bits(value.Value.Z);
                words[3] = BitConverter.SingleToUInt32Bits(value.Value.W); return true;
            default: return false;
        }
    }

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool IsFinite(Vector3 value) => IsFinite(new Vector2(value.X, value.Y)) && float.IsFinite(value.Z);
    private static bool IsFinite(Vector4 value) => IsFinite(new Vector3(value.X, value.Y, value.Z)) && float.IsFinite(value.W);

    private static bool ValidateModes(XRMaterial material, out string reason)
    {
        if (!Allowed(material, "_LightingMode", [0, 2, 5, 6], out reason) ||
            !Allowed(material, "_Mode", [0, 1, 9], out reason) ||
            !Allowed(material, "_MatcapUVMode", [0, 1, 2], out reason) ||
            !Allowed(material, "_RimStyle", [0, 1, 2], out reason) ||
            !Allowed(material, "_AlphaSource", [0, 1, 2], out reason) ||
            !Allowed(material, "_MainAlphaMaskMode", [0, 1, 2, 3, 4], out reason) ||
            !Allowed(material, "_FaceDiscard", [0, 1, 2], out reason) ||
            !Allowed(material, "_DissolveType", [0, 1, 2, 3, 4, 5], out reason) ||
            !Allowed(material, "_DissolveCoordinateSpace", [0, 1], out reason))
            return false;
        if (!Allowed(material, "_SourceGlitter", [0, 1], out reason))
            return false;
        if (material.IsUberFeatureEnabled("glitter", false) && IsNonZero(material, "_SourceGlitter") &&
            (!Allowed(material, "_GlitterLayers", [1, 2, 3, 4, 5, 6, 7, 8], out reason) ||
             !Allowed(material, "_GlitterMode", [0, 1, 2], out reason) ||
             !Allowed(material, "_GlitterShape", [0], out reason) ||
             !Allowed(material, "_GlitterBlendType", [0, 1], out reason) ||
             !Allowed(material, "_GlitterMaskChannel", [0, 1, 2, 3], out reason)))
            return false;
        foreach (string name in NativeUberMaterialSchema.Parameters)
        {
            if (name == "_UDIMDiscardUV") continue;
            if (name.StartsWith("_DecalTexture", StringComparison.Ordinal)) continue;
            if (!name.EndsWith("UV", StringComparison.Ordinal) &&
                !name.EndsWith("Uv", StringComparison.Ordinal)) continue;
            if (Allowed(material, name, [0, 1, 2, 3], out reason)) continue;
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool Allowed(XRMaterial material, string name, ReadOnlySpan<int> values, out string reason)
    {
        ShaderVar? parameter = null;
        foreach (ShaderVar candidate in material.Parameters)
            if (candidate?.Name == name) { parameter = candidate; break; }
        int value = parameter switch
        {
            null => 0,
            ShaderInt integer => integer.Value,
            ShaderUInt integer when integer.Value <= int.MaxValue => (int)integer.Value,
            ShaderFloat scalar when float.IsFinite(scalar.Value) &&
                scalar.Value == MathF.Truncate(scalar.Value) => (int)scalar.Value,
            _ => int.MinValue,
        };
        foreach (int allowed in values)
            if (value == allowed) { reason = string.Empty; return true; }
        reason = $"Native Uber parameter '{name}' uses unsupported value {value}.";
        return false;
    }

    private static bool ValidateActiveModifiers(XRMaterial material, out string reason)
    {
        if (material.IsUberFeatureEnabled("matcap", false) &&
            !material.IsUberFeatureEnabled("layered-matcap-rim", false))
        {
            reason = "Native Uber legacy matcap requires a separately matched native operation.";
            return false;
        }
        if (material.IsUberFeatureEnabled("surface-extensions", false))
        {
            if (IsNonZero(material, "_MainTexDistortionStrength") ||
                IsNonZero(material, "_NormalCorrectStrength") ||
                IsNonZero(material, "_MainTexStochasticMode") ||
                IsNonZero(material, "_ColorMaskEnabled"))
            {
                reason = "Native Uber surface distortion, normal correction, or color mask is active without a matching native operation.";
                return false;
            }
        }
        if (material.IsUberFeatureEnabled("surface-extensions", false) &&
            (IsNonZero(material, "_DistanceFade") || IsNonZero(material, "_AlphaFresnel") ||
             IsNonZero(material, "_AlphaAngular") || IsNonZero(material, "_AlphaDither") ||
             IsNonZero(material, "_DepthAlphaEnabled")))
        {
            reason = "Native Uber distance, angle, dither, or depth alpha has no matching coverage operation.";
            return false;
        }
        if (material.IsUberFeatureEnabled("layered-matcap-rim", false) && IsNonZero(material, "_DepthRimStrength"))
        {
            reason = "Native Uber depth rim has no matching native operation.";
            return false;
        }
        if (material.IsUberFeatureEnabled("advanced-stylized-lighting", false) &&
            (IsNonZero(material, "_LightingForceDirection") ||
             IsNonZero(material, "_LightingForceColor")))
        {
            reason = "Native Uber forced light direction or color has no matching native operation.";
            return false;
        }
        if (material.IsUberFeatureEnabled("layered-matcap-rim", false) &&
            IsNonZero(material, "_Rim2Enable"))
        {
            reason = "Native Uber second rim layer has no matching native operation.";
            return false;
        }
        if (material.IsUberFeatureEnabled("layered-emission", false) &&
            IsNonZero(material, "_EmissionGlobalMask"))
        {
            reason = "Native Uber emission global mask has no matching native operation.";
            return false;
        }
        if (material.IsUberFeatureEnabled("dissolve", false) &&
            IsNonZero(material, "_DissolveHueShift"))
        {
            reason = "Native Uber dissolve hue shift has no matching native operation.";
            return false;
        }
        if (IsDifferentFrom(material, "_MainContrast", 1f) ||
            IsNonZero(material, "_MainGrayscale") ||
            IsNonZero(material, "_MainColorReplaceStrength"))
        {
            reason = "Native Uber contrast, grayscale, or color replacement has no matching native operation.";
            return false;
        }
        if (material.IsUberFeatureEnabled("extended-effects", false) &&
            (IsNonZero(material, "_InternalParallaxStrength") ||
             IsNonZero(material, "_PathingStrength") ||
             IsNonZero(material, "_ProximityStrength") ||
             IsNonZero(material, "_TouchGlowStrength") ||
             IsNonZero(material, "_VideoBlend") ||
             IsNonZero(material, "_ProceduralMode")))
        {
            reason = "Native Uber active extended effect has no matching native operation.";
            return false;
        }
        for (int slot = 0; slot < 4; ++slot)
        {
            Vector4 decalBlend = material.Parameter<ShaderVector4>(DecalBlendParams[slot])?.Value ?? Vector4.Zero;
            if (material.IsUberFeatureEnabled("layered-decals", false) && decalBlend.Y > 0f)
            {
                if (!Allowed(material, DecalUvs[slot], [0, 1, 2, 3], out reason))
                    return false;
                Vector4 decalModifiers = material.Parameter<ShaderVector4>(DecalModifiers[slot])?.Value ?? Vector4.Zero;
                Vector4 decalFx = material.Parameter<ShaderVector4>(DecalFx[slot])?.Value ?? Vector4.Zero;
                if (decalModifiers.X is not (0f or 1f or 2f) ||
                    decalModifiers.Y != 0f ||
                    decalModifiers.Z is not (0f or 1f) ||
                    decalModifiers.W != 0f ||
                    decalFx.Z != 0f)
                {
                    reason = $"Native Uber active decal slot {slot} has an unsupported face, global mask, mirror, depth, or chroma modifier.";
                    return false;
                }
            }
            Vector4 emission = material.Parameter<ShaderVector4>(EmissionParams[slot])?.Value ?? Vector4.Zero;
            Vector4 modifiers = material.Parameter<ShaderVector4>(EmissionModifiers[slot])?.Value ?? Vector4.Zero;
            if (emission.X > 0f && !Allowed(material, EmissionUvs[slot], [0, 1, 2, 3], out reason))
                return false;
            if (emission.X > 0f && modifiers.Z != 0f)
            {
                reason = "Native Uber active emission slot has an unsupported global mask.";
                return false;
            }
        }
        bool activeUdimCell = material.Parameter<ShaderFloat>("_UvTileDiscard") is { Value: > 0.5f };
        bool anyUdimCell = false;
        foreach (string name in NativeUberMaterialSchema.UdimCellNames)
            anyUdimCell |= IsNonZero(material, name);
        activeUdimCell &= anyUdimCell;
        if (activeUdimCell &&
            (!Allowed(material, "_UDIMDiscardMode", [1], out _) ||
             !Allowed(material, "_UDIMDiscardUV", [0, 1], out _)))
        {
            reason = "Native Uber active UDIM rows require tile-discard mode 1 and UV set 0 or 1.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool IsNonZero(XRMaterial material, string name)
    {
        foreach (ShaderVar parameter in material.Parameters)
        {
            if (parameter?.Name != name) continue;
            return parameter switch
            {
                ShaderFloat value => value.Value != 0f,
                ShaderInt value => value.Value != 0,
                ShaderUInt value => value.Value != 0u,
                ShaderVector2 value => value.Value != Vector2.Zero,
                ShaderVector3 value => value.Value != Vector3.Zero,
                ShaderVector4 value => value.Value != Vector4.Zero,
                _ => true,
            };
        }
        return false;
    }

    private static bool IsDifferentFrom(XRMaterial material, string name, float neutral)
    {
        ShaderFloat? value = material.Parameter<ShaderFloat>(name);
        return value is not null && value.Value != neutral;
    }
}
