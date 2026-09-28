using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Emits the supported opaque browser mesh shader from authored material semantics.</summary>
public static class BrowserMaterialShaderGenerator
{
    // The explicit bind group layout retains the texture and sampler slots for both variants.
    private const string MeshShader = """
        struct ViewUniforms {
            transform: mat4x4<f32>,
        };

        struct MaterialUniforms {
            tint: vec4f,
        };

        @group(0) @binding(0) var<uniform> view: ViewUniforms;
        @group(1) @binding(0) var<uniform> material: MaterialUniforms;
        @group(1) @binding(1) var colorTexture: texture_2d<f32>;
        @group(1) @binding(2) var colorSampler: sampler;

        struct VertexInput {
            @location(0) position: vec3f,
            @location(1) uv: vec2f,
        };

        struct VertexOutput {
            @builtin(position) position: vec4f,
            @location(0) uv: vec2f,
        };

        @vertex
        fn vertexMain(input: VertexInput) -> VertexOutput {
            var output: VertexOutput;
            output.position = view.transform * vec4f(input.position, 1.0);
            output.uv = input.uv;
            return output;
        }

        """;

    private const string TintFragment = """
        @fragment
        fn fragmentMain(input: VertexOutput) -> @location(0) vec4f {
            return vec4f(material.tint.rgb, 1.0);
        }
        """;

    private const string TextureFragment = """
        @fragment
        fn fragmentMain(input: VertexOutput) -> @location(0) vec4f {
            let sampledColor = textureSample(colorTexture, colorSampler, input.uv);
            return vec4f(sampledColor.rgb * material.tint.rgb, 1.0);
        }
        """;

    /// <summary>Generates WGSL for an unlit, opaque tint or sampled-color browser material.</summary>
    public static ShaderArtifact Generate(BrowserMaterialShaderDefinition definition, ShaderCompileTarget target)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (string.IsNullOrWhiteSpace(definition.Name))
            throw Unsupported(definition, target, "a nonempty material name is required");
        if (target != ShaderCompileTarget.WebGPUWgsl)
            throw Unsupported(definition, target, "only WebGPU WGSL generation is supported");
        if (definition.ShadingModel != "unlit")
            throw Unsupported(definition, target, $"shading model '{definition.ShadingModel}' is unsupported; lighting is unavailable");
        if (definition.Surface != "opaque")
            throw Unsupported(definition, target, $"surface '{definition.Surface}' is unsupported; transparent and masked passes are unavailable");
        if (definition.Skinning)
            throw Unsupported(definition, target, "skinning is unsupported");
        if (definition.Storage)
            throw Unsupported(definition, target, "storage resources are unsupported");
        if (definition.Bindless)
            throw Unsupported(definition, target, "bindless resources are unsupported");

        string fragment = definition.BaseColor switch
        {
            "tint" => TintFragment,
            "texture" => TextureFragment,
            _ => throw Unsupported(definition, target, $"base color '{definition.BaseColor}' is unsupported"),
        };
        return ShaderArtifact.FromWgsl(MeshShader + fragment);
    }

    private static NotSupportedException Unsupported(BrowserMaterialShaderDefinition definition,
        ShaderCompileTarget target, string reason) =>
        new($"Browser material '{definition.Name}' pass '{definition.Surface}' target '{target}': {reason}.");
}
