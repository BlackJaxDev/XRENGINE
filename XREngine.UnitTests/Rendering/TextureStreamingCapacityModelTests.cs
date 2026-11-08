using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Checks the CPU capacity model of texture streaming and the resizable decode gate.
/// </summary>
[TestFixture]
public sealed class TextureStreamingCapacityModelTests
{
    // Foreground threads follow EngineExecutionTopology: min(4, processors - 1). The desktop reserves 2 dedicated threads
    // in a headless host and 12 with the Vulkan and OpenXR lanes; the base never goes below the old fixed width of 2.
    [TestCase(4, 3, 2, 2)]
    [TestCase(6, 4, 2, 2)]
    [TestCase(8, 4, 2, 2)]
    [TestCase(16, 4, 2, 8)]
    [TestCase(16, 4, 12, 2)]
    [TestCase(12, 4, 2, 6)]
    [TestCase(32, 4, 12, 8)]
    [TestCase(2, 1, 0, 2)]
    public void BaseDecodeWidth_UsesTheTopologyReservation_AndNeverGoesBelowTheOldWidth(int processors, int foreground, int dedicated, int expected)
        => TextureStreamingCapacityModel.ResolveBaseDecodeWidth(processors, foreground, dedicated, null).ShouldBe(expected);

    [TestCase(4, 1, 1)]
    [TestCase(16, 3, 3)]
    [TestCase(4, 100, TextureStreamingCapacityModel.MaxDecodeConcurrencyOverride)]
    [TestCase(4, 0, 1)]
    public void BaseDecodeWidth_ExplicitOverride_PinsTheValue(int processors, int pinned, int expected)
        => TextureStreamingCapacityModel.ResolveBaseDecodeWidth(processors, 4, 2, pinned).ShouldBe(expected);

    [TestCase(1, 8, 1)]
    [TestCase(3, 8, 3)]
    [TestCase(8, 8, 8)]
    [TestCase(8, 4, 4)]
    public void UploadPreparationWorkers_FollowTheDecodeWidth_AndTheStagingBuffers(int decode, int staging, int expected)
        => TextureStreamingCapacityModel.ResolveUploadPreparationWorkers(decode, staging).ShouldBe(expected);

    [Test]
    public void DecodeGate_ExtraRelease_IsCountedAndGrantsNothing()
    {
        PriorityAsyncSemaphore gate = new(1);
        gate.Release();

        gate.ExcessReleaseCount.ShouldBe(1);
        gate.InUse.ShouldBe(0);
        gate.WaitAsync(JobPriority.Normal, CancellationToken.None).IsCompletedSuccessfully.ShouldBeTrue();
        gate.WaitAsync(JobPriority.Normal, CancellationToken.None).IsCompleted.ShouldBeFalse();
        gate.WaitingCount.ShouldBe(1);
    }

    [Test]
    public async Task DecodeGate_LargerCapacity_GrantsWaitingRequests()
    {
        PriorityAsyncSemaphore gate = new(1);
        (await gate.WaitAsync(JobPriority.Normal, CancellationToken.None)).ShouldBeTrue();
        ValueTask<bool> second = gate.WaitAsync(JobPriority.Normal, CancellationToken.None);
        ValueTask<bool> third = gate.WaitAsync(JobPriority.High, CancellationToken.None);
        second.IsCompleted.ShouldBeFalse();
        third.IsCompleted.ShouldBeFalse();

        gate.SetCapacity(3);

        (await second).ShouldBeTrue();
        (await third).ShouldBeTrue();
        gate.InUse.ShouldBe(3);
    }

    [Test]
    public async Task DecodeGate_SmallerCapacity_TakesEffectAsPermitsRelease()
    {
        PriorityAsyncSemaphore gate = new(3);
        for (int i = 0; i < 3; i++)
            (await gate.WaitAsync(JobPriority.Normal, CancellationToken.None)).ShouldBeTrue();

        gate.SetCapacity(1);
        ValueTask<bool> waiting = gate.WaitAsync(JobPriority.Normal, CancellationToken.None);

        gate.Release();
        gate.Release();
        waiting.IsCompleted.ShouldBeFalse();
        gate.InUse.ShouldBe(1);

        gate.Release();
        (await waiting).ShouldBeTrue();
        gate.InUse.ShouldBe(1);
    }
}
