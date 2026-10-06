using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanImageViewOwnershipTests
{
    [Test]
    public void ExactImageViewRetirement_RejectsStaleReceiptsAndReusesMatchingTicket()
    {
        VulkanResourceRuntime resources = new(1);
        VulkanResourceLifetimeKey key = new(ObjectType.ImageView, 0xA11CE);
        VulkanResourceLifetimeTracker tracker = resources.Lifetime.Tracker;

        resources.CaptureImageViewRetirementTicket(key, "missing view", 1)
            .ShouldBe(VulkanRetirementTicket.None);
        tracker.ResourceLifetimes.ContainsKey(key).ShouldBeFalse();

        resources.RegisterResource(ObjectType.ImageView, key.Handle, "first view");
        VulkanResourceLifetimeRecord first = tracker.ResourceLifetimes[key];
        ulong firstGeneration = first.Generation;
        resources.CaptureImageViewRetirementTicket(key, "zero receipt", 0)
            .ShouldBe(VulkanRetirementTicket.None);
        first.PublishedGeneration.ShouldBe(firstGeneration);
        first.State.ShouldBe(EVulkanResourceLifetimeState.CpuOwned);
        first.RetirementSerial.ShouldBe(0UL);

        resources.CompleteResourceDestruction(ObjectType.ImageView, key.Handle, forced: true);
        first.State.ShouldBe(EVulkanResourceLifetimeState.Destroyed);
        resources.CaptureImageViewRetirementTicket(key, "destroyed receipt", firstGeneration)
            .ShouldBe(VulkanRetirementTicket.None);
        first.State.ShouldBe(EVulkanResourceLifetimeState.Destroyed);
        first.RetirementSerial.ShouldBe(0UL);

        resources.RegisterResource(ObjectType.ImageView, key.Handle, "replacement view");
        VulkanResourceLifetimeRecord current = tracker.ResourceLifetimes[key];
        ulong currentGeneration = current.Generation;
        currentGeneration.ShouldNotBe(firstGeneration);
        string ownerBeforeStaleCall = current.Owner;
        ulong publishedBeforeStaleCall = current.PublishedGeneration;
        EVulkanResourceLifetimeState stateBeforeStaleCall = current.State;
        ulong serialBeforeStaleCall = current.RetirementSerial;

        resources.CaptureImageViewRetirementTicket(key, "stale wrapper", firstGeneration)
            .ShouldBe(VulkanRetirementTicket.None);
        current.PublishedGeneration.ShouldBe(publishedBeforeStaleCall);
        current.State.ShouldBe(stateBeforeStaleCall);
        current.Owner.ShouldBe(ownerBeforeStaleCall);
        current.RetirementSerial.ShouldBe(serialBeforeStaleCall);
        resources.Lifetime.TryDequeueSupersededResourceDescriptorOwner(out _).ShouldBeFalse();

        VulkanRetirementTicket accepted = resources.CaptureImageViewRetirementTicket(
            key, "current wrapper", currentGeneration);
        accepted.ResourceGeneration.ShouldBe(currentGeneration);
        current.PublishedGeneration.ShouldBe(0UL);
        current.State.HasFlag(EVulkanResourceLifetimeState.PendingRetirement).ShouldBeTrue();
        ulong acceptedSerial = current.RetirementSerial;
        acceptedSerial.ShouldBeGreaterThan(0UL);

        resources.CaptureImageViewRetirementTicket(key, "same wrapper", currentGeneration)
            .ShouldBe(accepted);
        current.PublishedGeneration.ShouldBe(0UL);
        current.RetirementSerial.ShouldBe(acceptedSerial);
        tracker.ResourceLifetimes[key].ShouldBeSameAs(current);
        resources.Lifetime.TryDequeueSupersededResourceDescriptorOwner(out VulkanSupersededResourceDescriptorOwner queuedOwner)
            .ShouldBeTrue();
        queuedOwner.ResourceKey.ShouldBe(key);
        queuedOwner.Generation.ShouldBe(currentGeneration);
        resources.Lifetime.TryDequeueSupersededResourceDescriptorOwner(out _).ShouldBeFalse();
    }

    [Test]
    public void QualifiedImageViewRetirement_QueuesOnlyMatchingReceiptsOnce()
    {
        VulkanResourceRuntime resources = new(1);
        VulkanResourceLifetimeTracker tracker = resources.Lifetime.Tracker;
        const ulong primaryHandle = 0xB100;
        const ulong recycledHandle = 0xB200;
        const ulong attachmentHandle = 0xB300;

        resources.RegisterResource(ObjectType.ImageView, primaryHandle, "primary view");
        resources.RegisterResource(ObjectType.ImageView, recycledHandle, "old attachment view");
        resources.RegisterResource(ObjectType.ImageView, attachmentHandle, "current attachment view");
        ulong primaryGeneration = tracker.ResourceLifetimes[new(ObjectType.ImageView, primaryHandle)].Generation;
        ulong oldGeneration = tracker.ResourceLifetimes[new(ObjectType.ImageView, recycledHandle)].Generation;
        ulong attachmentGeneration = tracker.ResourceLifetimes[new(ObjectType.ImageView, attachmentHandle)].Generation;
        resources.CompleteResourceDestruction(ObjectType.ImageView, recycledHandle, forced: true);
        resources.RegisterResource(ObjectType.ImageView, recycledHandle, "replacement attachment view");
        VulkanResourceLifetimeRecord replacement = tracker.ResourceLifetimes[new(ObjectType.ImageView, recycledHandle)];
        ulong replacementGeneration = replacement.Generation;

        RetiredImageResources staleOnly = new(
            default, default, new ImageView(recycledHandle), [], default, 0,
            oldGeneration, [], true);
        resources.Images.RetireOwnedResources(staleOnly, "stale wrapper");
        resources.Lifetime.Retirement.Images[0].Count.ShouldBe(0);
        replacement.PublishedGeneration.ShouldBe(replacementGeneration);
        replacement.State.ShouldBe(EVulkanResourceLifetimeState.CpuOwned);
        replacement.RetirementSerial.ShouldBe(0UL);

        RetiredImageResources mixed = new(
            default,
            default,
            new ImageView(primaryHandle),
            [new ImageView(recycledHandle), new ImageView(attachmentHandle), new ImageView(recycledHandle)],
            default,
            0,
            primaryGeneration,
            [oldGeneration, attachmentGeneration, 0],
            true);
        resources.Images.RetireOwnedResources(mixed, "current wrapper");
        resources.Lifetime.Retirement.Images[0].Count.ShouldBe(1);
        RetiredImageResourceEntry queued = resources.Lifetime.Retirement.Images[0][0];
        queued.PrimaryViewGeneration.ShouldBe(primaryGeneration);
        queued.Resources.PrimaryView.Handle.ShouldBe(primaryHandle);
        queued.AttachmentViewGenerations.Length.ShouldBe(1);
        queued.AttachmentViewGenerations[0].ShouldBe(attachmentGeneration);
        queued.Resources.AttachmentViews.Length.ShouldBe(1);
        queued.Resources.AttachmentViews[0].Handle.ShouldBe(attachmentHandle);
        replacement.PublishedGeneration.ShouldBe(replacementGeneration);
        replacement.State.ShouldBe(EVulkanResourceLifetimeState.CpuOwned);
        replacement.RetirementSerial.ShouldBe(0UL);

        resources.Images.RetireOwnedResources(mixed, "repeat wrapper");
        resources.Lifetime.Retirement.Images[0].Count.ShouldBe(1);
        replacement.PublishedGeneration.ShouldBe(replacementGeneration);
        replacement.RetirementSerial.ShouldBe(0UL);
    }
}
