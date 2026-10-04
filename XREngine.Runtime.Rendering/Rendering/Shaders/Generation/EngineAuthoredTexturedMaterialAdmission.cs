using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Admits exact authored normal/specular factors and source-proven raster companions.</summary>
public static class EngineAuthoredTexturedMaterialAdmission
{
    private static readonly ConditionalWeakTable<XRMaterial, AuthoredTexturedSurfaceBinding> Bindings = new();

    public static bool TryAdmit(XRMaterial material, ShaderProgramArtifact artifact,
        out AuthoredTexturedSurfaceBinding? binding, out string? reason)
    {
        binding = null;
        reason = "Authored textured requires its exact authored WebGPU vertex and fragment companion.";
        if (material.EngineSemantic != EngineMaterialSemanticIdentity.AuthoredLitTexturedV1 || material.ID == Guid.Empty ||
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
        if (!EngineAuthoredTexturedShaderProvenance.TryValidate(artifact, out int textureFlags, out string provenanceReason))
        {
            reason = provenanceReason;
            return false;
        }
        AuthoredTexturedSurfaceBinding reader = Bindings.GetValue(material, static value => new(value));
        if (!reader.TryRead(out AuthoredTexturedSurface surface, out reason)) return false;
        if (surface.TextureFlags != textureFlags)
        {
            reason = "Authored textured roles differ from the exact cooked family; recook the material.";
            return false;
        }
        binding = reader;
        return true;
    }

    public static bool TryValidateCompanion(ShaderProgramArtifact artifact, EngineMaterialVariantKey key, out string reason)
        => EngineAuthoredTexturedShaderProvenance.TryValidateCompanion(artifact, key, out reason);
}
