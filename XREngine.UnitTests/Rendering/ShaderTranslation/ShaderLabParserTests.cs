using System.Linq;
using NUnit.Framework;
using Shouldly;
using XREngine.ShaderTranslation.ShaderLab;

namespace XREngine.UnitTests.Rendering.ShaderTranslation;

[TestFixture]
public sealed class ShaderLabParserTests
{
    [Test]
    public void Parse_ToonShader_ReadsPropertiesPassesAndState()
    {
        ShaderLabDocument document = ShaderLabParser.Parse(UnityShaderTestData.Read("TranslationToonLit.shader"), "TranslationToonLit.shader");

        document.Name.ShouldBe("XRTests/Translation Toon Lit");
        document.Fallback.ShouldBe("Legacy Shaders/VertexLit");
        document.IncludeBlocks.Count.ShouldBe(1);
        document.IncludeBlocks[0].Code.ShouldContain("float4 ShadeToon(");

        ShaderLabProperty color = document.FindProperty("_Color").ShouldNotBeNull();
        color.Kind.ShouldBe(ShaderLabPropertyKind.Color);
        color.DefaultValue.ShouldBe([1.0f, 1.0f, 1.0f, 1.0f]);
        ShaderLabProperty bump = document.FindProperty("_BumpMap").ShouldNotBeNull();
        bump.Kind.ShouldBe(ShaderLabPropertyKind.Texture2D);
        bump.DefaultTexture.ShouldBe("bump");
        bump.HasAttribute("Normal").ShouldBeTrue();
        ShaderLabProperty step = document.FindProperty("_ShadeStep").ShouldNotBeNull();
        step.Kind.ShouldBe(ShaderLabPropertyKind.Range);
        step.RangeMax.ShouldBe(1.0f);
        document.FindProperty("_Cull").ShouldNotBeNull().Attributes.Single().ShouldBe("Enum(UnityEngine.Rendering.CullMode)");

        ShaderLabSubShader subShader = document.SubShaders.Single();
        subShader.Tags["queue"].ShouldBe("AlphaTest");
        subShader.Commands.Single().ShouldSatisfyAllConditions(
            command => command.Name.ShouldBe("Cull"),
            command => command.Arguments.ShouldBe(["[_Cull]"]));
        subShader.Passes.Select(pass => pass.LightMode).ShouldBe(["ForwardBase", "ForwardAdd", "ShadowCaster", "Meta"]);
        subShader.Passes[1].Commands.Select(command => command.ArgumentText).ShouldBe(["One One", "Off"]);
        subShader.Passes[3].Program.ShouldBeNull();
        subShader.Passes[0].Program.ShouldNotBeNull().Language.ShouldBe(ShaderLabProgramLanguage.Cg);
    }

    [Test]
    public void Parse_StencilBlock_ReadsNestedCommandsAndHlslProgram()
    {
        ShaderLabDocument document = ShaderLabParser.Parse(UnityShaderTestData.Read("TranslationScreenEye.shader"), "TranslationScreenEye.shader");

        ShaderLabPass pass = document.SubShaders.Single().Passes.Single();
        pass.Program.ShouldNotBeNull().Language.ShouldBe(ShaderLabProgramLanguage.Hlsl);
        ShaderLabCommand stencil = pass.Commands.Single(command => command.Name == "Stencil");
        stencil.Children.Select(child => child.Name + " " + child.ArgumentText).ShouldBe(["Ref [_StencilRef]", "Comp Always", "Pass Replace"]);
        pass.Commands.Single(command => command.Name == "Blend").Arguments.ShouldBe(["SrcAlpha", "OneMinusSrcAlpha", ",", "One", "OneMinusSrcAlpha"]);
    }

    [Test]
    public void Parse_ProgramBlock_KeepsTextAndFirstLine()
    {
        const string source = "Shader \"A\"\n{\n    SubShader\n    {\n        Pass\n        {\n            CGPROGRAM\n            // ENDCGX is not an end marker\n            float4 frag() : SV_Target { return 1; }\n            ENDCG\n        }\n    }\n}\n";

        ShaderLabDocument document = ShaderLabParser.Parse(source, "A.shader");

        ShaderLabProgramBlock program = document.SubShaders[0].Passes[0].Program.ShouldNotBeNull();
        program.FirstLine.ShouldBe(7);
        program.Code.ShouldContain("ENDCGX is not an end marker");
        program.Code.ShouldContain("return 1;");
    }

    [Test]
    public void Parse_MissingBrace_ReportsLine()
    {
        const string source = "Shader \"Broken\"\n{\n    Properties\n    {\n        _Color (\"Color\", Color) = (1, 1, 1, 1)\n";

        ShaderLabParseException exception = Should.Throw<ShaderLabParseException>(() => ShaderLabParser.Parse(source, "Broken.shader"));

        exception.SourcePath.ShouldBe("Broken.shader");
        exception.Line.ShouldBe(6);
    }

    [Test]
    public void Parse_CategoryState_AppliesToNestedSubShaders()
    {
        const string source = "Shader \"C\" { Category { ZWrite Off\n SubShader { Pass { } } } }";

        ShaderLabDocument document = ShaderLabParser.Parse(source, "C.shader");

        document.SubShaders.Single().Commands.Single().ShouldSatisfyAllConditions(
            command => command.Name.ShouldBe("ZWrite"),
            command => command.Arguments.ShouldBe(["Off"]));
    }

    [Test]
    public void Parse_CategoryTags_SubShaderTagsWin()
    {
        const string source = "Shader \"C\" { Category { Tags { \"Queue\" = \"Transparent\" \"RenderType\" = \"Transparent\" }\n SubShader { Tags { \"RenderType\" = \"Opaque\" } Pass { } } } }";

        ShaderLabDocument document = ShaderLabParser.Parse(source, "C.shader");

        ShaderLabSubShader subShader = document.SubShaders.Single();
        subShader.Tags["Queue"].ShouldBe("Transparent");
        subShader.Tags["RenderType"].ShouldBe("Opaque");
    }
}
