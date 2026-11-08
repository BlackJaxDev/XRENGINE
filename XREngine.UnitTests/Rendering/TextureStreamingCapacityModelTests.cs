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
    [TestCase(1, 0, 1)]
    [TestCase(2, 1, 1)]
    [TestCase(4, 2, 1)]
    [TestCase(6, 4, 1)]
    [TestCase(8, 4, 3)]
    [TestCase(12, 8, 7)]
    [TestCase(16, 16, 8)]
    [TestCase(32, 16, 8)]
    [TestCase(64, 16, 8)]
    public void DecodeConcurrency_LeavesTheFrameThreads_AndStaysInTheSafeRange(int processors, int jobWorkers, int expected)
        => TextureStreamingCapacityModel.ResolveDecodeConcurrency(processors, jobWorkers, null).ShouldBe(expected);

    [Test]
    public void DecodeConcurrency_IsNotMoreThanTheJobWorkerCount()
        => TextureStreamingCapacityModel.ResolveDecodeConcurrency(16, 3, null).ShouldBe(3);

    [Test]
    public void DecodeConcurrency_UnknownJobWorkerCount_UsesTheProcessorsOnly()
        => TextureStreamingCapacityModel.ResolveDecodeConcurrency(10, 0, null).ShouldBe(5);

    [TestCase(2, 3, 3)]
    [TestCase(32, 1, 1)]
    [TestCase(4, 100, TextureStreamingCapacityModel.MaxDecodeConcurrencyOverride)]
    [TestCase(4, 0, 1)]
    public void DecodeConcurrency_ExplicitOverride_PinsTheValue(int processors, int pinned, int expected)
        => TextureStreamingCapacityModel.ResolveDecodeConcurrency(processors, 16, pinned).ShouldBe(expected);

    [TestCase(1, 8, 1)]
    [TestCase(3, 8, 3)]
    [TestCase(8, 8, 8)]
    [TestCase(8, 4, 4)]
    public void UploadPreparationWorkers_FollowTheDecodeWidth_AndTheStagingBuffers(int decode, int staging, int expected)
        => TextureStreamingCapacityModel.ResolveUploadPreparationWorkers(decode, staging).ShouldBe(expected);

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
