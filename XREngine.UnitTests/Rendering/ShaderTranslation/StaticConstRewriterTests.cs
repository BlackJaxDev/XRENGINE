using NUnit.Framework;
using Shouldly;
using XREngine.ShaderTranslation.Preprocessing;

namespace XREngine.UnitTests.Rendering.ShaderTranslation;

[TestFixture]
public sealed class StaticConstRewriterTests
{
    [Test]
    public void Rewrite_InitializerWithCall_RemovesConst()
    {
        const string code = "static const float3 Gray = ToLinear(float3(0.5, 0.5, 0.5));\nstatic const float2 Half = float2(0.5, 0.5);\nstatic const int Count = 4;\n";

        string rewritten = StaticConstRewriter.Rewrite(code);

        rewritten.ShouldBe("static       float3 Gray = ToLinear(float3(0.5, 0.5, 0.5));\nstatic const float2 Half = float2(0.5, 0.5);\nstatic const int Count = 4;\n");
    }
}
