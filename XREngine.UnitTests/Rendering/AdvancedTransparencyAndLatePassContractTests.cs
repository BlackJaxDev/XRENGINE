using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedTransparencyAndLatePassContractTests
{
    [Test]
    public void LatePassMetadata_InitializesCorrectly()
    {
        AdvancedLatePassMetadata metadata = new(
            EAdvancedLatePassKind.Refraction,
            requiresSceneColorSnapshot: true,
            participatesInMotionVectors: true,
            writesDepth: false,
            isOrderDependent: true);

        metadata.Kind.ShouldBe(EAdvancedLatePassKind.Refraction);
        metadata.RequiresSceneColorSnapshot.ShouldBeTrue();
        metadata.ParticipatesInMotionVectors.ShouldBeTrue();
        metadata.WritesDepth.ShouldBeFalse();
        metadata.IsOrderDependent.ShouldBeTrue();
        metadata.UnsupportedReason.ShouldBeNull();
    }

    [Test]
    public void LatePassValidator_RejectsOpaqueAndMaskedDraws()
    {
        AdvancedLatePassMetadata metadata = new(EAdvancedLatePassKind.SortedAlpha);

        bool opaqueValid = AdvancedLatePassEligibilityValidator.TryValidateLatePass(metadata, isOpaqueOrMasked: true, out string? rejectionReason);
        opaqueValid.ShouldBeFalse();
        rejectionReason.ShouldNotBeNull();
        rejectionReason.ShouldContain("Opaque and masked surfaces must be classified and shaded natively");

        bool transparentValid = AdvancedLatePassEligibilityValidator.TryValidateLatePass(metadata, isOpaqueOrMasked: false, out rejectionReason);
        transparentValid.ShouldBeTrue();
        rejectionReason.ShouldBeNull();
    }

    [Test]
    public void LatePassValidator_PropagatesUnsupportedReason()
    {
        AdvancedLatePassMetadata metadata = new(
            EAdvancedLatePassKind.VolumetricFog,
            unsupportedReason: "Volumetric fog is disabled by project quality settings.");

        bool valid = AdvancedLatePassEligibilityValidator.TryValidateLatePass(metadata, isOpaqueOrMasked: false, out string? reason);
        valid.ShouldBeFalse();
        reason.ShouldBe("Volumetric fog is disabled by project quality settings.");
    }

    [Test]
    public void SceneColorContract_RequiresSnapshotAccurately()
    {
        AdvancedSceneColorContract.RequiresSnapshot(refractiveDrawCount: 0u, hasFeedbackPass: false).ShouldBeFalse();
        AdvancedSceneColorContract.RequiresSnapshot(refractiveDrawCount: 3u, hasFeedbackPass: false).ShouldBeTrue();
        AdvancedSceneColorContract.RequiresSnapshot(refractiveDrawCount: 0u, hasFeedbackPass: true).ShouldBeTrue();
        AdvancedSceneColorContract.RequiresSnapshot(refractiveDrawCount: 2u, hasFeedbackPass: true).ShouldBeTrue();
    }

    [Test]
    public void TemporalResetFlags_BitwiseAndHistoryValidation()
    {
        AdvancedTemporalResetFlags flags = AdvancedTemporalResetFlags.CameraCut | AdvancedTemporalResetFlags.Resize;
        flags.HasFlag(AdvancedTemporalResetFlags.CameraCut).ShouldBeTrue();
        flags.HasFlag(AdvancedTemporalResetFlags.Resize).ShouldBeTrue();
        flags.HasFlag(AdvancedTemporalResetFlags.PipelineSwitch).ShouldBeFalse();

        // History validity requires flags == None and frameIndex > 0
        AdvancedTemporalHistoryContract.IsHistoryValid(AdvancedTemporalResetFlags.None, frameIndex: 1u).ShouldBeTrue();
        AdvancedTemporalHistoryContract.IsHistoryValid(AdvancedTemporalResetFlags.None, frameIndex: 0u).ShouldBeFalse();
        AdvancedTemporalHistoryContract.IsHistoryValid(flags, frameIndex: 5u).ShouldBeFalse();
    }

    [Test]
    public void SpecialEffectDescriptor_StoresLaneProperties()
    {
        AdvancedSpecialEffectDescriptor water = new(
            EAdvancedSpecialEffectLane.Water,
            isSupported: true,
            requiresDepthPrePass: true,
            displacesGeometry: false);

        water.Lane.ShouldBe(EAdvancedSpecialEffectLane.Water);
        water.IsSupported.ShouldBeTrue();
        water.RequiresDepthPrePass.ShouldBeTrue();
        water.DisplacesGeometry.ShouldBeFalse();
    }

    [Test]
    public void ResourceNames_ProduceConsistentIdentifiers()
    {
        AdvancedSceneColorContract.SceneColorSnapshotResourceName.ShouldBe("AdvancedShading.SceneColorSnapshot");
        AdvancedTemporalHistoryContract.ReactiveMaskResourceName.ShouldBe("AdvancedShading.ReactiveMask");
    }
}
