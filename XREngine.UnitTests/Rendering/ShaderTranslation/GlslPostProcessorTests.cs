using NUnit.Framework;
using Shouldly;
using XREngine.ShaderTranslation.Compilation;

namespace XREngine.UnitTests.Rendering.ShaderTranslation;

[TestFixture]
public sealed class GlslPostProcessorTests
{
    [Test]
    public void Process_FragmentStage_MakesLooseUniformsAndNamesCombinedSamplers()
    {
        const string glsl = """
            #version 460

            struct GlobalParams_std140
            {
                vec4 _Color;
                float _Cutoff;
            };

            uniform GlobalParams_std140 globalParams;

            layout(binding = 2) uniform sampler2D _Legacy;
            uniform sampler2D SPIRV_Cross_Combined_MainTexsampler_MainTex;
            uniform sampler2D SPIRV_Cross_Combined_MaskTexsampler_MainTex;
            uniform sampler2D SPIRV_Cross_Combined_MainTexSPIRV_Cross_DummySampler;

            void main()
            {
                vec4 c = texture(SPIRV_Cross_Combined_MainTexsampler_MainTex, vec2(0.0)) * globalParams._Color;
                c += texture(SPIRV_Cross_Combined_MaskTexsampler_MainTex, vec2(0.0));
                c += texelFetch(SPIRV_Cross_Combined_MainTexSPIRV_Cross_DummySampler, ivec2(0), 0) + texture(_Legacy, vec2(globalParams._Cutoff));
            }
            """;
        SlangStageReflection reflection = new();
        reflection.Textures.AddRange(["_MainTex", "_MaskTex"]);
        reflection.Samplers.Add("sampler_MainTex");

        GlslPostProcessResult result = GlslPostProcessor.Process(glsl, reflection, isVertexStage: false);

        result.Glsl.ShouldContain("uniform vec4 _Color;\nuniform float _Cutoff;\n");
        result.Glsl.ShouldNotContain("globalParams");
        result.Glsl.ShouldNotContain("GlobalParams_std140");
        result.Glsl.ShouldContain("uniform sampler2D _Legacy;");
        result.Glsl.ShouldContain("uniform sampler2D _MainTex;");
        result.Glsl.ShouldContain("uniform sampler2D _MaskTex;");
        result.Glsl.ShouldNotContain("SPIRV_Cross");
        result.Glsl.ShouldContain("texelFetch(_MainTex, ivec2(0), 0)");
        result.Notes.ShouldBeEmpty();
    }

    [Test]
    public void Process_VertexStage_MovesInputsToEngineAttributeLocationsAndNames()
    {
        const string glsl = """
            #version 460

            layout(location = 0) in vec4 v_vertex;
            layout(location = 1) in vec2 v_uv;
            layout(location = 2) in vec4 v_color;
            layout(location = 3) in vec4 v_extra;
            layout(location = 0) out vec4 Color0;

            void main()
            {
                gl_Position = v_vertex + vec4(v_uv, 0.0, 0.0) + v_color + v_extra;
                Color0 = v_color;
            }
            """;
        SlangStageReflection reflection = new();
        reflection.VertexInputs.AddRange(
        [
            new VertexInputReflection("POSITION", 0, 0, 4),
            new VertexInputReflection("TEXCOORD", 1, 1, 2),
            new VertexInputReflection("COLOR", 0, 2, 4),
            new VertexInputReflection("TEXCOORD", 6, 3, 4),
        ]);

        GlslPostProcessResult result = GlslPostProcessor.Process(glsl, reflection, isVertexStage: true);

        // The mesh renderers bind vertex buffers by input name, so the inputs take the engine buffer names.
        result.Glsl.ShouldContain("layout(location = 0) in vec4 Position;");
        result.Glsl.ShouldContain("layout(location = 4) in vec2 TexCoord1;");
        result.Glsl.ShouldContain("layout(location = 7) in vec4 Color0;");
        result.Glsl.ShouldContain("layout(location = 3) in vec4 v_extra;");
        result.Glsl.ShouldContain("gl_Position = Position + vec4(TexCoord1, 0.0, 0.0) + Color0 + v_extra;");

        // An output that already has an engine attribute name gets a suffix.
        result.Glsl.ShouldContain("layout(location = 0) out vec4 Color0_xrUser;");
        result.Glsl.ShouldContain("Color0_xrUser = Color0;");
        result.Glsl.ShouldNotContain("v_vertex");
        result.UnsupportedVertexInputs.ShouldBe(["TEXCOORD6"]);
    }

    [Test]
    public void Process_EngineLightBuffer_UsesEngineBlockNameAndForwardSet()
    {
        const string glsl = """
            #version 460

            layout(binding = 22, std430) readonly buffer StructuredBuffer
            {
                XRDirLight_std430 _member0[];
            } DirectionalLights;

            void main()
            {
                vec3 d = DirectionalLights._member0[0].Direction;
            }
            """;

        GlslPostProcessResult result = GlslPostProcessor.Process(glsl, new SlangStageReflection(), isVertexStage: false);

        result.Glsl.ShouldContain("layout(std430, XRENGINE_FORWARD_DESCRIPTOR_SET binding = 22) readonly buffer ForwardDirectionalLightsBuffer\n{\n    XRDirLight_std430 DirectionalLights[];\n};");
        result.Glsl.ShouldContain("vec3 d = DirectionalLights[0].Direction;");
        result.Glsl.ShouldContain("#define XRENGINE_FORWARD_DESCRIPTOR_SET set = 3,");
    }

    [Test]
    public void Process_RewrittenGlobal_RestoresUniformName()
    {
        const string glsl = """
            #version 460
            uniform vec4 _Color_xrUniform;
            vec4 _Color;
            out vec4 color;
            void main()
            {
                _Color = _Color_xrUniform;
                _Color.w = 1.0;
                color = _Color;
            }
            """;

        GlslPostProcessResult result = GlslPostProcessor.Process(glsl, new SlangStageReflection(), isVertexStage: false, ["_Color"]);

        result.Glsl.ShouldContain("uniform vec4 _Color;\nvec4 _Color_xrLocal;");
        result.Glsl.ShouldContain("_Color_xrLocal = _Color;");
        result.Glsl.ShouldContain("_Color_xrLocal.w = 1.0;");
        result.Glsl.ShouldNotContain("_xrUniform");
    }
}
