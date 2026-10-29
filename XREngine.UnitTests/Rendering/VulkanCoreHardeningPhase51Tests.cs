using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCoreHardeningPhase51Tests
{
    [Test]
    public void UnknownPassInitialization_UsesExactRecordedSubresources()
    {
        // A new engine image publishes its creation layout for every subresource, so a
        // pass never sees an unknown subresource state.
        string imageLayouts = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.ImageLayouts.cs");
        string initialLayouts = SliceBetween(
            imageLayouts,
            "internal void RegisterTrackedImageInitialLayouts(",
            "internal void ResetCommandBufferImageLayoutJournal(");

        initialLayouts.ShouldContain("createInfo.InitialLayout");
        initialLayouts.ShouldContain("for (uint mip = 0; mip < mipLevels; mip++)");
        initialLayouts.ShouldContain("for (uint layer = 0; layer < arrayLayers; layer++)");
        initialLayouts.ShouldContain("VulkanTrackedImageSubresource key = new(imageHandle, mip, layer, aspect);");
        initialLayouts.ShouldContain("Synchronization._trackedImageSubresourceStates[key] = state;");
        initialLayouts.ShouldNotContain("LastKnownLayout");

        // Planned barriers take their old layout from the exact recorded subresource
        // range of this command buffer generation.
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Synchronization/VulkanRenderer.BarrierEmission.cs");
        string method = SliceBetween(
            source,
            "private uint BuildPlannedImageBarriers(",
            "private static void BuildPlannedBufferBarriers(");

        method.ShouldContain("BaseArrayLayer = planned.Range.BaseArrayLayer");
        method.ShouldContain("LayerCount = Math.Max(1u, planned.Range.LayerCount)");
        method.ShouldContain("includeEntryState: false");
        method.ShouldContain("includeUndefinedState: true");
        method.ShouldContain("effectiveOldLayout = recordedLayout;");
        method.ShouldContain("OldLayout = effectiveOldLayout");
        method.ShouldNotContain("LastKnownLayout");
    }

    [Test]
    public void OrderedCommandBuffers_SeedEntryStateAndValidateGeneration()
    {
        string synchronization = SourceContractWorkspace.ReadVulkanCommandRuntimeSource();
        string imgui = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiOverlayCommandRecorder.cs");
        string dynamicText = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanDynamicUiBatchTextOverlayRecorder.cs");

        synchronization.ShouldContain("EntrySubresources");
        synchronization.ShouldContain("EntryStateIncomplete");
        synchronization.ShouldContain("internal void SeedRecordedImageLayoutState(");
        synchronization.ShouldContain("recorded.EntryStateIncomplete = predecessorState.EntryStateIncomplete;");
        synchronization.ShouldContain("ValidateOrderedCommandBufferImageStateContracts(");
        synchronization.ShouldContain("VulkanImageEntryStateContract.Compare(");
        synchronization.ShouldContain("TryGetRecordedImageEntryStateMismatch(");
        imgui.ShouldContain("encoder.Runtime.SeedRecordedImageLayoutState(\n                input.OverlayCommandBuffer,\n                input.PredecessorCommandBuffer);");
        dynamicText.ShouldContain("encoder.Runtime.SeedRecordedImageLayoutState(\n                input.OverlayCommandBuffer,\n                input.PredecessorCommandBuffer);");
    }

    [Test]
    public void PrimaryReuse_ValidatesOnlyTouchedImageEntryStates()
    {
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.ImageLayouts.cs");
        string allocation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.NativeRecordingServices.cs");

        string reuseValidation = SliceBetween(
            allocation,
            "private bool IsCommandBufferVariantImageLayoutStateDirty(",
            "private bool TryGetRecordedImageEntryStateMismatch(");

        reuseValidation.ShouldContain("TryGetRecordedImageEntryStateMismatch(");
        reuseValidation.ShouldContain("variant.PrimaryCommandBuffer");
        reuseValidation.ShouldNotContain("variant.RecordedImageLayoutStartSignature != imageLayoutStartSignature");
        synchronization.ShouldContain("recorded.EntrySubresources[key] = entrySubmitted!.Submitted");
        synchronization.ShouldContain("recorded.EntryStateIncomplete = true");
    }

    [Test]
    public void ExplicitBarrierOldLayout_IsNeverRewrittenFromGlobalState()
    {
        // The tracked encoder records the caller barriers unchanged. It only publishes
        // the new layout after the native barrier.
        string encoder = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanTrackedCommandEncoder.cs");
        string barrier = SliceBetween(
            encoder,
            "internal void PipelineBarrier(",
            "internal void ClearColorImage(");

        int nativeBarrier = barrier.IndexOf("Api.CmdPipelineBarrier(", StringComparison.Ordinal);
        nativeBarrier.ShouldBeGreaterThanOrEqualTo(0);
        barrier.IndexOf("barrier.NewLayout,", nativeBarrier, StringComparison.Ordinal).ShouldBeGreaterThan(nativeBarrier);
        barrier.ShouldNotContain("OldLayout =");
        barrier.ShouldNotContain("recordedOldLayout");

        string synchronization = SourceContractWorkspace.ReadVulkanCommandRuntimeSource();
        synchronization.ShouldNotContain("barrier.OldLayout = recordedOldLayout");
        synchronization.ShouldNotContain("imageBarriers[i].OldLayout = recordedOldLayout");
    }

    [Test]
    public void DescriptorAndUiTransitions_AreExplicitAndConsumerScoped()
    {
        string descriptorLayouts = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorImageLayouts.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanDescriptorLifetimeAuthority.cs");
        string barriers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Synchronization/VulkanRenderer.BarrierEmission.cs");
        string imgui = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiTextureRegistryService.cs");

        descriptorLayouts.ShouldNotContain("TryTransitionDedicatedImageLayout");
        lifetime.ShouldContain("state.HasReflection = reflectedBindings is not null;");
        lifetime.ShouldContain("if (binding.Set == setIndex && IsLifetimeTrackedImageDescriptorType(binding.DescriptorType))");
        lifetime.ShouldContain("state.ReflectedImageBindings.Add(binding.Binding);");

        // Sampling transitions only cover image bindings that the consumer reflects.
        string sampling = SliceBetween(
            barriers,
            "internal bool TransitionPublishedDescriptorSetImagesForSampling(",
            "private bool TryPrepareSecondaryDescriptorImageRequirements(");
        sampling.ShouldContain("if (snapshot.HasReflection && Array.IndexOf(snapshot.ReflectedImageBindings, published.Binding) < 0)\n                    continue;");
        sampling.ShouldContain("TransitionDescriptorImageForSampling(");

        // ImGui only binds descriptor-ready textures in the layout that their source publishes.
        imgui.ShouldContain("if (!source.IsDescriptorReady)");
        imgui.ShouldContain("status = EVulkanImGuiTextureRegistrationStatus.NeedsUpload;");
        imgui.ShouldContain("descriptorLayout = VulkanProgramUtilities.ResolveDescriptorImageLayout(source, DescriptorType.CombinedImageSampler);");
    }

    [Test]
    public void RejectedAcquiredFramesAndRetirementHaveBoundedRecovery()
    {
        string frameLoop = string.Concat(
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Acquire.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Recovery.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Desktop/Policy/VulkanRejectedDesktopFramePolicy.cs"),
            ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Presentation.cs"));
        string tracker = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Lifetime/VulkanResourceLifetimeTracker.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.LifetimeLedger.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");

        frameLoop.ShouldContain("case Result.SuboptimalKhr:");
        frameLoop.ShouldContain("TryRecoverRejectedDesktopImage");
        frameLoop.ShouldContain("if (!imageWasEverPresented)");
        frameLoop.ShouldContain("if (!imageHasValidCompletedContent)");
        frameLoop.ShouldContain("ERejectedDesktopFramePolicyReason.NoCompletedFinalWrite");
        frameLoop.ShouldContain("PresentedWithoutValidFinalWrite");
        frameLoop.ShouldNotContain("OldLayout = ImageLayout.Undefined,\n                            NewLayout = ImageLayout.PresentSrcKhr");

        // Retirement fences new recording of the resource and publishes the command
        // buffers that depend on it before it captures the retirement ticket.
        tracker.ShouldContain("ResourceCommandBufferDependencies");
        string captureTicket = SliceBetween(
            lifetime,
            "internal VulkanRetirementTicket CaptureRetirementTicket(",
            "internal VulkanRetirementTicket CaptureImageViewRetirementTicket(");
        int admissionFence = captureTicket.IndexOf("Lifetime.Tracker.FenceResourceRecordingAdmission(key, owner);", StringComparison.Ordinal);
        admissionFence.ShouldBeGreaterThanOrEqualTo(0);
        captureTicket.IndexOf("Lifetime.PublishTrackingDependenciesBeforeRetirement(key);", StringComparison.Ordinal)
            .ShouldBeGreaterThan(admissionFence);
        lifetime.ShouldContain("Lifetime.Tracker.ResourceCommandBufferDependencies.TryGetValue(");

        retirement.ShouldContain("TryBeginDestroyResourceGeneration(");
        lifetime.ShouldContain("internal bool TryBeginDestroyResourceGeneration(");
        retirement.ShouldContain("CompleteRetiredImageDeduplication(frameSlot, in entry)");
        retirement.ShouldNotContain("Api!.FreeMemory(device, memory, null)");
    }

    [Test]
    public void Vulkan14FeatureChains_AreGatedByCorePromotionOrEnabledExtension()
    {
        string queries = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.FeatureQueries.cs");
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.LogicalDeviceBootstrap.cs");

        queries.ShouldContain("promotedToCore = IsVulkanApiVersionAtLeast(properties.ApiVersion, 1u, 4u)");
        queries.ShouldContain("if (!promotedToCore && !extensionEnabled)");
        string maintenance5Query = SliceBetween(
            queries,
            "internal unsafe void QueryMaintenance5Capabilities(",
            "featureSupported = featuresKhr.Maintenance5;");
        maintenance5Query.ShouldContain("if (!promotedToCore && !extensionEnabled)\n            return;");
        source.ShouldContain("dynamicRenderingLocalReadFeatureSupported");
        source.ShouldContain("maintenance5FeatureSupported");
        source.ShouldContain("bool enableMaintenance5Feature = maintenance5FeatureSupported;");
        source.ShouldContain("Maintenance5 = enableMaintenance5Feature,");
    }

    [Test]
    public void SwapchainAcquireAndSecondaryInheritance_MatchTheirExecutionScopes()
    {
        string extensions = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/Device/VulkanDeviceContext.Instance.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string secondaries = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");
        string primaryRecording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/VulkanCommandRuntime.PrimaryRecording.cs");

        extensions.ShouldContain("VK_EXT_swapchain_colorspace");
        extensions.ShouldContain("IsInstanceExtensionAvailable(api, \"VK_EXT_swapchain_colorspace\")");
        recording.ShouldContain("ImageLayout.Undefined => PipelineStageFlags.ColorAttachmentOutputBit");
        recording.ShouldContain("TryGetRecordedImageAccessState(");
        recording.ShouldContain("ImageLayout depthOldLayout = hasRecordedDepthState");
        secondaries.ShouldContain("bool includeDepthAttachment = true");
        secondaries.ShouldContain("variant.DynamicUiSecondaryIncludesDepth == includeDepthAttachment");
        primaryRecording.ShouldContain("includeDepthAttachment: false");
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
        => SourceContractWorkspace.ReadFile(relativePath);

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
