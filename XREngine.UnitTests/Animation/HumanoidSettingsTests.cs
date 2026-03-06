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
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.JawClose).ShouldBe(new Vector2(-5.0f, 25.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftUpperLegInOut).ShouldBe(new Vector2(-60.0f, 60.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightUpperLegInOut).ShouldBe(new Vector2(-60.0f, 60.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftHandThumb2Stretched).ShouldBe(new Vector2(-40.0f, 35.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.RightHandThumb2Stretched).ShouldBe(new Vector2(-40.0f, 35.0f));
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

        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.JawClose).ShouldBe(new Vector2(-25.0f, 5.0f));
        settings.GetResolvedMuscleRotationDegRange(EHumanoidValue.LeftUpperLegFrontBack).ShouldBe(new Vector2(-34.0f, 12.0f));
        settings.MuscleScaleRanges[EHumanoidValue.LeftUpperLegFrontBack].ShouldBe(new Vector2(-5.0f, 2.0f));
    }
}
