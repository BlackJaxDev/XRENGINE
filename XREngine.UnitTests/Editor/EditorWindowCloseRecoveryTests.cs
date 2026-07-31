using NUnit.Framework;
using Shouldly;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class EditorWindowCloseRecoveryTests
{
    [TestCase(0, false, false)]
    [TestCase(1, false, false)]
    [TestCase(2, false, false)]
    [TestCase(3, false, true)]
    [TestCase(10, false, true)]
    [TestCase(0, true, true)]
    public void UnsavedChangesPrompt_IsBypassedOnlyWhenRenderingCannotShowIt(
        int consecutiveRenderFailures,
        bool renderPermanentlyDisabled,
        bool expected)
        => EditorImGuiUI.ShouldBypassUnsavedChangesPromptForRenderFailure(
                consecutiveRenderFailures,
                renderPermanentlyDisabled)
            .ShouldBe(expected);
}
