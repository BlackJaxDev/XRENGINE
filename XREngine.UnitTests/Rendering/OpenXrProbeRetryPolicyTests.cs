using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenXR;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class OpenXrProbeRetryPolicyTests
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);

    [Test]
    public void GetSystem_FormFactorUnavailable_RetriesWithoutTreatingItAsConfigurationFailure()
    {
        OpenXrProbeRetryDecision decision = OpenXrProbeRetryPolicy.ForGetSystemResult(
            Result.ErrorFormFactorUnavailable,
            consecutiveFailures: 1,
            InitialDelay,
            MaximumDelay);

        decision.ShouldRetry.ShouldBeTrue();
        decision.Delay.ShouldBe(InitialDelay);
        decision.Category.ShouldBe("headset-or-form-factor-unavailable");
        decision.RecreateInstance.ShouldBeFalse();
    }

    [TestCase(Result.ErrorRuntimeUnavailable)]
    [TestCase(Result.ErrorRuntimeFailure)]
    [TestCase(Result.ErrorInitializationFailed)]
    [TestCase(Result.ErrorInstanceLost)]
    public void CreateInstance_RecoverableRuntimeResults_UseBackoff(Result result)
    {
        OpenXrProbeRetryDecision decision = OpenXrProbeRetryPolicy.ForCreateInstanceResult(
            result,
            consecutiveFailures: 3,
            InitialDelay,
            MaximumDelay);

        decision.ShouldRetry.ShouldBeTrue();
        decision.Delay.ShouldBe(TimeSpan.FromSeconds(6));
        decision.Category.ShouldBe("recoverable-runtime");
        decision.RecreateInstance.ShouldBeTrue();
    }

    [TestCase(Result.ErrorExtensionNotPresent)]
    [TestCase(Result.ErrorApiLayerNotPresent)]
    [TestCase(Result.ErrorApiVersionUnsupported)]
    [TestCase(Result.ErrorValidationFailure)]
    public void CreateInstance_ConfigurationResults_HaltUntilReconfigured(Result result)
    {
        OpenXrProbeRetryDecision decision = OpenXrProbeRetryPolicy.ForCreateInstanceResult(
            result,
            consecutiveFailures: 1,
            InitialDelay,
            MaximumDelay);

        decision.ShouldRetry.ShouldBeFalse();
        decision.Delay.ShouldBe(Timeout.InfiniteTimeSpan);
        decision.Category.ShouldBe("configuration-or-capability");
        decision.RecreateInstance.ShouldBeFalse();
    }

    [Test]
    public void CalculateBackoff_CapsAtMaximumDelay()
    {
        OpenXrProbeRetryPolicy.CalculateBackoff(
                consecutiveFailures: 20,
                InitialDelay,
                MaximumDelay)
            .ShouldBe(MaximumDelay);
    }
}
