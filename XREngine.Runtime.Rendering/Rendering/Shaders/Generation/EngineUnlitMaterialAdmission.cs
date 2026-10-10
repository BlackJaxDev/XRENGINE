using System.Runtime.CompilerServices;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Admits a target-cooked ordinary unlit receiver only with its exact modeled source and layout.</summary>
public static class EngineUnlitMaterialAdmission
{
    private static readonly ConditionalWeakTable<XRMaterial, EngineUnlitSurfaceBinding> Bindings = new();

    /// <summary>Admits a source-free engine factory through its declared shared variant.</summary>
    public static bool TryAdmitBuiltIn(XRMaterial material, ShaderProgramArtifact artifact,
        EngineMaterialVariantKey key, out EngineUnlitSurfaceBinding? binding, out string? reason)
    {
        binding = null;
        reason = "Unlit built-in receivers require a source-free factory material and its declared forward variant.";
        if ((material.GetType() != typeof(XRMaterial) &&
             material is not PublishedUnlitMaterial { PublishedUnlitTextureProfile: not null }) ||
            material.Shaders.Count != 0 ||
            !material.EngineSemantic.IsUnlit() || key != EngineUnlitMaterialShaderGenerator.BuiltInKey(material.EngineSemantic) ||
            artifact.SemanticSchemaIdentity != EngineUnlitMaterialShaderGenerator.SchemaFor(material.EngineSemantic) ||
            !EngineUnlitShaderProvenance.TryValidateBuiltIn(artifact, key, out reason)) return false;
        EngineUnlitSurfaceBinding reader = Bindings.GetValue(material, static value => new(value));
        if (!reader.TryRead(out _, out reason)) return false;
        binding = reader;
        return true;
    }

    public static bool TryAdmit(XRMaterial material, ShaderProgramArtifact artifact,
        out EngineUnlitSurfaceBinding? binding, out string? reason)
    {
        binding = null;
        reason = "Unlit requires its exact target-cooked canonical forward fragment companion.";
        if (!material.EngineSemantic.IsUnlit() || material.ID == Guid.Empty ||
            material.Shaders.Count != 1 || material.Shaders[0].Type != EShaderType.Fragment ||
            !string.IsNullOrEmpty(material.Shaders[0].Source?.Text) ||
            !string.IsNullOrEmpty(material.Shaders[0].Source?.FilePath) ||
            material.Shaders[0].CookedArtifactIdentity != artifact.Identity ||
            !artifact.Name.StartsWith("mat-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(artifact.Name.AsSpan(4), "N", out Guid sourceId) || sourceId != material.ID ||
            artifact.SemanticSchemaIdentity != EngineUnlitMaterialShaderGenerator.SchemaFor(material.EngineSemantic) ||
            artifact.Pass != EngineUnlitMaterialShaderGenerator.Pass)
            return false;
        if (!EngineUnlitShaderProvenance.TryValidate(artifact, out string proofReason))
        {
            reason = proofReason;
            return false;
        }
        EngineUnlitSurfaceBinding reader = Bindings.GetValue(material, static value => new(value));
        if (!reader.TryRead(out _, out reason)) return false;
        binding = reader;
        return true;
    }
}
