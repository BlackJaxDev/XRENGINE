using System.Text.Json;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Validates the complete pinned input closure of the engine's generated surface lowering.</summary>
public static class EngineLitMaterialShaderProvenance
{
    public static bool TryValidate(ShaderProgramArtifact artifact, out string reason)
    {
        reason = "The native surface requires a verified engine MaterialRecipe descriptor and its complete canonical source closure; recook the authored material.";
        if (artifact.SourceLanguage != "MaterialRecipe" || artifact.DescriptorBytes.IsDefaultOrEmpty)
            return false;
        EngineLitMaterialShaderPlan plan;
        switch (artifact.SemanticSchemaIdentity)
        {
            case EngineLitMaterialShaderGenerator.ColorSchema:
                plan = EngineLitMaterialShaderGenerator.Plan(artifact.Name, "lit", "opaque", "tint", "vertex", artifact.Target);
                break;
            case EngineLitMaterialShaderGenerator.ColorCoverageSchema:
                plan = EngineLitMaterialShaderGenerator.Plan(artifact.Name, "lit", "opaque-coverage", "tint", "vertex", artifact.Target);
                break;
            case EngineLitMaterialShaderGenerator.TextureSchema:
            case EngineLitMaterialShaderGenerator.NormalTextureSchema:
                plan = EngineLitMaterialShaderGenerator.Plan(artifact.Name, "lit", "opaque", "texture",
                    artifact.SemanticSchemaIdentity == EngineLitMaterialShaderGenerator.NormalTextureSchema ? "texture" : "vertex", artifact.Target);
                break;
            default:
                return false;
        }
        if (artifact.Pass != plan.Pass || artifact.VertexEntryPoint != "standardLitVertex" ||
            artifact.FragmentEntryPoint != "standardLitFragment" || artifact.ComputeEntryPoint is not null)
            return false;
        using JsonDocument document = JsonDocument.Parse(artifact.DescriptorBytes.AsMemory());
        JsonElement descriptor = document.RootElement;
        if (!descriptor.TryGetProperty("defines", out JsonElement defines) || defines.ValueKind != JsonValueKind.Array || defines.GetArrayLength() != 0 ||
            !descriptor.TryGetProperty("includes", out JsonElement includes) || includes.ValueKind != JsonValueKind.Array || includes.GetArrayLength() != 0 ||
            !descriptor.TryGetProperty("dependencies", out JsonElement dependencies) || dependencies.ValueKind != JsonValueKind.Array ||
            !descriptor.TryGetProperty("sourceMap", out JsonElement sourceMap) || sourceMap.ValueKind != JsonValueKind.Object ||
            !sourceMap.TryGetProperty("path", out JsonElement sourcePath) || sourcePath.ValueKind != JsonValueKind.String)
            return false;
        IReadOnlyList<EngineLitMaterialShaderSource> sources = EngineLitMaterialShaderGenerator.RequiredCanonicalSources(plan);
        string materialPath = sourcePath.GetString()!;
        if (!materialPath.EndsWith(".material.json", StringComparison.Ordinal) || dependencies.GetArrayLength() != sources.Count + 2)
            return false;
        uint seen = 0;
        bool materialSeen = false, recipeSeen = false;
        foreach (JsonElement dependency in dependencies.EnumerateArray())
        {
            if (!dependency.TryGetProperty("path", out JsonElement pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                !dependency.TryGetProperty("sha256", out JsonElement hashValue) || hashValue.ValueKind != JsonValueKind.String)
                return false;
            string path = pathValue.GetString()!;
            string hash = hashValue.GetString()!;
            if (path == materialPath)
            {
                if (materialSeen) return false;
                materialSeen = true;
                continue;
            }
            if (path.EndsWith(".recipe.json", StringComparison.Ordinal))
            {
                if (recipeSeen) return false;
                recipeSeen = true;
                continue;
            }
            int index = 0;
            while (index < sources.Count && path != sources[index].Path &&
                !path.EndsWith("/" + sources[index].Path, StringComparison.Ordinal)) index++;
            if (index == sources.Count || (seen & (1u << index)) != 0 || hash != sources[index].Sha256)
                return false;
            seen |= 1u << index;
        }
        if (!materialSeen || !recipeSeen || seen != (1u << sources.Count) - 1u) return false;
        reason = string.Empty;
        return true;
    }
}
