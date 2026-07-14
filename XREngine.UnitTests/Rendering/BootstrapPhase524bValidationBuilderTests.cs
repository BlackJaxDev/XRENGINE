using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap.Builders;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class BootstrapPhase524bValidationBuilderTests
{
    [Test]
    public void MovingSentinelTrajectory_IsPeriodicAndStaysOutsideCentralOccluder()
    {
        Vector3 start = BootstrapPhase524bValidationBuilder.CalculateMovingSentinelTranslation(0u);
        Vector3 repeated = BootstrapPhase524bValidationBuilder.CalculateMovingSentinelTranslation(120u);

        Vector3.Distance(repeated, start).ShouldBeLessThan(1.0e-6f);

        for (uint tick = 0u; tick < 120u; tick++)
        {
            Vector3 translation = BootstrapPhase524bValidationBuilder.CalculateMovingSentinelTranslation(tick);
            translation.X.ShouldBeInRange(-2.7001f, -1.9999f);
            translation.Y.ShouldBeInRange(0.5999f, 1.0001f);
            translation.Z.ShouldBe(-5.50f, 1.0e-6f);

            // The mover's right face remains left of the central occluder's left face.
            (translation.X + 0.35f).ShouldBeLessThan(-1.30f);
        }
    }

    [Test]
    public void HeadsetRelativeMovingSentinelTrajectory_IsPeriodicVisibleAndAlwaysMoving()
    {
        Vector3 start = BootstrapPhase524bValidationBuilder.CalculateHeadsetRelativeMovingSentinelTranslation(0u);
        Vector3 repeated = BootstrapPhase524bValidationBuilder.CalculateHeadsetRelativeMovingSentinelTranslation(120u);

        Vector3.Distance(repeated, start).ShouldBeLessThan(1.0e-6f);

        for (uint tick = 0u; tick < 120u; tick++)
        {
            Vector3 translation = BootstrapPhase524bValidationBuilder.CalculateHeadsetRelativeMovingSentinelTranslation(tick);
            Vector3 next = BootstrapPhase524bValidationBuilder.CalculateHeadsetRelativeMovingSentinelTranslation(tick + 1u);

            translation.X.ShouldBeInRange(-0.2801f, -0.1599f);
            translation.Y.ShouldBeInRange(0.1199f, 0.1801f);
            translation.Z.ShouldBe(-0.65f, 1.0e-6f);

            // Conservative normalized view bounds keep the complete 0.16 m box
            // comfortably visible for ordinary OpenXR projection fields of view.
            MathF.Abs(translation.X / translation.Z).ShouldBeLessThan(0.46f);
            MathF.Abs(translation.Y / translation.Z).ShouldBeLessThan(0.30f);

            // The two-axis harmonic path has no stationary update interval, so
            // every captured frame receives a measurable transform delta.
            Vector3.Distance(next, translation).ShouldBeGreaterThan(0.002f);
        }
    }

    [Test]
    public void TemporalScenarioSchedule_CoversAllRequiredScenariosBeforeWarmupCompletes()
    {
        Phase524bTemporalScenarioDiagnostics.SequenceCompleteFrame.ShouldBeLessThan(100);
        Phase524bTemporalScenarioDiagnostics.Definitions.Length.ShouldBe(8);
        Phase524bTemporalScenarioDiagnostics.Definitions.ToArray()
            .Select(static definition => definition.Scenario)
            .Distinct()
            .ShouldBe([
                EPhase524bTemporalScenario.ObjectMotion,
                EPhase524bTemporalScenario.StaticPose,
                EPhase524bTemporalScenario.HeadRotation,
                EPhase524bTemporalScenario.HeadTranslation,
                EPhase524bTemporalScenario.Disocclusion,
                EPhase524bTemporalScenario.MotionStop,
            ], ignoreOrder: true);

        BootstrapPhase524bValidationBuilder.ResolveTemporalScenario(8)
            .ShouldBe(EPhase524bTemporalScenario.ObjectMotion);
        BootstrapPhase524bValidationBuilder.ResolveTemporalScenario(16)
            .ShouldBe(EPhase524bTemporalScenario.StaticPose);
        BootstrapPhase524bValidationBuilder.ResolveTemporalScenario(24)
            .ShouldBe(EPhase524bTemporalScenario.HeadRotation);
        BootstrapPhase524bValidationBuilder.ResolveTemporalScenario(32)
            .ShouldBe(EPhase524bTemporalScenario.HeadTranslation);
        BootstrapPhase524bValidationBuilder.ResolveTemporalScenario(48)
            .ShouldBe(EPhase524bTemporalScenario.Disocclusion);
        BootstrapPhase524bValidationBuilder.ResolveTemporalScenario(68)
            .ShouldBe(EPhase524bTemporalScenario.MotionStop);
    }

    [Test]
    public void TemporalScenarioTransforms_ProvideMovingStoppedHeadAndDisocclusionOracles()
    {
        BootstrapPhase524bValidationBuilder.CalculateTemporalMovingSentinelTranslation(16, false)
            .ShouldBe(BootstrapPhase524bValidationBuilder.CalculateTemporalMovingSentinelTranslation(18, false));
        BootstrapPhase524bValidationBuilder.CalculateTemporalHeadYawDegrees(25)
            .ShouldBeGreaterThan(BootstrapPhase524bValidationBuilder.CalculateTemporalHeadYawDegrees(24));
        BootstrapPhase524bValidationBuilder.CalculateTemporalHeadTranslation(33).X
            .ShouldBeGreaterThan(BootstrapPhase524bValidationBuilder.CalculateTemporalHeadTranslation(32).X);
        BootstrapPhase524bValidationBuilder.CalculateTemporalOccluderTranslation(48).X
            .ShouldBeGreaterThan(BootstrapPhase524bValidationBuilder.CalculateTemporalOccluderTranslation(40).X + 3.0f);
        BootstrapPhase524bValidationBuilder.CalculateTemporalMovingSentinelTranslation(58, true).X
            .ShouldBeGreaterThan(BootstrapPhase524bValidationBuilder.CalculateTemporalMovingSentinelTranslation(56, true).X);
        BootstrapPhase524bValidationBuilder.CalculateTemporalMovingSentinelTranslation(68, true)
            .ShouldBe(BootstrapPhase524bValidationBuilder.CalculateTemporalMovingSentinelTranslation(70, true));
    }

    [Test]
    [NonParallelizable]
    public void TemporalScenarioClock_AdvancesOncePerCompletedStrictSpsFrame()
    {
        Phase524bTemporalScenarioDiagnostics.Reset();
        Phase524bTemporalScenarioDiagnostics.SequenceFrame.ShouldBe(0);

        Phase524bTemporalScenarioDiagnostics.CompleteStrictSpsFrame(100UL);
        Phase524bTemporalScenarioDiagnostics.CompleteStrictSpsFrame(100UL);
        Phase524bTemporalScenarioDiagnostics.SequenceFrame.ShouldBe(1);

        for (ulong frame = 101UL; frame <= 107UL; frame++)
            Phase524bTemporalScenarioDiagnostics.CompleteStrictSpsFrame(frame);

        Phase524bTemporalScenarioDiagnostics.SequenceFrame.ShouldBe(8);
        Phase524bTemporalScenarioDiagnostics.TryGetActiveCaptureSample(out _, out var definition)
            .ShouldBeTrue();
        definition.Sample.ShouldBe(EPhase524bTemporalSample.ObjectMotionActive);
    }

    [Test]
    [NonParallelizable]
    public void TemporalScenarioClock_PostSequenceMotionContinuesWithoutReopeningCaptureWindows()
    {
        Phase524bTemporalScenarioDiagnostics.Reset();
        for (ulong frame = 1UL; frame <= 75UL; frame++)
            Phase524bTemporalScenarioDiagnostics.CompleteStrictSpsFrame(frame);

        Phase524bTemporalScenarioDiagnostics.SequenceFrame.ShouldBe(75);
        Phase524bTemporalScenarioDiagnostics.TryGetActiveCaptureSample(out _, out _)
            .ShouldBeFalse();
        BootstrapPhase524bValidationBuilder.CalculateTemporalMovingSentinelTranslation(72, true)
            .ShouldNotBe(BootstrapPhase524bValidationBuilder.CalculateTemporalMovingSentinelTranslation(73, true));

        for (ulong frame = 76UL; frame <= 192UL; frame++)
            Phase524bTemporalScenarioDiagnostics.CompleteStrictSpsFrame(frame);

        Phase524bTemporalScenarioDiagnostics.SequenceFrame.ShouldBe(72);
        Phase524bTemporalScenarioDiagnostics.TryGetActiveCaptureSample(out _, out _)
            .ShouldBeFalse();
    }

    [Test]
    [NonParallelizable]
    public void BoundaryCaptureMotion_UsesFixedCrossProcessPosesAndResetClearsOverride()
    {
        Phase524bTemporalScenarioDiagnostics.Reset();
        try
        {
            Phase524bTemporalScenarioDiagnostics.SetBoundaryCaptureMotionIndex(0);
            Phase524bTemporalScenarioDiagnostics.TryGetBoundaryCaptureMotionTick(out uint firstTick)
                .ShouldBeTrue();
            firstTick.ShouldBe(0u);
            Vector3 first = BootstrapPhase524bValidationBuilder.CalculateTemporalMovingSentinelTranslation(
                Phase524bTemporalScenarioDiagnostics.SequenceCompleteFrame,
                headsetRelative: true);

            Phase524bTemporalScenarioDiagnostics.SetBoundaryCaptureMotionIndex(1);
            Phase524bTemporalScenarioDiagnostics.TryGetBoundaryCaptureMotionTick(out uint secondTick)
                .ShouldBeTrue();
            secondTick.ShouldBe((uint)Phase524bTemporalScenarioDiagnostics.BoundaryCaptureMotionTickStep);
            Vector3 second = BootstrapPhase524bValidationBuilder.CalculateTemporalMovingSentinelTranslation(
                Phase524bTemporalScenarioDiagnostics.SequenceCompleteFrame + 47,
                headsetRelative: true);

            first.ShouldBe(BootstrapPhase524bValidationBuilder.CalculateHeadsetRelativeMovingSentinelTranslation(firstTick));
            second.ShouldBe(BootstrapPhase524bValidationBuilder.CalculateHeadsetRelativeMovingSentinelTranslation(secondTick));
            second.ShouldNotBe(first);
        }
        finally
        {
            Phase524bTemporalScenarioDiagnostics.Reset();
        }

        Phase524bTemporalScenarioDiagnostics.TryGetBoundaryCaptureMotionTick(out _)
            .ShouldBeFalse();
    }

    [Test]
    [NonParallelizable]
    public void ValidationWorkload_RequiresDedicatedExplicitOptIn()
    {
        string variable = XREngineEnvironmentVariables.VulkanPhase524bValidation;
        string? previous = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            BootstrapPhase524bValidationBuilder.IsEnabled.ShouldBeFalse();

            Environment.SetEnvironmentVariable(variable, "0");
            BootstrapPhase524bValidationBuilder.IsEnabled.ShouldBeFalse();

            Environment.SetEnvironmentVariable(variable, "1");
            BootstrapPhase524bValidationBuilder.IsEnabled.ShouldBeTrue();

            Environment.SetEnvironmentVariable(variable, "true");
            BootstrapPhase524bValidationBuilder.IsEnabled.ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}
