using XREngine.Data.Rendering;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Checks an exact authored WGSL companion against the engine's opaque PBR surface.</summary>
public static class EngineAuthoredLitMaterialAdmission
{
    public static bool TryAdmit(XRMaterial material, ShaderProgramArtifact artifact,
        out StandardLitColorSurfaceBinding? color, out StandardLitTextureSurfaceBinding? texture,
        out string? reason)
    {
        color = null;
        texture = null;
        reason = null;
        if (material.EngineSemantic != EngineMaterialSemanticIdentity.AuthoredLitV1 || material.Shaders.Count == 0 ||
            material.Shaders.Count > 2 || material.Shaders.Any(shader => shader.Type is not (EShaderType.Vertex or EShaderType.Fragment)) ||
            material.Shaders.Count(shader => shader.Type == EShaderType.Fragment) != 1 ||
            artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.SourceLanguage != "MaterialRecipe" ||
            artifact.Pass != "opaque-forward" || artifact.VertexEntryPoint != "standardLitVertex" ||
            artifact.FragmentEntryPoint != "standardLitFragment" || artifact.ComputeEntryPoint is not null)
        {
            reason = "Authored PBR requires exact WebGPU MaterialRecipe vertex and fragment stage companions for opaque-forward.";
            return false;
        }
        bool textured = artifact.SemanticSchemaIdentity is EngineLitMaterialShaderGenerator.TextureSchema or
            EngineLitMaterialShaderGenerator.NormalTextureSchema;
        bool normalTexture = artifact.SemanticSchemaIdentity == EngineLitMaterialShaderGenerator.NormalTextureSchema;
        if (artifact.SemanticSchemaIdentity is not (EngineLitMaterialShaderGenerator.ColorSchema or
            EngineLitMaterialShaderGenerator.TextureSchema or EngineLitMaterialShaderGenerator.NormalTextureSchema))
        {
            reason = $"Authored lit schema '{artifact.SemanticSchemaIdentity}' is unsupported.";
            return false;
        }
        if (!HasPhysicalPbrAbi(artifact, textured, normalTexture))
        {
            reason = "Authored PBR descriptor does not declare the engine's complete physical vertex, factor, lighting, AO and sampled-texture ABI.";
            return false;
        }
        if (artifact.SemanticSchemaIdentity == EngineLitMaterialShaderGenerator.ColorSchema)
        {
            if (!StandardLitColorSurfaceBinding.TryCreateAuthoredCooked(material, out color, out reason)) return false;
            return true;
        }
        if (!StandardLitTextureSurfaceBinding.TryCreateAuthoredCooked(material, out texture, out reason) ||
            !texture!.TryRead(out StandardLitTextureSurface surface, out reason)) return false;
        if ((surface.Normal is not null) != normalTexture)
        {
            reason = "Authored lit texture features do not match the cooked vertex and normal-map ABI.";
            return false;
        }
        return true;
    }

    private static bool HasPhysicalPbrAbi(ShaderProgramArtifact artifact, bool textured, bool normalTexture)
    {
        if (artifact.VertexBuffers.Length != 1 || artifact.VertexBuffers[0].StepMode != "vertex" ||
            artifact.VertexBuffers[0].Attributes.Length != (normalTexture ? 4 : textured ? 3 : 2) ||
            !Vertex(artifact, 0, "position", "float32x3") ||
            !Vertex(artifact, 1, "normal", "float32x3") ||
            textured && !Vertex(artifact, 2, "uv0", "float32x2") ||
            normalTexture && !Vertex(artifact, 3, "tangent", "float32x4")) return false;

        if (artifact.Resources.Length != (normalTexture ? 14 : textured ? 12 : 6) ||
            !Uniform(artifact, "View", 0, 0, ShaderAbiResourceOwner.Engine, 80,
                "ViewProjection", "CameraPosition") ||
            !Uniform(artifact, "Object", 0, 1, ShaderAbiResourceOwner.Engine, 128,
                "ModelMatrix", "NormalMatrix") ||
            !Uniform(artifact, "StandardLitMaterial", 1, 0, ShaderAbiResourceOwner.Material,
                textured ? 48u : 32u, textured
                    ? ["StandardLitBaseColorOpacity", "StandardLitRoughnessMetallicSpecularEmission", "StandardLitTextureControls"]
                    : ["StandardLitBaseColorOpacity", "StandardLitRoughnessMetallicSpecularEmission"]) ||
            !Uniform(artifact, "ForwardLighting", 2, 0, ShaderAbiResourceOwner.Engine, 1072,
                LightingProviders()) ||
            !Sampled(artifact, "AmbientOcclusionTexture", 2, 1, 2)) return false;
        if (!textured) return true;
        return Sampled(artifact, "StandardLitBaseColorTexture", 1, 1, 2) &&
            Sampled(artifact, "StandardLitMetallicTexture", 1, 5, 6) &&
            Sampled(artifact, "StandardLitRoughnessTexture", 1, 7, 8) &&
            (!normalTexture || Sampled(artifact, "StandardLitNormalTexture", 1, 3, 4));
    }

    private static bool Vertex(ShaderProgramArtifact artifact, int location, string semantic, string format)
    {
        ShaderVertexAttribute attribute = artifact.VertexBuffers[0].Attributes[location];
        return attribute.Location == location && attribute.Semantic == semantic && attribute.Format == format;
    }

    private static bool Uniform(ShaderProgramArtifact artifact, string name, uint group, uint binding,
        ShaderAbiResourceOwner owner, uint bytes, params string[] providers)
    {
        ShaderStageResourceLayout? resource = artifact.Resources.FirstOrDefault(resource =>
            resource.Contract.Set == group && resource.Contract.Binding == binding);
        if (resource is null || resource.Contract.Name != name || resource.Contract.Kind != ShaderAbiResourceKind.UniformBuffer ||
            resource.Contract.Owner != owner || resource.Contract.ByteSize != bytes ||
            resource.Contract.Members.Length != providers.Length || !resource.DynamicOffset) return false;
        for (int index = 0; index < providers.Length; index++)
        {
            ShaderAbiMemberContract member = resource.Contract.Members[index];
            uint offset = name switch
            {
                "View" when index == 1 => 64,
                "Object" when index == 1 => 64,
                _ => (uint)(16 * index),
            };
            string type = name == "Object" || name == "View" && index == 0
                ? "mat4x4<f32>" : "vec4<f32>";
            if (member.ProviderName != providers[index] || member.Offset != offset || member.PhysicalType != type)
                return false;
        }
        return true;
    }

    private static bool Sampled(ShaderProgramArtifact artifact, string name, uint group, uint textureBinding, uint samplerBinding)
        => artifact.Resources.Any(resource => resource.Contract.Name == name && resource.Contract.Set == group &&
            resource.Contract.Binding == textureBinding && resource.Contract.Kind == ShaderAbiResourceKind.SampledImage &&
            resource.Contract.Owner == ShaderAbiResourceOwner.Material) &&
            artifact.Resources.Any(resource => resource.Contract.Name == name && resource.Contract.Set == group &&
            resource.Contract.Binding == samplerBinding && resource.Contract.Kind == ShaderAbiResourceKind.Sampler &&
            resource.Contract.Owner == ShaderAbiResourceOwner.Material);

    private static string[] LightingProviders()
    {
        List<string> names = ["ForwardLightCounts", "GlobalAmbient"];
        for (int i = 0; i < 4; i++) { names.Add($"ForwardDirectional{i}Direction"); names.Add($"ForwardDirectional{i}ColorIntensity"); }
        for (int i = 0; i < 8; i++)
        {
            names.Add($"ForwardPoint{i}PositionRadius"); names.Add($"ForwardPoint{i}ColorIntensity");
            names.Add($"ForwardPoint{i}Brightness");
        }
        for (int i = 0; i < 8; i++)
        {
            names.Add($"ForwardSpot{i}PositionRadius"); names.Add($"ForwardSpot{i}ColorIntensity");
            names.Add($"ForwardSpot{i}DirectionExponent"); names.Add($"ForwardSpot{i}CutoffsBrightness");
        }
        names.Add("AmbientOcclusionControls");
        return [.. names];
    }
}
