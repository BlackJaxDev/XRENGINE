using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Occlusion;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class CpuOcclusionParityValidatorTests
{
    [Test]
    public void Validate_AcceptsImageAndSentinelParityWithOwningViewAndBothEyeProofs()
    {
        CpuOcclusionParitySample[] samples = CreateValidCohort();

        CpuOcclusionParityValidationResult result = CpuOcclusionParityValidator.Validate(
            samples,
            finalImageParity: true);

        result.IsValid.ShouldBeTrue();
        result.DisabledCandidateCount.ShouldBe(4);
        result.EnabledCandidateCount.ShouldBe(2);
        result.RemovedCandidateCount.ShouldBe(2);
        result.DesktopProvenCullCount.ShouldBe(1);
        result.SinglePassStereoProvenCullCount.ShouldBe(1);
        result.KnownVisibleSentinelCount.ShouldBe(2);
        result.RejectedSentinelCount.ShouldBe(0);
        result.EnabledOutsideDisabledCount.ShouldBe(0);
        result.RemovedWithoutProofCount.ShouldBe(0);
    }

    [Test]
    public void Validate_RejectsEnabledCandidatesOutsideDisabledGroundTruth()
    {
        CpuOcclusionParitySample[] samples = CreateValidCohort();
        samples[0] = samples[0] with { InDisabledCandidateSet = false };

        CpuOcclusionParityValidationResult result = CpuOcclusionParityValidator.Validate(samples, finalImageParity: true);

        result.IsValid.ShouldBeFalse();
        result.EnabledOutsideDisabledCount.ShouldBe(1);
        result.RejectedSentinelCount.ShouldBe(1);
    }

    [Test]
    public void Validate_RejectsKnownVisibleSentinelRemoval()
    {
        CpuOcclusionParitySample[] samples = CreateValidCohort();
        samples[0] = samples[0] with { InEnabledCandidateSet = false };

        CpuOcclusionParityValidationResult result = CpuOcclusionParityValidator.Validate(samples, finalImageParity: true);

        result.IsValid.ShouldBeFalse();
        result.RejectedSentinelCount.ShouldBe(1);
    }

    [Test]
    public void Validate_RejectsCohortWithoutKnownVisibleSentinels()
    {
        CpuOcclusionParitySample[] samples = CreateValidCohort();
        samples[0] = samples[0] with { IsKnownVisibleSentinel = false };
        samples[2] = samples[2] with { IsKnownVisibleSentinel = false };

        CpuOcclusionParityValidationResult result = CpuOcclusionParityValidator.Validate(samples, finalImageParity: true);

        result.IsValid.ShouldBeFalse();
        result.KnownVisibleSentinelCount.ShouldBe(0);
    }

    [Test]
    public void Validate_RejectsDesktopRemovalWithoutOwningViewProof()
    {
        CpuOcclusionParitySample[] samples = CreateValidCohort();
        samples[1] = samples[1] with { OcclusionProofCoverageMask = 0u };

        CpuOcclusionParityValidationResult result = CpuOcclusionParityValidator.Validate(samples, finalImageParity: true);

        result.IsValid.ShouldBeFalse();
        result.RemovedWithoutProofCount.ShouldBe(1);
        result.DesktopProvenCullCount.ShouldBe(0);
    }

    [Test]
    public void Validate_RejectsSinglePassStereoRemovalWithoutBothEyeProof()
    {
        CpuOcclusionParitySample[] samples = CreateValidCohort();
        samples[3] = samples[3] with { OcclusionProofCoverageMask = 0x1u };

        CpuOcclusionParityValidationResult result = CpuOcclusionParityValidator.Validate(samples, finalImageParity: true);

        result.IsValid.ShouldBeFalse();
        result.RemovedWithoutProofCount.ShouldBe(1);
        result.SinglePassStereoProvenCullCount.ShouldBe(0);
    }

    [Test]
    public void Validate_RejectsFinalImageMismatch()
    {
        CpuOcclusionParityValidationResult result = CpuOcclusionParityValidator.Validate(
            CreateValidCohort(),
            finalImageParity: false);

        result.IsValid.ShouldBeFalse();
        result.FinalImageParity.ShouldBeFalse();
    }

    private static CpuOcclusionParitySample[] CreateValidCohort()
    {
        var desktopKey = new OcclusionViewKey(
            renderPass: 0,
            scope: EOcclusionViewScope.MonoDesktop,
            pipelineInstanceId: 501,
            povId: 501,
            coverageMask: 0x1u,
            requiredCoverageMask: 0x1u,
            outputId: 0xC001UL);
        var singlePassStereoKey = new OcclusionViewKey(
            renderPass: 0,
            scope: EOcclusionViewScope.VrSinglePassStereo,
            pipelineInstanceId: 502,
            povId: -502,
            coverageMask: 0x3u,
            requiredCoverageMask: 0x3u,
            declaredViewCount: 2,
            outputId: 0xC002UL);

        return
        [
            new(desktopKey, 1u, true, true, true, 0u),
            new(desktopKey, 2u, true, false, false, 0x1u),
            new(singlePassStereoKey, 1u, true, true, true, 0u),
            new(singlePassStereoKey, 2u, true, false, false, 0x3u),
        ];
    }
}
