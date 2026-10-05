using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Lights;
using XREngine.Rendering;

namespace XREngine.UnitTests.VR;

/// <summary>Exercises real completion-owner transitions with deterministic backend fence states.</summary>
[TestFixture]
[NonParallelizable]
public sealed class VrSpectatorOutputLifetimeTests
{
    private int _previousRenderThread;
    [SetUp]
    public void EnterRenderThread()
    {
        _previousRenderThread = RuntimeEngine.RenderThreadId;
        RuntimeEngine.AssignRenderThread(Environment.CurrentManagedThreadId);
    }
    [TearDown]
    public void LeaveRenderThread() => RuntimeEngine.AssignRenderThread(_previousRenderThread);

    [Test]
    public void PendingSubmission_DoesNotPollOrPublish()
    {
        var (output, slot, fence) = Writer();
        fence.Submission = EGpuFenceSubmissionStatus.AwaitingSubmission;
        Advance(output).ShouldBeFalse();
        fence.PollCount.ShouldBe(0);
        Get(output, "_writer").ShouldBeSameAs(slot);
        Get(output, "_published").ShouldBeNull();
    }

    [Test]
    public void RejectedSubmission_ReleasesWriterAndAllowsRetry()
    {
        var (output, _, fence) = Writer();
        fence.Submission = EGpuFenceSubmissionStatus.Failed;
        Advance(output).ShouldBeFalse();
        fence.IsDisposed.ShouldBeTrue();
        Get(output, "_writer").ShouldBeNull();
        output.LastFailure.ShouldNotBeNull();
        output.LastFailure!.ShouldContain("retry");
        Advance(output).ShouldBeTrue();
    }

    [Test]
    public void FailedSubmittedFence_QuarantinesInsteadOfUnsafeRetirement()
    {
        var (output, slot, fence) = Writer();
        fence.Status = EGpuFenceStatus.Failed;
        Advance(output).ShouldBeFalse();
        fence.IsDisposed.ShouldBeFalse();
        Get(slot, "_quarantined", typeof(AdvancedOffscreenTextureCaptureComponent)).ShouldBe(true);
        Get(output, "_writer").ShouldBeNull();
        output.LastFailure.ShouldNotBeNull();
        output.LastFailure!.ShouldContain("recreate");
    }

    [Test]
    public void OrphanedPendingSlot_IsDrainedAfterLostReceipt()
    {
        var (output, slot, fence) = Writer();
        Set(output, "_writer", null);
        Drain(output, slot).ShouldBeTrue();
        fence.PollCount.ShouldBe(1);
        Get(slot, "_writerFence", typeof(AdvancedOffscreenTextureCaptureComponent)).ShouldBeSameAs(fence);
        fence.Status = EGpuFenceStatus.Signaled;
        Drain(output, slot).ShouldBeTrue();
        fence.IsDisposed.ShouldBeTrue();
        Get(slot, "_writerFence", typeof(AdvancedOffscreenTextureCaptureComponent)).ShouldBeNull();
        Get(output, "_published").ShouldBeNull();
    }

    [Test]
    public void CompletedPreCutWriter_IsNotPublishedIntoNewHistory()
    {
        var (output, slot, fence) = Writer();
        Set(slot, "_writerAuthored", true, typeof(AdvancedOffscreenTextureCaptureComponent));
        Set(output, "_historyVersion", 2L);
        Set(output, "_writerHistoryVersion", 1L);
        fence.Status = EGpuFenceStatus.Signaled;
        Advance(output).ShouldBeTrue();
        Get(output, "_published").ShouldBeNull();
        Get(output, "_writer").ShouldBeNull();
    }

    [Test]
    public void CompletedCurrentWriter_PublishesOnlyAfterSignal()
    {
        var (output, slot, fence) = Writer();
        Set(slot, "_writerAuthored", true, typeof(AdvancedOffscreenTextureCaptureComponent));
        Advance(output).ShouldBeFalse();
        Get(output, "_published").ShouldBeNull();
        fence.Status = EGpuFenceStatus.Signaled;
        Advance(output).ShouldBeTrue();
        Get(output, "_published").ShouldBeSameAs(slot);
        output.CompletedFrames.ShouldBe(1);
    }

    private static (VrSpectatorOutputComponent, SpectatorTextureCaptureComponent, SpectatorTestFence) Writer()
    {
        var output = new VrSpectatorOutputComponent();
        var slot = new SpectatorTextureCaptureComponent();
        var fence = new SpectatorTestFence();
        Set(slot, "_writerFence", fence, typeof(AdvancedOffscreenTextureCaptureComponent));
        Set(output, "_writer", slot);
        return (output, slot, fence);
    }
    private static bool Advance(VrSpectatorOutputComponent output)
        => (bool)typeof(VrSpectatorOutputComponent).GetMethod("TryAdvanceWriter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(output, null)!;
    private static bool Drain(VrSpectatorOutputComponent output, SpectatorTextureCaptureComponent slot)
        => (bool)typeof(VrSpectatorOutputComponent).GetMethod("DrainUnownedWriter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(output, [slot])!;
    private static object? Get(object target, string name, Type? type = null)
        => (type ?? target.GetType()).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string name, object? value, Type? type = null)
        => (type ?? target.GetType()).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
