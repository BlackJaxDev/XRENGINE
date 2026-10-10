using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

public partial class XRRenderProgram
{
    /// <summary>Uses the verified cooked ABI when a runtime cannot inspect authored GLSL files.</summary>
    private static (IReadOnlyDictionary<string, ShaderUniformBinding> uniforms,
        IReadOnlyDictionary<string, ShaderTextureBinding> textures) BuildCookedShaderInterface(
        ShaderProgramArtifact artifact)
    {
        Dictionary<string, ShaderUniformBinding> uniforms = new(UniformComparer);
        Dictionary<string, ShaderTextureBinding> textures = new(UniformComparer);

        foreach (ShaderStageResourceLayout resource in artifact.Resources)
        {
            IReadOnlyList<EShaderType> stages = CookedVisibilityStages(resource.Visibility);
            ShaderAbiResourceContract contract = resource.Contract;
            if (contract.Kind == ShaderAbiResourceKind.UniformBuffer)
            {
                foreach (ShaderAbiMemberContract member in contract.Members)
                {
                    string glslType = CookedUniformType(member.PhysicalType);
                    EShaderVarType? engineType = UniformTypeLookup.TryGetValue(glslType, out EShaderVarType type)
                        ? type : null;
                    bool isArray = member.ArrayCount != 0;
                    uniforms.TryAdd(member.ProviderName, new ShaderUniformBinding(
                        member.ProviderName,
                        glslType,
                        engineType,
                        isArray,
                        isArray ? checked((int)member.ArrayCount) : null,
                        null,
                        stages));
                }
            }
            else if (contract.Kind is ShaderAbiResourceKind.SampledImage or
                ShaderAbiResourceKind.CombinedImageSampler or ShaderAbiResourceKind.Sampler or
                ShaderAbiResourceKind.StorageImage)
            {
                textures.TryAdd(contract.Name, new ShaderTextureBinding(
                    contract.Name,
                    resource.BindingType,
                    false,
                    null,
                    null,
                    stages));
            }
        }

        return (uniforms, textures);
    }

    private static IReadOnlyList<EShaderType> CookedVisibilityStages(ShaderStageVisibility visibility)
    {
        List<EShaderType> stages = new(3);
        if ((visibility & ShaderStageVisibility.Vertex) != 0)
            stages.Add(EShaderType.Vertex);
        if ((visibility & ShaderStageVisibility.Fragment) != 0)
            stages.Add(EShaderType.Fragment);
        if ((visibility & ShaderStageVisibility.Compute) != 0)
            stages.Add(EShaderType.Compute);
        return stages;
    }

    private static string CookedUniformType(string physicalType)
        => physicalType switch
        {
            "f32" => "float",
            "i32" => "int",
            "u32" => "uint",
            "vec2<f32>" => "vec2",
            "vec3<f32>" => "vec3",
            "vec4<f32>" => "vec4",
            "vec2<i32>" => "ivec2",
            "vec3<i32>" => "ivec3",
            "vec4<i32>" => "ivec4",
            "vec2<u32>" => "uvec2",
            "vec3<u32>" => "uvec3",
            "vec4<u32>" => "uvec4",
            "mat3x3<f32>" => "mat3",
            "mat4x4<f32>" => "mat4",
            _ => physicalType,
        };
}
