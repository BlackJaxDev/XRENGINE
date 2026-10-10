using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Commands;

/// <summary>Captures exact cooked Uber parameters and independently owned image roles before resource acquisition.</summary>
public static class AdvancedUberBaseSurfaceSourceEncoder
{
    private sealed class CachedBinding(UberBaseMaterialProfile profile)
    {
        public readonly UberBaseMaterialProfile Profile = profile;
        public UberBaseSurfaceBinding? Binding;
    }

    private static readonly ConditionalWeakTable<XRMaterial, CachedBinding> Bindings = new();

    /// <summary>Retains one cold-validated profile; replacing it requires a new material identity.</summary>
    public static bool TryGetBinding(XRMaterial material, out UberBaseSurfaceBinding? binding, out string reason)
    {
        binding = null;
        reason = "UberBase.NativeProfileMissing: the exact cooked Uber base profile is required.";
        if (material.EngineSemantic != EngineMaterialSemanticIdentity.UberBaseV1 || material.CookedUberBaseProfile is not { } profile)
            return false;
        CachedBinding cached = Bindings.GetValue(material, static source => new(source.CookedUberBaseProfile!));
        if (!ReferenceEquals(cached.Profile, profile))
        {
            reason = "UberBase.NativeProfileChanged: replace the material at a generation boundary after changing its cooked profile.";
            return false;
        }
        if (cached.Binding is null)
        {
            if (!UberBaseSurfaceBinding.TryCreate(material, profile, out UberBaseSurfaceBinding? created, out string? createReason))
            {
                reason = createReason ?? reason;
                return false;
            }
            cached.Binding = created;
        }
        binding = cached.Binding;
        reason = string.Empty;
        return true;
    }

    public static bool TryEncode(XRMaterial? material, out AdvancedUberBaseSurfaceRecord record,
        Span<AdvancedGpuResourceBindingSource> roles, out string reason)
    {
        record = default;
        roles.Clear();
        reason = string.Empty;
        if (roles.Length != AdvancedUberBaseSurfaceRecord.RoleCount)
        {
            reason = "An Uber base surface requires exactly seven texture role destinations.";
            return false;
        }
        if (material is null || material.EngineSemantic != EngineMaterialSemanticIdentity.UberBaseV1) return true;
        if (!EngineUberBaseNativeAdmission.TryRead(material, out _, out reason)) return false;
        if (!TryGetBinding(material, out UberBaseSurfaceBinding? binding, out reason)) return false;
        if (!binding!.TryGetValues(out ReadOnlySpan<byte> values, out string? valueReason))
        {
            reason = valueReason ?? "The cooked Uber base parameter image is invalid.";
            return false;
        }
        uint pipelineFlags = 15;
        bool contactDisabled = false;
        foreach (string macro in binding.Profile.Variant.PipelineMacros)
        {
            switch (macro)
            {
                case "XRENGINE_UBER_DISABLE_FORWARD_LIGHTING": pipelineFlags &= ~1u; break;
                case "XRENGINE_UBER_DISABLE_FORWARD_SHADOWS": pipelineFlags &= ~2u; break;
                case "XRENGINE_UBER_DISABLE_FORWARD_AMBIENT_OCCLUSION": pipelineFlags &= ~4u; break;
                case "XRENGINE_UBER_DISABLE_FORWARD_PBR_RESOURCES": pipelineFlags &= ~8u; break;
                case "XRENGINE_UBER_DISABLE_FORWARD_CONTACT_SHADOWS": contactDisabled = true; break;
            }
        }
        if (!contactDisabled)
        {
            reason = "UberBase.NativeContactShadowsUnsupported: the selected source requires a separately qualified contact-shadow operation.";
            return false;
        }
        AdvancedUberBaseSurfaceRecord candidate = new() { Features = binding.Features, PipelineFlags = pipelineFlags };
        candidate.SetParameters(values);
        ReadOnlySpan<UberBaseTextureBinding?> textures = binding.Textures;
        for (int role = 0; role < AdvancedUberBaseSurfaceRecord.RoleCount; role++)
        {
            XRTexture2D? texture = textures[role]?.Texture;
            EAdvancedResourceFallback fallback = role == 1 ? EAdvancedResourceFallback.FlatNormal :
                role == 6 ? EAdvancedResourceFallback.Black : EAdvancedResourceFallback.White;
            if (!AdvancedGpuResourceSourceEncoder.TryEncode(texture, fallback, out roles[role], out _, out reason) ||
                !AdvancedEngineSurfaceSamplingKey.TryCapture(texture, out uint samplingKey, out reason))
            {
                roles.Clear();
                return false;
            }
            candidate.SetSamplingKey(role, samplingKey);
        }
        candidate.SchemaVersion = AdvancedUberBaseSurfaceRecord.CurrentSchemaVersion;
        record = candidate;
        return true;
    }
}
