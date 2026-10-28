using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanSwapchainDepthPublicationTests
{
    [Test]
    public void SwapchainDepth_IsPublishedAndRetiredAsOneImmutableGeneration()
    {
        string state = ReadWorkspaceFile(
            VulkanRoot + "Frame/Output/Authority/VulkanDesktopOutputState.cs");
        string output = ReadWorkspaceFile(
            VulkanRoot + "Frame/Output/Authority/VulkanOutputRuntime.cs");
        string depth = ReadWorkspaceFile(
            VulkanRoot + "Frame/Output/Authority/VulkanDesktopSwapchainService.Depth.cs");
        string swapchain = ReadWorkspaceFile(
            VulkanRoot + "Frame/Output/Authority/VulkanDesktopSwapchainService.cs");

        state.ShouldContain("internal VulkanSwapchainDepthResources? DepthResources;");
        state.ShouldContain("internal object DepthMutationGate { get; } = new();");
        output.ShouldContain("=> Volatile.Read(ref Desktop.DepthResources);");
        depth.ShouldContain("CreateVulkanImageTracked(ref imageInfo, out Image depthImage");
        depth.ShouldContain("TrackLiveImageView(depthView, in viewInfo, \"Swapchain.Depth\");");
        depth.ShouldContain("lock (_output.Desktop.DepthMutationGate)");
        depth.ShouldContain("Volatile.Write(ref _output.Desktop.DepthResources, resources);");
        depth.ShouldContain("return Interlocked.Exchange(ref _output.Desktop.DepthResources, null);");

        int detachIndex = swapchain.IndexOf(
            "VulkanSwapchainDepthResources? depth = DetachDepthResources();",
            StringComparison.Ordinal);
        detachIndex.ShouldBeGreaterThanOrEqualTo(0);
        int retireIndex = swapchain.IndexOf(
            "_resources.Images.RetireOwnedResources(new RetiredImageResources(",
            detachIndex,
            StringComparison.Ordinal);
        retireIndex.ShouldBeGreaterThan(detachIndex);
    }

    [Test]
    public void FrameRecordingAndReadback_CaptureOneDepthGeneration()
    {
        string recording = ReadWorkspaceFile(
            VulkanRoot + "Frame/Loop/VulkanFrameLoop.PrimaryRecordingPreparation.cs");
        string target = ReadWorkspaceFile(
            VulkanRoot + "Frame/Output/Authority/VulkanSwapchainRecordingTargetResolver.cs");
        string readback = ReadWorkspaceFile(
            VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.Readback.cs");
        string blitResolution = ReadWorkspaceFile(
            VulkanRoot + "Frame/Loop/Authority/VulkanFrameLoop.ReadbackResolution.cs");
        string blit = ReadWorkspaceFile(
            VulkanRoot + "Commands/Authority/VulkanCommandRuntime.LegacyBlitResolution.cs");

        // The recording target input reads the published depth generation one
        // time. The resolver then takes the image and the view from that input.
        recording.ShouldContain("OutputRuntime.DesktopDepthResources,");
        target.ShouldContain("input.DepthResources?.Image ?? default");
        target.ShouldContain("input.DepthResources?.View ?? default");
        readback.ShouldContain("VulkanSwapchainDepthResources? resources = OutputRuntime.DesktopDepthResources;");
        readback.ShouldContain("CreateSwapchainDepthReadbackSource(resources)");
        readback.ShouldContain("resources.Image,");
        readback.ShouldContain("resources.Extent,");
        blitResolution.ShouldContain("OutputRuntime.DesktopDepthResources);");
        blit.ShouldContain("desktopDepth.Image,");
        blit.ShouldContain("desktopDepth.Extent);");
    }

    [Test]
    public void SwapchainReplacement_DetachesExternalImageGenerationsBeforeHandleReuse()
    {
        string swapchain = ReadWorkspaceFile(
            VulkanRoot + "Frame/Output/Authority/VulkanDesktopSwapchainService.cs");
        string runtime = ReadWorkspaceFile(
            VulkanRoot + "Resources/Authority/VulkanResourceRuntime.cs");
        string tracker = ReadWorkspaceFile(
            VulkanRoot + "Resources/Lifetime/VulkanResourceLifetimeTracker.cs");
        string retirement = ReadWorkspaceFile(
            VulkanRoot + "Frame/Swapchain/RetiredSwapchainGeneration.cs");

        int detachIndex = swapchain.IndexOf(
            "_resources.DetachExternalImageLifetimesForHandleReuse(oldImages)",
            StringComparison.Ordinal);
        int createIndex = swapchain.IndexOf(
            "CreateSwapchain(oldSwapchain);",
            StringComparison.Ordinal);
        detachIndex.ShouldBeGreaterThanOrEqualTo(0);
        createIndex.ShouldBeGreaterThan(detachIndex);

        // A new generation can reuse a native image handle. The submitted
        // layout state of each fetched image must be removed before recording.
        int fetchIndex = swapchain.IndexOf(
            "throw new InvalidOperationException($\"Failed to fetch swapchain images ({result}).\");",
            StringComparison.Ordinal);
        int clearIndex = swapchain.IndexOf(
            "_resources.Images.ClearTrackedLayouts(generationImages[index]);",
            StringComparison.Ordinal);
        fetchIndex.ShouldBeGreaterThanOrEqualTo(0);
        clearIndex.ShouldBeGreaterThan(fetchIndex);

        runtime.ShouldContain("tracker.TryDetachExternalResourceIdentityNoLock(key, out slots[index]);");
        tracker.ShouldContain("internal bool TryDetachExternalResourceIdentityNoLock(");
        tracker.ShouldContain("if (!ResourceLifetimes.Remove(key))");
        tracker.ShouldContain("PublishedResourceGenerations.TryRemove(key, out _);");
        tracker.ShouldContain("DetachedResourceSlots.Add(pinned, slot);");
        runtime.ShouldContain("resource.Generation != expectedGeneration");
        runtime.ShouldContain("tracker.ReleaseDetachedResourceSlotNoLock(");
        retirement.ShouldContain("VulkanResourceSlotHandle[] ImageLifetimeSlots");
        swapchain.ShouldContain("? generation.ImageLifetimeSlots[i]");
        swapchain.ShouldContain("_resources.CompleteDetachedExternalResourceDestruction(");
        swapchain.ShouldNotContain("CompleteResourceDestruction(ObjectType.Image");
    }

    [Test]
    public void DeferredImageViewRetirement_IsQualifiedByHandleGeneration()
    {
        string entry = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Resources/Retirement/VulkanRenderer.RetiredImageResourceEntry.cs");
        string queue = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Retirement/VulkanResourceRetirementQueue.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");
        string imageViews = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Images/VulkanImageResourceService.cs");

        entry.ShouldContain("ulong PrimaryViewGeneration");
        entry.ShouldContain("ulong[] AttachmentViewGenerations");
        queue.ShouldContain("HashSet<VulkanPinnedResourceGeneration> AllImageViewHandles");
        imageViews.ShouldContain("primaryViewTicket.ResourceGeneration");
        retirement.ShouldContain("TryTakeImageViewGeneration(");
        retirement.ShouldContain("entry.PrimaryViewGeneration");
        retirement.ShouldContain("entry.AttachmentViewGenerations");
        imageViews.ShouldContain("TakeImageViewForRetirement(ImageView view, ulong generation, int frameSlot)");
        imageViews.ShouldContain("new VulkanResourceLifetimeKey(ObjectType.ImageView, view.Handle), generation");
    }

    [Test]
    public void DynamicRendering_RejectsRetiredAttachmentsBeforeNativeBegin()
    {
        string recording = ReadWorkspaceFile(
            VulkanRoot + "Commands/Authority/VulkanCommandRuntime.NativeRecordingServices.cs");
        string coordination = ReadWorkspaceFile(
            VulkanRoot + "Commands/Authority/VulkanCommandRuntime.ResourceCoordination.cs");
        string lifetime = ReadWorkspaceFile(
            VulkanRoot + "Resources/Authority/VulkanResourceRuntime.cs");

        int beginIndex = recording.IndexOf(
            "private unsafe void CmdBeginDynamicRendering(",
            StringComparison.Ordinal);
        beginIndex.ShouldBeGreaterThanOrEqualTo(0);
        int guardIndex = recording.IndexOf(
            "colorAttachments[index].ImageView.Handle);",
            beginIndex,
            StringComparison.Ordinal);
        int depthGuardIndex = recording.IndexOf(
            "renderingInfo->PDepthAttachment->ImageView.Handle);",
            beginIndex,
            StringComparison.Ordinal);
        int nativeBeginIndex = recording.IndexOf(
            "Api.CmdBeginRendering(commandBuffer, renderingInfo);",
            beginIndex,
            StringComparison.Ordinal);
        guardIndex.ShouldBeGreaterThan(beginIndex);
        depthGuardIndex.ShouldBeGreaterThan(guardIndex);
        nativeBeginIndex.ShouldBeGreaterThan(depthGuardIndex);

        // Tracking validates a dependency without a live published generation
        // before it returns. A retired attachment therefore throws before the
        // native begin command is recorded.
        coordination.ShouldContain("ResourceRuntime.TryValidateCommandBufferDependencyNoLock(");
        lifetime.ShouldContain(
            "attempted to record retired resource");
        lifetime.ShouldContain(
            "EVulkanResourceLifetimeState.PendingRetirement");
        lifetime.ShouldContain(
            "EVulkanResourceLifetimeState.Destroyed");
    }

    [Test]
    public void SwapchainAttachmentRetirement_AbortsFrameWithoutImmediateRecordingRetry()
    {
        string recording = ReadWorkspaceFile(
            VulkanRoot + "Frame/Loop/VulkanRenderer.FrameLoop.Recording.cs");
        string policy = ReadWorkspaceFile(
            VulkanRoot + "Commands/Authority/VulkanRecordingFailurePolicy.cs");

        recording.ShouldContain("return HandleDesktopRecordingDeferred(");
        recording.ShouldContain(
            "when (VulkanRecordingFailurePolicy.IsTransientResourceRetirement(ex))");
        recording.ShouldContain(
            "VulkanRecordingFailurePolicy.IsSwapchainResourceRetirement(reason);");
        recording.ShouldContain(
            "A generation-bound swapchain attachment retired during command recording");
        recording.ShouldContain("EDesktopFrameReason.RecordingDeferred;");
        policy.ShouldContain("internal static bool IsSwapchainResourceRetirement(string failureReason)");
        policy.ShouldContain("\"attempted to record retired resource\"");
        policy.ShouldContain("\"Swapchain.Color\"");
        policy.ShouldContain("\"Swapchain.Depth\"");
    }

    private const string VulkanRoot =
        "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/";

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
