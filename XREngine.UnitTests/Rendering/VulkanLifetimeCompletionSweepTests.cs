using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Checks the non-blocking completion sweep of the Vulkan resource lifetime tracker: a submission
/// whose native work is complete but whose owner has not reported it yet advances the completion
/// frontier, and a pending submission does not.
/// </summary>
[TestFixture]
public sealed class VulkanLifetimeCompletionSweepTests
{
    private const ulong Queue = 0x10UL;

    [Test]
    public void CompletedUnobservedSubmission_AdvancesTheGraphicsFrontier()
    {
        VulkanResourceLifetimeTracker tracker = CreateTracker(submissionCount: 3);
        tracker.CompletedGraphicsSequence.ShouldBe(0UL);

        // Sequences 1 and 2 finished on the GPU; nobody reported them.
        FakeStatus status = new(completedThroughSequence: 2UL);
        tracker.SweepCompletions(ref status).ShouldBe(2);

        tracker.CompletedGraphicsSequence.ShouldBe(2UL);
    }

    [Test]
    public void PendingSubmission_DoesNotAdvanceTheFrontier()
    {
        VulkanResourceLifetimeTracker tracker = CreateTracker(submissionCount: 2);

        FakeStatus status = new(completedThroughSequence: 0UL);
        tracker.SweepCompletions(ref status).ShouldBe(0);

        tracker.CompletedGraphicsSequence.ShouldBe(0UL);
    }

    [Test]
    public void OldestPendingSubmission_HoldsTheFrontierBelowItself()
    {
        VulkanResourceLifetimeTracker tracker = CreateTracker(submissionCount: 3);

        // Only sequence 3 reports complete. On one queue a later signal covers earlier work, so
        // the tracker marks 1 and 2 as well and the frontier reaches 3.
        FakeStatus status = new(completedOnly: 3UL);
        tracker.SweepCompletions(ref status).ShouldBe(1);

        tracker.CompletedGraphicsSequence.ShouldBe(3UL);
    }

    [Test]
    public void DeviceLost_SweepsNothing()
    {
        VulkanResourceLifetimeTracker tracker = CreateTracker(submissionCount: 1);
        tracker.DeviceLost = true;

        FakeStatus status = new(completedThroughSequence: 1UL);
        tracker.SweepCompletions(ref status).ShouldBe(0);
    }

    private static VulkanResourceLifetimeTracker CreateTracker(int submissionCount)
    {
        VulkanResourceLifetimeTracker tracker = new();
        for (int index = 0; index < submissionCount; index++)
        {
            ulong sequence = ++tracker.LastGraphicsSequence;
            tracker.LifetimeSubmissions.Add(new VulkanLifetimeSubmission(
                Queue,
                EVulkanLifetimeQueueDomain.Graphics,
                sequence,
                TimelineSemaphoreHandle: 0UL,
                TimelineValue: 0UL,
                FenceHandle: 0x100UL + sequence));
        }
        return tracker;
    }

    private readonly struct FakeStatus : IVulkanLifetimeSubmissionStatus
    {
        private readonly ulong _completedThrough;
        private readonly ulong _completedOnly;

        public FakeStatus(ulong completedThroughSequence)
        {
            _completedThrough = completedThroughSequence;
            _completedOnly = 0UL;
        }

        public FakeStatus(ulong completedOnly, bool _ = false)
        {
            _completedThrough = 0UL;
            _completedOnly = completedOnly;
        }

        public bool IsComplete(in VulkanLifetimeSubmission submission)
            => submission.QueueSequence <= _completedThrough ||
               (_completedOnly != 0UL && submission.QueueSequence == _completedOnly);
    }
}
