using System.Linq;
using NUnit.Framework;
using Shouldly;
using XREngine.ShaderTranslation.ShaderLab;
using XREngine.ShaderTranslation.Translation;

namespace XREngine.UnitTests.Rendering.ShaderTranslation;

[TestFixture]
public sealed class ShaderPassAssemblerTests
{
    [Test]
    public void Assemble_CgPass_AddsImplicitIncludesKeywordsAndLineDirectives()
    {
        ShaderLabDocument document = ShaderLabParser.Parse(UnityShaderTestData.Read("TranslationToonLit.shader"), "Shaders/TranslationToonLit.shader");
        ShaderLabSubShader subShader = document.SubShaders[0];
        ShaderLabPass pass = subShader.Passes[0];

        string unit = ShaderPassAssembler.Assemble(
            document,
            ShaderPassAssembler.GetIncludeBlocks(document, subShader, pass),
            pass.Program!,
            ShaderProgramStage.Fragment,
            "fragBase",
            EnginePassKind.ForwardBase,
            45,
            ["DIRECTIONAL", "_NORMALMAP"]);

        unit.ShouldContain("#define SHADER_TARGET 45\n");
        unit.ShouldContain("#define SHADER_STAGE_FRAGMENT 1\n");
        unit.ShouldContain("#define UNITY_PASS_FORWARDBASE 1\n");
        unit.ShouldContain("#define _NORMALMAP 1\n");
        unit.IndexOf("#include \"HLSLSupport.cginc\"").ShouldBeLessThan(unit.IndexOf("#include \"UnityShaderVariables.cginc\""));
        unit.ShouldContain($"#line {document.IncludeBlocks[0].FirstLine} \"Shaders/TranslationToonLit.shader\"");
        unit.ShouldContain($"#line {pass.Program!.FirstLine} \"Shaders/TranslationToonLit.shader\"");
        unit.ShouldNotContain("#pragma");
    }

    [Test]
    public void Assemble_BlanksPragmasWithoutChangingLineCount()
    {
        ShaderLabProgramBlock program = new(ShaderLabProgramLanguage.Hlsl, false, "\n#pragma vertex v\n  # pragma fragment f\nfloat4 x;\n", 10);
        ShaderLabDocument document = ShaderLabParser.Parse("Shader \"S\" { SubShader { Pass { } } }", "S.shader");

        string unit = ShaderPassAssembler.Assemble(document, [], program, ShaderProgramStage.Vertex, "v", EnginePassKind.Unlit, 25, []);

        string body = unit[(unit.IndexOf("#line 10 \"S.shader\"\n") + "#line 10 \"S.shader\"\n".Length)..];
        body.Split('\n').Take(4).ShouldBe(["", "", "", "float4 x;"]);
        unit.ShouldNotContain("HLSLSupport.cginc");
    }

    [Test]
    public void RewriteFrontFacing_ReplacesVfaceWithSignedFloat()
    {
        const string code = "float4 frag(v2f i,\n  fixed facing : VFACE) : SV_Target\n{\n  return facing;\n}\n";

        string rewritten = ShaderPassAssembler.RewriteFrontFacing(code, "frag");

        rewritten.ShouldContain("bool facing_xrFrontFacing : SV_IsFrontFace");
        rewritten.ShouldContain("{ float facing = facing_xrFrontFacing ? 1.0 : -1.0;");
        rewritten.Count(c => c == '\n').ShouldBe(code.Count(c => c == '\n'));
    }

    [Test]
    public void RewriteFrontFacing_IgnoresPrototypeAndOtherFunctions()
    {
        const string code = "float4 frag(v2f i, float facing : VFACE);\nfloat4 other(float facing : VFACE) { return 0; }\n";

        ShaderPassAssembler.RewriteFrontFacing(code, "frag").ShouldBe(code);
    }
}
