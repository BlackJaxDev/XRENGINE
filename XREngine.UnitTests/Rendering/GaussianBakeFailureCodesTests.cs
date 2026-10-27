using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Models.Gaussian.Baking;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GaussianBakeFailureCodesTests
{
    [TestCase("invalid-arguments", 2)]
    [TestCase("input-load-failed", 3)]
    [TestCase("content-rejected", 4)]
    [TestCase("content-empty", 5)]
    [TestCase("budget-exceeded", 6)]
    [TestCase("unsupported-option", 7)]
    [TestCase("capture-failed", 8)]
    [TestCase("encode-failed", 9)]
    [TestCase("unexpected", 1)]
    [TestCase("", 1)]
    public void ExitCodeFor_FailureCode_ReturnsMappedExitCode(string failureCode, int expectedExitCode)
        => GaussianBakeFailureCodes.ExitCodeFor(failureCode).ShouldBe(expectedExitCode);

    [Test]
    public void FailureCodes_AsConstants_MatchWireValues()
    {
        GaussianBakeFailureCodes.InvalidArguments.ShouldBe("invalid-arguments");
        GaussianBakeFailureCodes.InputLoadFailed.ShouldBe("input-load-failed");
        GaussianBakeFailureCodes.ContentRejected.ShouldBe("content-rejected");
        GaussianBakeFailureCodes.ContentEmpty.ShouldBe("content-empty");
        GaussianBakeFailureCodes.BudgetExceeded.ShouldBe("budget-exceeded");
        GaussianBakeFailureCodes.UnsupportedOption.ShouldBe("unsupported-option");
        GaussianBakeFailureCodes.CaptureFailed.ShouldBe("capture-failed");
        GaussianBakeFailureCodes.EncodeFailed.ShouldBe("encode-failed");
        GaussianBakeFailureCodes.Unexpected.ShouldBe("unexpected");
    }
}
