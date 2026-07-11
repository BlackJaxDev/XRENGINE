using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCoreHardeningPhase51Tests
{
    [Test]
    public void UnknownPassInitialization_UsesExactRecordedSubresources()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string method = SliceBetween(
            source,
            "private void EmitInitialImageAspectBarriers(",
            "private void EmitPlannedImageBarriers(");

        method.ShouldContain("for (uint mip = 0; mip < mipLevels; mip++)");
        method.ShouldContain("TryGetRecordedImageAccessState(commandBuffer, group.Image, single, out _)");
        method.ShouldContain("BaseArrayLayer = firstUnknownLayer");
        method.ShouldContain("LayerCount = layer - firstUnknownLayer");
        method.ShouldContain("OldLayout = ImageLayout.Undefined");
        method.ShouldNotContain("LastKnownLayout");
    }

    [Test]
    public void OrderedCommandBuffers_SeedEntryStateAndValidateGeneration()
    {
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string imgui = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs");
        string dynamicText = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.SecondaryCommandBuffers.cs");

        synchronization.ShouldContain("EntrySubresources");
        synchronization.ShouldContain("SeedRecordedImageLayoutState(");
        synchronization.ShouldContain("ValidateOrderedCommandBufferImageStateContracts(");
        synchronization.ShouldContain("actual.ResourceGeneration != expected.ResourceGeneration");
        imgui.ShouldContain("SeedRecordedImageLayoutState(commandBuffer, predecessorCommandBuffer)");
        dynamicText.ShouldContain("SeedRecordedImageLayoutState(commandBuffer, predecessorCommandBuffer)");
    }

    [Test]
    public void ExplicitBarrierOldLayout_IsNeverRewrittenFromGlobalState()
    {
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string barrier = SliceBetween(
            synchronization,
            "private void ValidateRecordedImageBarrierOldLayout(",
            "private void RecordImageAccess(");

        barrier.ShouldContain("preserving the caller contract");
        barrier.ShouldNotContain("barrier.OldLayout = recordedOldLayout");
        synchronization.ShouldNotContain("imageBarriers[i].OldLayout = recordedOldLayout");
    }

    [Test]
    public void DescriptorAndUiTransitions_AreExplicitAndConsumerScoped()
    {
        string descriptorLayouts = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorImageLayouts.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string imgui = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs");

        descriptorLayouts.ShouldNotContain("TryTransitionDedicatedImageLayout");
        lifetime.ShouldContain("ReflectedImageBindings");
        lifetime.ShouldContain("if (setState.HasReflection && !setState.ReflectedImageBindings.Contains(binding))");
        lifetime.ShouldContain("throw new InvalidOperationException(message)");
        imgui.ShouldContain("TransitionImGuiSnapshotTexturesForSampling(commandBuffer, drawData)");
        imgui.ShouldContain("drawCommand.TextureId");
        imgui.ShouldContain("CmdPipelineBarrierTracked(");
    }

    [Test]
    public void RejectedAcquiredFramesAndRetirementHaveBoundedRecovery()
    {
        string frameLoop = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");

        frameLoop.ShouldContain("else if (result == Result.SuboptimalKhr)");
        frameLoop.ShouldContain("TryPresentAbortedDirtyFrame");
        frameLoop.ShouldContain("if (!imageWasEverPresented)");
        frameLoop.ShouldContain("if (!imageHasValidPresentedContent)");
        frameLoop.ShouldContain("Refusing skipped-frame present for unwritten swapchain image");
        frameLoop.ShouldContain("PresentedWithoutValidFinalWrite");
        frameLoop.ShouldNotContain("OldLayout = ImageLayout.Undefined,\n                            NewLayout = ImageLayout.PresentSrcKhr");
        lifetime.ShouldContain("_vulkanResourceCommandBufferDependencies");
        lifetime.ShouldContain("InvalidateCachedCommandBuffersForRetiringResource(");
        retirement.ShouldContain("TryBeginDestroyVulkanResourceGeneration(");
        retirement.ShouldContain("CompleteRetiredImageDeduplication(frameSlot, in r)");
        retirement.ShouldNotContain("Api!.FreeMemory(device, memory, null)");
    }

    [Test]
    public void Vulkan14FeatureChains_AreGatedByCorePromotionOrEnabledExtension()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs");

        source.ShouldContain("promotedToCore = IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 4u)");
        source.ShouldContain("if (!promotedToCore && !extensionEnabled)");
        source.ShouldContain("dynamicRenderingLocalReadFeatureSupported");
        source.ShouldContain("maintenance5FeatureSupported");
        source.ShouldContain("if (enableMaintenance5Feature)");
    }

    [Test]
    public void SwapchainAcquireAndSecondaryInheritance_MatchTheirExecutionScopes()
    {
        string extensions = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanExtensions.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");
        string secondaries = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.SecondaryCommandBuffers.cs");

        extensions.ShouldContain("VK_EXT_swapchain_colorspace");
        extensions.ShouldContain("IsInstanceExtensionAvailable(ExtSwapchainColorspaceExtensionName)");
        recording.ShouldContain("ImageLayout.Undefined => PipelineStageFlags.ColorAttachmentOutputBit");
        recording.ShouldContain("TryGetRecordedImageAccessState(");
        recording.ShouldContain("ImageLayout depthOldLayout = hasRecordedDepthState");
        secondaries.ShouldContain("bool includeDepthAttachment = true");
        secondaries.ShouldContain("variant.DynamicUiSecondaryIncludesDepth == includeDepthAttachment");
        secondaries.ShouldContain("includeDepthAttachment: false");
    }

    [Test]
    public void StartupLightProbeCapture_CanBeEnabledAfterActivation()
    {
        string lifecycle = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Scene/Components/Capture/LightProbeComponent.Lifecycle.cs");

        lifecycle.ShouldContain("case nameof(AutoCaptureOnActivate):");
        lifecycle.ShouldContain("case nameof(World):");
        lifecycle.ShouldContain("ScheduleStartupCaptureIfRequested();");
        lifecycle.ShouldContain("FullCapture(Resolution, CaptureDepthCubeMap);");
        lifecycle.ShouldNotContain("FullCapture(128, false)");
    }

    private static string SliceBetween(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Expected start token '{startToken}'.");
        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Expected end token '{endToken}'.");
        return source[start..end];
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string path = Path.Combine(
            ResolveRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        path = ResolveRelocatedCommandBufferPath(path);
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{relativePath}'.");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static string ResolveRelocatedCommandBufferPath(string path)
    {
        if (File.Exists(path))
            return path;

        string marker = $"{Path.DirectorySeparatorChar}Commands{Path.DirectorySeparatorChar}VulkanRenderer.";
        return path.Replace(
            marker,
            $"{Path.DirectorySeparatorChar}Commands{Path.DirectorySeparatorChar}CommandBuffers{Path.DirectorySeparatorChar}VulkanRenderer.",
            StringComparison.Ordinal);
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
