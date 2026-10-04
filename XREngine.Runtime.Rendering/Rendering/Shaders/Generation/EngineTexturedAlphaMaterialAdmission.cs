using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Admits exact authored texture-alpha factors and source-proven raster companions.</summary>
public static class EngineTexturedAlphaMaterialAdmission
{
    private static readonly ConditionalWeakTable<XRMaterial, TexturedAlphaSurfaceBinding> Bindings = new();

    public static bool TryAdmit(XRMaterial material, ShaderProgramArtifact artifact,
        out TexturedAlphaSurfaceBinding? binding, out string? reason)
    {
        binding = null;
        reason = "Textured alpha requires its exact authored WebGPU vertex and fragment companion.";
        if (material.EngineSemantic != EngineMaterialSemanticIdentity.AuthoredLitTextureAlphaV1 || material.ID == Guid.Empty ||
            material.Shaders.Count is < 1 or > 2 || !artifact.Name.StartsWith("mat-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(artifact.Name.AsSpan(4), "N", out Guid authoredId) || authoredId != material.ID) return false;
        int fragments = 0;
        for (int index = 0; index < material.Shaders.Count; index++)
        {
            XRShader shader = material.Shaders[index];
            if (!string.IsNullOrEmpty(shader.Source?.Text) || !string.IsNullOrEmpty(shader.Source?.FilePath) ||
                shader.CookedArtifactIdentity != artifact.Identity) return false;
            EShaderType type = shader.Type;
            if (type == EShaderType.Fragment) fragments++;
            else if (type != EShaderType.Vertex) return false;
        }
        if (fragments != 1) return false;
        if (!EngineTexturedAlphaShaderProvenance.TryValidate(artifact, out string provenanceReason))
        {
            reason = provenanceReason;
            return false;
        }
        TexturedAlphaSurfaceBinding reader = Bindings.GetValue(material, static value => new(value));
        if (!reader.TryRead(out _, out reason)) return false;
        binding = reader;
        return true;
    }

    public static bool TryValidateCompanion(ShaderProgramArtifact artifact, EngineMaterialVariantKey key, out string reason)
        => EngineTexturedAlphaShaderProvenance.TryValidateCompanion(artifact, key, out reason);
}
