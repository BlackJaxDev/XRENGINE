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
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs");

        source.ShouldContain("private VulkanSwapchainDepthResources? _swapchainDepthResources;");
        source.ShouldContain("private readonly object _swapchainDepthMutationLock = new();");
        source.ShouldContain("=> Volatile.Read(ref _swapchainDepthResources);");
        source.ShouldContain("CreateVulkanImageTracked(ref imageInfo, out Image depthImage");
        source.ShouldContain("TrackLiveImageView(depthView, in viewInfo, \"Swapchain.Depth\");");
        source.ShouldContain("lock (_swapchainDepthMutationLock)");
        source.ShouldContain("Volatile.Write(ref _swapchainDepthResources, resources);");

        int detachIndex = source.IndexOf(
            "Interlocked.Exchange(ref _swapchainDepthResources, null)",
            StringComparison.Ordinal);
        int retireIndex = source.IndexOf(
            "RetireImageResources(new RetiredImageResources(",
            detachIndex,
            StringComparison.Ordinal);
        detachIndex.ShouldBeGreaterThanOrEqualTo(0);
        retireIndex.ShouldBeGreaterThan(detachIndex);
    }

    [Test]
    public void FrameRecordingAndReadback_CaptureOneDepthGeneration()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string readback = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Readback.cs");
        string blit = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs");

        recording.ShouldContain("VulkanSwapchainDepthResources? depth = CurrentSwapchainDepthResources;");
        recording.ShouldContain("depth?.Image ?? default");
        recording.ShouldContain("depth?.View ?? default");
        readback.ShouldContain("VulkanSwapchainDepthResources? resources = CurrentSwapchainDepthResources;");
        readback.ShouldContain("Image = resources.Image");
        blit.ShouldContain("VulkanSwapchainDepthResources? depth = CurrentSwapchainDepthResources;");
        blit.ShouldContain("depth.Extent");
    }

    [Test]
    public void SwapchainReplacement_DetachesExternalImageGenerationsBeforeHandleReuse()
    {
        string swapchain = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Swapchain.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.RetiredSwapchainGeneration.cs");

        int detachIndex = swapchain.IndexOf(
            "DetachSwapchainImageLifetimesForHandleReuse(oldImages)",
            StringComparison.Ordinal);
        int createIndex = swapchain.IndexOf(
            "CreateAllSwapChainObjects(oldSwapchain)",
            StringComparison.Ordinal);
        detachIndex.ShouldBeGreaterThanOrEqualTo(0);
        createIndex.ShouldBeGreaterThan(detachIndex);
        swapchain.ShouldContain("ClearTrackedImageLayouts(swapChainImages[i]);");

        lifetime.ShouldContain("DetachExternalVulkanResourceLifetimeForHandleReuse(");
        lifetime.ShouldContain("_vulkanResourceLifetimes.Remove(key);");
        lifetime.ShouldContain("_vulkanPublishedResourceGenerations.TryRemove(key, out _);");
        lifetime.ShouldContain("resource.Generation != expectedGeneration");
        retirement.ShouldContain("ulong[] ImageLifetimeGenerations");
        retirement.ShouldContain("CompleteDetachedExternalVulkanResourceDestruction(");
        retirement.ShouldNotContain(
            "CompleteVulkanResourceDestruction(ObjectType.Image, image.Handle, force);");
    }

    [Test]
    public void DeferredImageViewRetirement_IsQualifiedByHandleGeneration()
    {
        string entry = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.RetiredImageResourceEntry.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");
        string imageViews = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Textures/VulkanRenderer.ImageViewLifetime.cs");

        entry.ShouldContain("ulong PrimaryViewGeneration");
        entry.ShouldContain("ulong[] AttachmentViewGenerations");
        retirement.ShouldContain(
            "HashSet<VulkanPinnedResourceGeneration> _retiredImageViewHandlesAll");
        retirement.ShouldContain("primaryViewTicket.ResourceGeneration");
        retirement.ShouldContain("TryBeginDestroyImageViewGeneration(");
        retirement.ShouldContain("entry.PrimaryViewGeneration");
        retirement.ShouldContain("entry.AttachmentViewGenerations");
        imageViews.ShouldContain(
            "TryBeginDestroyVulkanResourceGeneration(");
        imageViews.ShouldContain("ulong expectedGeneration");
    }

    [Test]
    public void DynamicRendering_RejectsRetiredAttachmentsBeforeNativeBegin()
    {
        string extensions = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanExtensions.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        int guardIndex = extensions.IndexOf(
            "EnsureVulkanImageViewAvailableForCommandRecording(",
            StringComparison.Ordinal);
        int nativeBeginIndex = extensions.IndexOf(
            "Api!.CmdBeginRendering(commandBuffer, renderingInfo);",
            StringComparison.Ordinal);
        guardIndex.ShouldBeGreaterThanOrEqualTo(0);
        nativeBeginIndex.ShouldBeGreaterThan(guardIndex);
        lifetime.ShouldContain(
            "attempted to record retired Vulkan resource");
        lifetime.ShouldContain(
            "EVulkanResourceLifetimeState.PendingRetirement");
        lifetime.ShouldContain(
            "EVulkanResourceLifetimeState.Destroyed");
    }

    [Test]
    public void SwapchainAttachmentRetirement_AbortsFrameWithoutImmediateRecordingRetry()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string frameLoop = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");

        recording.ShouldContain(
            "!IsTransientResourceRetirementRecordingFailure(recordingDeferredReason) ||");
        recording.ShouldContain(
            "IsSwapchainResourceRetirementRecordingFailure(recordingDeferredReason)");
        frameLoop.ShouldContain(
            "IsSwapchainResourceRetirementRecordingFailure(");
        frameLoop.ShouldContain(
            "A generation-bound swapchain attachment retired during command recording");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string path = Path.Combine(
            ResolveRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
        return File.ReadAllText(path);
    }

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
