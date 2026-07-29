using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AdvancedAnimationPreparationContractTests
{
    [Test]
    public void CompletedFeedbackIsConsumedWithoutWaitingForProducingSlot()
    {
        AdvancedVisibilityFeedbackRing ring = new(
            slotCount: 3,
            recordCapacity: 4);
        AdvancedGpuHandle entity = new(7u, 3u);
        ring.GetGpuWritableMirror(5UL)[0] =
            new AdvancedAnimationVisibilityFeedback(
                entity,
                LastVisibleFrame: 5UL,
                ProjectedDiameter: 0.5f,
                DistanceOverRadius: 2.0f,
                ViewMask: 0b11UL,
                EAdvancedAnimationVisibilityFlags.Visible |
                EAdvancedAnimationVisibilityFlags.ShadowRelevant);
        ring.SealGpuWrite(
            frameId: 5UL,
            recordCount: 1,
            completionValue: 20UL);

        ring.TryGetLatestCompleted(
                maximumFrameId: 5UL,
                completedValue: 19UL,
                out _,
                out _)
            .ShouldBeFalse();
        ring.TryGetLatestCompleted(
                maximumFrameId: 5UL,
                completedValue: 20UL,
                out ReadOnlySpan<AdvancedAnimationVisibilityFeedback> completed,
                out ulong frame)
            .ShouldBeTrue();
        frame.ShouldBe(5UL);
        completed.Length.ShouldBe(1);
        completed[0].Entity.ShouldBe(entity);
        completed[0].ViewMask.ShouldBe(0b11UL);
    }

    [Test]
    public void CadenceIsDeterministicPreservesDeltaAndKeepsGameplayExplicit()
    {
        AdvancedAnimationScheduler first = new(capacity: 8);
        AdvancedAnimationScheduler second = new(capacity: 8);
        AdvancedAnimationScheduleProfile profile = new(
            FullRateProjectedDiameter: 2.0f,
            MediumRateProjectedDiameter: 1.0f,
            LowRateProjectedDiameter: 0.5f,
            FullRateInterval: 4u,
            MediumRateInterval: 4u,
            LowRateInterval: 4u,
            OffscreenInterval: 4u,
            VisibilityGraceFrames: 2u,
            MaximumStalePoseFrames: 20u);
        AdvancedBoneLodTier[] tiers =
        [
            new(
                128u,
                EAdvancedAnimationBoneRequirement.RuntimeRequired |
                EAdvancedAnimationBoneRequirement.IkTarget |
                EAdvancedAnimationBoneRequirement.Attachment |
                EAdvancedAnimationBoneRequirement.PhysicsChain),
            new(
                32u,
                EAdvancedAnimationBoneRequirement.RuntimeRequired),
        ];
        AdvancedAnimationVisibilityFeedback feedback = new(
            new AdvancedGpuHandle(11u, 2u),
            LastVisibleFrame: 100UL,
            ProjectedDiameter: 0.25f,
            DistanceOverRadius: 4.0f,
            ViewMask: 1UL,
            EAdvancedAnimationVisibilityFlags.Visible |
            EAdvancedAnimationVisibilityFlags.HistoryValid);

        float accumulatedBeforeUpdate = 0.0f;
        bool observedCadenceSkip = false;
        bool observedUpdate = false;
        for (ulong frame = 100UL; frame < 108UL; frame++)
        {
            AdvancedAnimationScheduleDecision a = first.Schedule(
                feedback with { LastVisibleFrame = frame },
                profile,
                tiers,
                EAdvancedAnimationBoneRequirement.RuntimeRequired |
                EAdvancedAnimationBoneRequirement.IkTarget,
                runtimeRequiredBoneCount: 32u,
                requestedBoneTier: 1u,
                frame,
                deltaSeconds: 0.125f,
                gameplayCpuAnimationRequired: true);
            AdvancedAnimationScheduleDecision b = second.Schedule(
                feedback with { LastVisibleFrame = frame },
                profile,
                tiers,
                EAdvancedAnimationBoneRequirement.RuntimeRequired |
                EAdvancedAnimationBoneRequirement.IkTarget,
                runtimeRequiredBoneCount: 32u,
                requestedBoneTier: 1u,
                frame,
                deltaSeconds: 0.125f,
                gameplayCpuAnimationRequired: true);

            a.ShouldBe(b);
            a.CadenceFrames.ShouldBe(4u);
            a.BoneTier.ShouldBe(0u);
            a.GameplayCpuAnimationRequired.ShouldBeTrue();
            if (!a.UpdateRenderPose)
            {
                observedCadenceSkip = true;
                a.SkipReason.ShouldBe(EAdvancedAnimationSkipReason.Cadence);
                accumulatedBeforeUpdate =
                    Math.Max(accumulatedBeforeUpdate, a.AccumulatedDeltaSeconds);
            }
            else
            {
                observedUpdate = true;
                a.AccumulatedDeltaSeconds
                    .ShouldBeGreaterThanOrEqualTo(accumulatedBeforeUpdate);
            }
        }

        observedCadenceSkip.ShouldBeTrue();
        observedUpdate.ShouldBeTrue();
    }

    [Test]
    public void GraceAndNewVisibilityAvoidPoseThrash()
    {
        AdvancedAnimationScheduler scheduler = new(capacity: 4);
        AdvancedAnimationScheduleProfile profile =
            AdvancedAnimationScheduleProfile.Default with
            {
                FullRateInterval = 8u,
                MediumRateInterval = 8u,
                LowRateInterval = 8u,
                OffscreenInterval = 8u,
                VisibilityGraceFrames = 3u,
            };
        AdvancedBoneLodTier[] tiers =
        [
            new(
                64u,
                EAdvancedAnimationBoneRequirement.RuntimeRequired),
        ];
        AdvancedGpuHandle entity = new(3u, 1u);

        AdvancedAnimationScheduleDecision newlyVisible = scheduler.Schedule(
            new AdvancedAnimationVisibilityFeedback(
                entity,
                LastVisibleFrame: 40UL,
                ProjectedDiameter: 0.01f,
                DistanceOverRadius: 100.0f,
                ViewMask: 1UL,
                EAdvancedAnimationVisibilityFlags.Visible |
                EAdvancedAnimationVisibilityFlags.NewlyVisible),
            profile,
            tiers,
            EAdvancedAnimationBoneRequirement.RuntimeRequired,
            runtimeRequiredBoneCount: 1u,
            requestedBoneTier: 0u,
            frameId: 40UL,
            deltaSeconds: 0.016f,
            gameplayCpuAnimationRequired: false);
        newlyVisible.UpdateRenderPose.ShouldBeTrue();

        AdvancedAnimationScheduleDecision outsideGrace = scheduler.Schedule(
            new AdvancedAnimationVisibilityFeedback(
                entity,
                LastVisibleFrame: 40UL,
                ProjectedDiameter: 0.01f,
                DistanceOverRadius: 100.0f,
                ViewMask: 0UL,
                EAdvancedAnimationVisibilityFlags.HistoryValid),
            profile,
            tiers,
            EAdvancedAnimationBoneRequirement.RuntimeRequired,
            runtimeRequiredBoneCount: 1u,
            requestedBoneTier: 0u,
            frameId: 50UL,
            deltaSeconds: 0.16f,
            gameplayCpuAnimationRequired: true);
        outsideGrace.UpdateRenderPose.ShouldBeFalse();
        outsideGrace.GameplayCpuAnimationRequired.ShouldBeTrue();
        outsideGrace.SkipReason.ShouldBe(
            EAdvancedAnimationSkipReason.OutsideVisibilityGrace);
        outsideGrace.StalePoseAge.ShouldBe(10u);
    }
}
