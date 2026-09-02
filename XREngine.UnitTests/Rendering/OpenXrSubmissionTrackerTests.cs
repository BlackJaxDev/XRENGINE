using System;
using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenXR;
using Silk.NET.Vulkan;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class OpenXrSubmissionTrackerTests
{
    [Test]
    public void OpenXrEnvironmentVariable_AsyncSubmit_IsDefinedCorrectly()
    {
        XREngineEnvironmentVariables.OpenXrVulkanAsyncSubmit.ShouldBe("XRE_OPENXR_VULKAN_ASYNC_SUBMIT");
    }

    [Test]
    public void OpenXrAsyncSubmitEnabled_DefaultsToTrue()
    {
        string? original = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanAsyncSubmit);
        try
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanAsyncSubmit, null);
            VulkanCommandRuntime.IsOpenXrAsyncSubmitEnabled.ShouldBeTrue();

            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanAsyncSubmit, "0");
            VulkanCommandRuntime.IsOpenXrAsyncSubmitEnabled.ShouldBeFalse();

            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanAsyncSubmit, "1");
            VulkanCommandRuntime.IsOpenXrAsyncSubmitEnabled.ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanAsyncSubmit, original);
        }
    }

    [Test]
    public void RetiredOpenXrSwapchainGeneration_RetainsProperties()
    {
        Swapchain[] swapchains = [new Swapchain(101UL), new Swapchain(102UL)];
        uint[] counts = [3u, 3u];
        Semaphore semaphore = new(201UL);
        ulong timelineValue = 42UL;
        long timestamp = 123456789L;

        unsafe
        {
            RetiredOpenXrSwapchainGeneration generation = new(
                swapchains,
                new SwapchainImageVulkan2KHR*[2],
                counts,
                2u,
                timelineValue,
                semaphore,
                timestamp);

            generation.ViewCount.ShouldBe(2u);
            generation.TombstoneTimelineValue.ShouldBe(42UL);
            generation.TimelineSemaphore.Handle.ShouldBe(201UL);
            generation.EnqueuedTimestamp.ShouldBe(123456789L);
            generation.Swapchains[0].Handle.ShouldBe(101UL);
            generation.Swapchains[1].Handle.ShouldBe(102UL);
        }
    }

    [Test]
    public void VrStats_OpenXrDecouplingMetrics_RecordAndExposeValues()
    {
        // Record metrics
        RuntimeEngine.Rendering.Stats.Vr.RecordOpenXrEyeQueueSubmitTime(TimeSpan.FromMilliseconds(2.5));
        RuntimeEngine.Rendering.Stats.Vr.RecordOpenXrEyeCompletionWaitTime(TimeSpan.FromMilliseconds(0.5));
        RuntimeEngine.Rendering.Stats.Vr.RecordOpenXrEyeFenceForcedWait();
        RuntimeEngine.Rendering.Stats.Vr.RecordOpenXrEyeInFlightStats(2u, 1u, 3u);

        // Swap to publish
        RuntimeEngine.Rendering.Stats.Vr.SnapshotAndReset();

        // Validate published metrics
        RuntimeEngine.Rendering.Stats.Vr.VrOpenXrEyeQueueSubmitTimeMs.ShouldBeGreaterThanOrEqualTo(2.0);
        RuntimeEngine.Rendering.Stats.Vr.VrOpenXrEyeCompletionWaitTimeMs.ShouldBeGreaterThanOrEqualTo(0.4);
        RuntimeEngine.Rendering.Stats.Vr.VrOpenXrEyeFenceForcedWaitCount.ShouldBeGreaterThanOrEqualTo(1);
        RuntimeEngine.Rendering.Stats.Vr.VrOpenXrEyeInFlightCount.ShouldBe(2);
        RuntimeEngine.Rendering.Stats.Vr.VrOpenXrEyeOldestInFlightAgeFrames.ShouldBe(1);
        RuntimeEngine.Rendering.Stats.Vr.VrOpenXrEyeSwapchainImageReuseAgeFrames.ShouldBe(3);
    }
}
