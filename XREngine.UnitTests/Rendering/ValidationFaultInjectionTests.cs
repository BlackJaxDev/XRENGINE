using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Development-only fault injection must consume exactly the armed count, stay bounded, and be
/// inert once disarmed. Both injectors are process-wide, so these tests run serially and disarm
/// around every test; injected totals are asserted as deltas.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ValidationFaultInjectionTests
{
    [SetUp]
    [TearDown]
    public void Disarm()
    {
        AdvancedPublicationFaultInjection.ArmPreflightRejections(0);
        VulkanTextureUploadFaultInjection.Arm(0, 0);
    }

    [Test]
    public void PublicationRejections_ConsumeExactlyTheArmedCount()
    {
        long injectedBefore = AdvancedPublicationFaultInjection.InjectedPreflightRejections;
        AdvancedPublicationFaultInjection.ArmPreflightRejections(2);

        AdvancedPublicationFaultInjection.TryConsumePreflightRejection().ShouldBeTrue();
        AdvancedPublicationFaultInjection.TryConsumePreflightRejection().ShouldBeTrue();
        AdvancedPublicationFaultInjection.TryConsumePreflightRejection().ShouldBeFalse();

        AdvancedPublicationFaultInjection.ArmedPreflightRejections.ShouldBe(0);
        (AdvancedPublicationFaultInjection.InjectedPreflightRejections - injectedBefore).ShouldBe(2);
    }

    [Test]
    public void PublicationRejections_ClampToTheMaximumAndDisarmWithZero()
    {
        AdvancedPublicationFaultInjection.ArmPreflightRejections(int.MaxValue);
        AdvancedPublicationFaultInjection.ArmedPreflightRejections
            .ShouldBe(AdvancedPublicationFaultInjection.MaximumArmedRejections);

        AdvancedPublicationFaultInjection.ArmPreflightRejections(0);
        AdvancedPublicationFaultInjection.TryConsumePreflightRejection().ShouldBeFalse();
    }

    [Test]
    public void TextureUploadFaults_ConsumeAdmissionFailuresAndCancellationsIndependently()
    {
        long failuresBefore = VulkanTextureUploadFaultInjection.InjectedAdmissionFailures;
        long cancellationsBefore = VulkanTextureUploadFaultInjection.InjectedCancellations;
        VulkanTextureUploadFaultInjection.Arm(admissionFailures: 1, cancellations: 2);

        VulkanTextureUploadFaultInjection.TryConsumeAdmissionFailure().ShouldBeTrue();
        VulkanTextureUploadFaultInjection.TryConsumeAdmissionFailure().ShouldBeFalse();
        VulkanTextureUploadFaultInjection.TryConsumeCancellation().ShouldBeTrue();
        VulkanTextureUploadFaultInjection.TryConsumeCancellation().ShouldBeTrue();
        VulkanTextureUploadFaultInjection.TryConsumeCancellation().ShouldBeFalse();

        (VulkanTextureUploadFaultInjection.InjectedAdmissionFailures - failuresBefore).ShouldBe(1);
        (VulkanTextureUploadFaultInjection.InjectedCancellations - cancellationsBefore).ShouldBe(2);
    }

    [Test]
    public void TextureUploadFaults_NegativeCountsDisarm()
    {
        VulkanTextureUploadFaultInjection.Arm(admissionFailures: -5, cancellations: -1);

        VulkanTextureUploadFaultInjection.ArmedAdmissionFailures.ShouldBe(0);
        VulkanTextureUploadFaultInjection.ArmedCancellations.ShouldBe(0);
        VulkanTextureUploadFaultInjection.TryConsumeAdmissionFailure().ShouldBeFalse();
        VulkanTextureUploadFaultInjection.TryConsumeCancellation().ShouldBeFalse();
    }

    [Test]
    public void TextureUploadFaults_ConcurrentConsumersNeverExceedTheArmedCount()
    {
        const int Armed = 64;
        long failuresBefore = VulkanTextureUploadFaultInjection.InjectedAdmissionFailures;
        VulkanTextureUploadFaultInjection.Arm(admissionFailures: Armed, cancellations: 0);
        int consumed = 0;

        Parallel.For(0, 1024, _ =>
        {
            if (VulkanTextureUploadFaultInjection.TryConsumeAdmissionFailure())
                Interlocked.Increment(ref consumed);
        });

        consumed.ShouldBe(Armed);
        (VulkanTextureUploadFaultInjection.InjectedAdmissionFailures - failuresBefore).ShouldBe(Armed);
    }
}
