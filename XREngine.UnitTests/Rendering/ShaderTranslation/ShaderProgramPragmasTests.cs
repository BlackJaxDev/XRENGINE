using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Shouldly;
using XREngine.ShaderTranslation.Translation;

namespace XREngine.UnitTests.Rendering.ShaderTranslation;

[TestFixture]
public sealed class ShaderProgramPragmasTests
{
    [Test]
    public void Parse_ReadsEntryPointsTargetAndStageKeywords()
    {
        const string code = """
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag // fragment entry
            #pragma shader_feature_local_fragment _NORMALMAP
            #pragma multi_compile _ _ALPHA_A _ALPHA_B
            #pragma skip_variants FOG_EXP
            """;

        ShaderProgramPragmas pragmas = ShaderProgramPragmas.Parse([(code, 1)]);

        pragmas.Target.ShouldBe(45);
        pragmas.GetEntryPoint(ShaderProgramStage.Vertex).ShouldBe("vert");
        pragmas.GetEntryPoint(ShaderProgramStage.Fragment).ShouldBe("frag");
        pragmas.Keywords[0].ShouldSatisfyAllConditions(
            declaration => declaration.Directive.ShouldBe(ShaderKeywordDirective.ShaderFeature),
            declaration => declaration.Stage.ShouldBe(ShaderProgramStage.Fragment),
            declaration => declaration.Keywords.ShouldBe([null, "_NORMALMAP"]),
            declaration => declaration.Line.ShouldBe(4));
        pragmas.Keywords[1].Keywords.ShouldBe([null, "_ALPHA_A", "_ALPHA_B"]);
        pragmas.Other.ShouldBe(["skip_variants FOG_EXP"]);
    }

    [Test]
    public void Select_UsesEnabledKeywordThenEmptyOptionThenFirstKeyword()
    {
        const string code = """
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fwdadd
            #pragma shader_feature _NORMALMAP
            #pragma shader_feature _MODE_A _MODE_B
            #pragma shader_feature_local_vertex _VERTEX_ONLY
            """;
        ShaderProgramPragmas pragmas = ShaderProgramPragmas.Parse([(code, 1)]);
        HashSet<string> enabled = ["LIGHTPROBE_SH", "SPOT", "_VERTEX_ONLY"];

        IReadOnlyList<string> fragment = ShaderVariantSelector.Select(pragmas, enabled, ShaderProgramStage.Fragment);
        IReadOnlyList<string> vertex = ShaderVariantSelector.Select(pragmas, enabled, ShaderProgramStage.Vertex);

        fragment.ShouldBe(["DIRECTIONAL", "LIGHTPROBE_SH", "SPOT", "_MODE_A"]);
        vertex.ShouldBe(["DIRECTIONAL", "LIGHTPROBE_SH", "SPOT", "_MODE_A", "_VERTEX_ONLY"]);
    }

    [Test]
    public void Parse_IncludeBlockPragmasComeFirst()
    {
        ShaderProgramPragmas pragmas = ShaderProgramPragmas.Parse(
        [
            ("#pragma multi_compile _ _FROM_INCLUDE", 3),
            ("#pragma vertex v\n#pragma fragment f", 20),
        ]);

        pragmas.Keywords.Single().Keywords.ShouldBe([null, "_FROM_INCLUDE"]);
        pragmas.Keywords.Single().Line.ShouldBe(3);
    }
}
