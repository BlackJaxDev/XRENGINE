using NUnit.Framework;
using Shouldly;
using XREngine.ShaderTranslation.Preprocessing;

namespace XREngine.UnitTests.Rendering.ShaderTranslation;

[TestFixture]
public sealed class SwitchSelectorRewriterTests
{
    [Test]
    public void Rewrite_FloatSelector_ConvertsToInt()
    {
        const string code = "// switch (comment)\nvoid F() { switch (Mode(a, b))\n{ case 0: break; } }\n";

        string rewritten = SwitchSelectorRewriter.Rewrite(code);

        rewritten.ShouldBe("// switch (comment)\nvoid F() { switch (int(Mode(a, b)))\n{ case 0: break; } }\n");
    }
}
