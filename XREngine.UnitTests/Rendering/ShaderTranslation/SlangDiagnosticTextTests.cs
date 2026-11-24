using NUnit.Framework;
using Shouldly;
using XREngine.ShaderTranslation.Compilation;

namespace XREngine.UnitTests.Rendering.ShaderTranslation;

[TestFixture]
public sealed class SlangDiagnosticTextTests
{
    [Test]
    public void OrderErrorsFirst_WarningBeforeError_MovesErrorWithItsNote()
    {
        const string output = "warning[E15400]: macro redefined\n --> a.hlsl:1:1\nerror[E30015]: undefined identifier\n --> a.hlsl:2:1\nnote: see definition\nwarning[E15601]: unknown pragma\n";

        string ordered = SlangDiagnosticText.OrderErrorsFirst(output);

        ordered.ShouldBe("error[E30015]: undefined identifier\n --> a.hlsl:2:1\nnote: see definition\nwarning[E15400]: macro redefined\n --> a.hlsl:1:1\nwarning[E15601]: unknown pragma");
    }

    [Test]
    public void OrderErrorsFirst_NoError_ReturnsInput()
    {
        const string output = "warning[E15400]: macro redefined\n";

        SlangDiagnosticText.OrderErrorsFirst(output).ShouldBe(output);
    }
}
