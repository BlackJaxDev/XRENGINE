using NUnit.Framework;
using Shouldly;
using XREngine.ShaderTranslation.Preprocessing;

namespace XREngine.UnitTests.Rendering.ShaderTranslation;

[TestFixture]
public sealed class GlobalWriteRewriterTests
{
    [Test]
    public void Rewrite_WrittenGlobal_GetsPrivateCopyOnSameLine()
    {
        const string code = "float4 _Color;\nfloat _Speed, mask;\nfloat4 frag() : SV_Target\n{\n    _Color.a = 1;\n    mask += _Speed;\n    return _Color * mask;\n}\n";

        GlobalWriteRewriteResult result = GlobalWriteRewriter.Rewrite(code);

        result.RewrittenGlobals.ShouldBe(["_Color", "mask"]);
        result.Text.ShouldContain("float4 _Color_xrUniform; static float4 _Color = _Color_xrUniform;\n");
        result.Text.ShouldContain("float _Speed, mask_xrUniform; static float mask = mask_xrUniform;\n");
        result.Text.ShouldContain("    _Color.a = 1;\n");
        result.Text.Split('\n').Length.ShouldBe(code.Split('\n').Length);
    }

    [Test]
    public void Rewrite_ReadOnlyStaticAndMemberAccess_AreNotChanged()
    {
        const string code = """
            static float counter;
            float4 _Tint;
            struct Data { float4 _Tint; };
            float4 frag(Data data) : SV_Target
            {
                counter++;
                data._Tint = 0;
                bool same = _Tint.x == 1;
                return _Tint + data._Tint;
            }
            """;

        GlobalWriteRewriteResult result = GlobalWriteRewriter.Rewrite(code);

        result.RewrittenGlobals.ShouldBeEmpty();
        result.Text.ShouldBe(code);
    }

    [Test]
    public void Rewrite_BufferMember_DeclaresCopyAfterBuffer()
    {
        const string code = "CBUFFER_START(UnityPerMaterial)\nfloat4 _BaseColor;\nfloat _Cutoff;\nCBUFFER_END\ncbuffer Extra { float2 _Offset[2]; };\nvoid f() { _Cutoff = 0.5; _Offset[1].x -= 1; }\n";

        GlobalWriteRewriteResult result = GlobalWriteRewriter.Rewrite(code);

        result.RewrittenGlobals.ShouldBe(["_Cutoff", "_Offset"]);
        result.Text.ShouldContain("float _Cutoff_xrUniform;\nCBUFFER_END static float _Cutoff = _Cutoff_xrUniform;\n");
        result.Text.ShouldContain("cbuffer Extra { float2 _Offset_xrUniform[2]; }; static float2 _Offset[2] = _Offset_xrUniform;\n");
    }

    [Test]
    public void Rewrite_IgnoresFunctionsInitializersAndDirectives()
    {
        const string code = "#define SET(x) gValue = x\nfloat gValue;\nstatic const float table[2] = { 1, 2 };\nfloat Helper(float a);\nfloat Helper(float a) { float gValue = a; return gValue; }\n";

        GlobalWriteRewriteResult result = GlobalWriteRewriter.Rewrite(code);

        // The local declaration looks like a write. The private copy keeps the same value, so the rewrite is safe.
        result.RewrittenGlobals.ShouldBe(["gValue"]);
        result.Text.ShouldStartWith("#define SET(x) gValue = x\nfloat gValue_xrUniform; static float gValue = gValue_xrUniform;\nstatic const float table[2] = { 1, 2 };\n");
    }

    [Test]
    public void Rewrite_ParameterInitializer_IsRemovedAndKeepsLines()
    {
        const string code = "uniform float gScale = 0.0f;\nfloat4 gTint = float4(1,\n2, 3, 4), gOther;\nstatic float gKept = 1;\n";

        GlobalWriteRewriteResult result = GlobalWriteRewriter.Rewrite(code);

        result.RewrittenGlobals.ShouldBeEmpty();
        result.Text.ShouldBe("uniform float gScale       ;\nfloat4 gTint            \n        , gOther;\nstatic float gKept = 1;\n");
    }
}
