using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class HumanoidSettingsTests
{
    [Test]
    public void GetResolvedMuscleRotationDegRange_UsesExpectedRepresentativeDefaults()
    {
        var settings = new HumanoidSettings();

        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.NeckNodDownUp).ShouldBe(new Vector2(-40.0f, 40.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.JawClose).ShouldBe(new Vector2(-10.0f, 10.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftShoulderDownUp).ShouldBe(new Vector2(15.0f, -30.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightShoulderFrontBack).ShouldBe(new Vector2(15.0f, -15.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftArmDownUp).ShouldBe(new Vector2(60.0f, -100.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightHandInOut).ShouldBe(new Vector2(40.0f, -40.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftUpperLegFrontBack).ShouldBe(new Vector2(90.0f, -50.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightUpperLegFrontBack).ShouldBe(new Vector2(90.0f, -50.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftUpperLegInOut).ShouldBe(new Vector2(60.0f, -60.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightUpperLegInOut).ShouldBe(new Vector2(60.0f, -60.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftUpperLegTwistInOut).ShouldBe(new Vector2(-60.0f, 60.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightUpperLegTwistInOut).ShouldBe(new Vector2(60.0f, -60.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftForearmStretch).ShouldBe(new Vector2(-80.0f, 80.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightForearmStretch).ShouldBe(new Vector2(-80.0f, 80.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftLowerLegStretch).ShouldBe(new Vector2(-80.0f, 80.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightLowerLegStretch).ShouldBe(new Vector2(-80.0f, 80.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftLowerLegTwistInOut).ShouldBe(new Vector2(-90.0f, 90.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightLowerLegTwistInOut).ShouldBe(new Vector2(90.0f, -90.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftFootTwistInOut).ShouldBe(new Vector2(30.0f, -30.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightFootTwistInOut).ShouldBe(new Vector2(30.0f, -30.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftFootUpDown).ShouldBe(new Vector2(50.0f, -50.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightFootUpDown).ShouldBe(new Vector2(50.0f, -50.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftToesUpDown).ShouldBe(new Vector2(50.0f, -50.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightToesUpDown).ShouldBe(new Vector2(50.0f, -50.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftHandThumb2Stretched).ShouldBe(new Vector2(40.0f, -35.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightHandThumb2Stretched).ShouldBe(new Vector2(40.0f, -35.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftHandIndexSpread).ShouldBe(new Vector2(20.0f, -20.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightHandLittle3Stretched).ShouldBe(new Vector2(45.0f, -45.0f));
    }

    [Test]
    public void GetResolvedMuscleRotationDegRange_PrefersPerMuscleOverride_AndOtherwiseUsesSharedFallback()
    {
        var settings = new HumanoidSettings
        {
            ArmDownUpDegRange = new Vector2(-72.0f, 111.0f),
        };

        settings.SetMuscleRotationDegRange(EHumanoidValue.LeftArmDownUp, new Vector2(-9.0f, 14.0f));

        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftArmDownUp).ShouldBe(new Vector2(-9.0f, 14.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightArmDownUp).ShouldBe(new Vector2(-72.0f, 111.0f));
    }

    [Test]
    public void NegateAllRanges_NegatesSharedFallbacks_AndExplicitOverrides()
    {
        var settings = new HumanoidSettings();
        settings.SetMuscleRotationDegRange(EHumanoidValue.LeftUpperLegFrontBack, new Vector2(-12.0f, 34.0f));
        settings.SetMuscleScaleRange(EHumanoidValue.LeftUpperLegFrontBack, new Vector2(-2.0f, 5.0f));

        settings.NegateAllRanges();

        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.JawClose).ShouldBe(new Vector2(-10.0f, 10.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftUpperLegFrontBack).ShouldBe(new Vector2(-34.0f, 12.0f));
        settings.MuscleScaleRanges[EHumanoidValue.LeftUpperLegFrontBack].ShouldBe(new Vector2(-5.0f, 2.0f));
    }
}
