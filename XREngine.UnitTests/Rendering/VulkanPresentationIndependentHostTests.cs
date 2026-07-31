using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering.Vulkan.DeviceBootstrap;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed unsafe class VulkanPresentationIndependentHostTests
{
    [Test]
    public void VulkanModule_AdvertisesBothPresentationIndependentLanes()
    {
        RendererBackendCapabilities capabilities = new VulkanRendererBackendModuleEntry().Metadata.Capabilities;

        (capabilities & RendererBackendCapabilities.PresentationlessRendering)
            .ShouldBe(RendererBackendCapabilities.PresentationlessRendering);
        (capabilities & RendererBackendCapabilities.HeadlessWsiPresentation)
            .ShouldBe(RendererBackendCapabilities.HeadlessWsiPresentation);
    }

    [Test]
    public void HeadlessWsiProbe_AlwaysReturnsActionableStatus()
    {
        VulkanHeadlessWsiProbeResult result = VulkanHeadlessWsiSupport.Probe();

        result.Message.ShouldNotBeNullOrWhiteSpace();
        result.Message.ShouldContain(
            result.Supported
                ? VulkanHeadlessWsiSupport.ExtensionName
                : "presentationless");
    }

    [Test]
    public void TargetDrivers_KeepPresentationlessAndOpenXrFreeOfWsiRequirements()
    {
        IVulkanRendererTargetDriver presentationless = VulkanRendererTargetDriverFactory.Create(
            new RendererHostContext(new PresentationlessRenderTarget(16, 16)));
        IVulkanRendererTargetDriver openXr = VulkanRendererTargetDriverFactory.Create(
            new RendererHostContext(
                new OpenXrRenderTarget(
                    new RenderTargetOutputProperties(16, 16, Layers: 2))));

        presentationless.RequiresPresentQueue.ShouldBeFalse();
        presentationless.RequiresSwapchainOutput.ShouldBeFalse();
        presentationless.GetRequiredInstanceExtensions().ShouldBeEmpty();
        presentationless.RequiredDeviceExtensions.ShouldBeEmpty();

        openXr.RequiresPresentQueue.ShouldBeFalse();
        openXr.RequiresSwapchainOutput.ShouldBeFalse();
        openXr.GetRequiredInstanceExtensions().ShouldBeEmpty();
        openXr.RequiredDeviceExtensions.ShouldBeEmpty();
    }

    [Test]
    public void QueueSelection_PresentationlessModeSelectsDedicatedWorkQueuesWithoutPresent()
    {
        QueueFamilyProperties[] families =
        [
            new() { QueueFlags = QueueFlags.GraphicsBit | QueueFlags.ComputeBit | QueueFlags.TransferBit, QueueCount = 2 },
            new() { QueueFlags = QueueFlags.ComputeBit | QueueFlags.TransferBit, QueueCount = 1 },
            new() { QueueFlags = QueueFlags.TransferBit, QueueCount = 1 },
        ];

        VulkanRenderer.QueueFamilyIndices indices = VulkanQueueFamilySelector.Select(
            families,
            surfaceApi: null,
            physicalDevice: default,
            surface: default);

        indices.IsComplete(requirePresentQueue: false).ShouldBeTrue();
        indices.GraphicsFamilyIndex.ShouldBe(0u);
        indices.ComputeFamilyIndex.ShouldBe(1u);
        indices.TransferFamilyIndex.ShouldBe(2u);
        indices.PresentFamilyIndex.ShouldBeNull();
    }

    [Test]
    public void ProductionRenderer_PresentationlessModeInitializesSharedDeviceCoreWithoutSurfaceRequirements()
    {
        RendererHostContext context = new(
            new PresentationlessRenderTarget(16, 16, FrameSlotCount: 2),
            backendGeneration: 31);
        VulkanRenderer renderer = new(context);

        try
        {
            renderer.TargetDriverName.ShouldBe("VulkanPresentationlessTargetDriver");
            renderer.TargetRequiresPresentQueue.ShouldBeFalse();
            renderer.TargetRequiresSwapchainOutput.ShouldBeFalse();

            renderer.Initialize();

            renderer.Device.Handle.ShouldNotBe(0);
            renderer.GraphicsQueue.Handle.ShouldNotBe(0);
            renderer.PresentQueue.Handle.ShouldBe(0);
            renderer.HasInitializedMemoryAllocator.ShouldBeTrue();
            renderer.BackendObjectRegistry.ShouldNotBeNull();
            renderer.EnabledInstanceExtensions.ShouldNotContain("VK_KHR_surface");
            renderer.EnabledDeviceExtensions.ShouldNotContain("VK_KHR_swapchain");
        }
        catch (Exception exception) when (IsUnavailableVulkanEnvironment(exception))
        {
            Assert.Ignore($"Production presentationless Vulkan initialization is unavailable on this machine: {exception.Message}");
        }
        finally
        {
            renderer.CleanUp();
        }
    }

    [Test]
    public void PresentationlessHost_SubmitsAndReadsBackStableDeterministicFrames()
    {
        RenderTargetOutputProperties output = new(
            16,
            16,
            ColorFormat: EPixelInternalFormat.Rgba8,
            DepthFormat: EPixelInternalFormat.DepthComponent32f,
            FrameSlotCount: 2);

        try
        {
            using VulkanExplicitTargetRendererHost host = new(
                new PresentationlessRenderTarget(
                    output.Width,
                    output.Height,
                    output.Layers,
                    output.FrameSlotCount,
                    output.SampleCount,
                    output.ColorFormat,
                    output.DepthFormat),
                backendGeneration: 23);
            host.BackendGeneration.ShouldBe(23);
            host.Renderer.HasInitializedMemoryAllocator.ShouldBeTrue();
            host.Renderer.PresentQueue.Handle.ShouldBe(0);
            host.Renderer.EnabledInstanceExtensions.ShouldNotContain("VK_KHR_surface");
            host.Renderer.EnabledDeviceExtensions.ShouldNotContain("VK_KHR_swapchain");
            host.TargetGeneration.ShouldBe(1UL);

            host.SubmitFrame(RecordDeterministicClear);
            Should.Throw<ArgumentOutOfRangeException>(
                () => host.ReadbackLastSubmittedColor(maxByteCount: 4));
            string firstHash = host.ComputeLastSubmittedColorHash();
            host.SubmitFrame(RecordDeterministicClear);
            string secondHash = host.ComputeLastSubmittedColorHash();

            secondHash.ShouldBe(firstHash);
            firstHash.Length.ShouldBe(64);
            host.LastCompletedGpuFrameNanoseconds.ShouldBeGreaterThanOrEqualTo(0);
        }
        catch (Exception exception) when (IsUnavailableVulkanEnvironment(exception))
        {
            Assert.Ignore($"Presentationless Vulkan smoke test is unavailable on this machine: {exception.Message}");
        }
    }

    [Test]
    public void HeadlessWsiHost_EitherSubmitsOrReportsUnsupportedExplicitly()
    {
        RenderTargetOutputProperties output = new(
            16,
            16,
            ColorFormat: EPixelInternalFormat.Rgba8,
            DepthFormat: EPixelInternalFormat.DepthComponent32f,
            FrameSlotCount: 2);
        VulkanHeadlessWsiProbeResult probe = VulkanHeadlessWsiSupport.Probe();

        if (!probe.Supported)
        {
            NotSupportedException exception = Should.Throw<NotSupportedException>(
                () => new VulkanExplicitTargetRendererHost(
                    new HeadlessWsiRenderTarget(output),
                    backendGeneration: 0));
            exception.Message.ShouldContain("presentationless");
            return;
        }

        try
        {
            using VulkanExplicitTargetRendererHost host = new(
                new HeadlessWsiRenderTarget(output),
                backendGeneration: 29);
            host.SubmitFrame(RecordDeterministicClear);

            host.BackendGeneration.ShouldBe(29);
            host.PresentationUsesDesktopCompositor.ShouldBeFalse();
            host.PresentationDescription.ShouldContain("no-op");
        }
        catch (NotSupportedException exception)
        {
            Assert.Ignore($"The loader exposes VK_EXT_headless_surface, but the selected device/surface combination is unsupported: {exception.Message}");
        }
    }

    [Test]
    public void OpenXrTargetAdapter_MapsRuntimeOwnedImagesWithoutVulkanAcquireOrPresent()
    {
        VulkanRenderFrameTarget target = new(
            new Image(0x1001UL),
            new ImageView(0x1002UL),
            new Image(0x1003UL),
            new ImageView(0x1004UL),
            new Extent2D(1440, 1600),
            Layers: 2,
            ImageLayout.Undefined,
            ImageLayout.ColorAttachmentOptimal);

        VulkanFrameTargetLease lease = VulkanOpenXrTargetDriver.MapRuntimeOwnedImage(
            in target,
            Format.R8G8B8A8Srgb,
            Format.D32Sfloat,
            SampleCountFlags.Count1Bit,
            imageIndex: 7,
            viewIndex: 1,
            supportsHiddenAreaMask: true);

        lease.IsValid.ShouldBeTrue();
        lease.ImagesExternallyOwned.ShouldBeTrue();
        lease.CompletionKind.ShouldBe(VulkanFrameTargetCompletionKind.OpenXrRuntimeRelease);
        Assert.That(lease.SubmissionWaitSemaphore.Handle, Is.Zero);
        Assert.That(lease.SubmissionSignalSemaphore.Handle, Is.Zero);
        lease.Target.Layers.ShouldBe(2u);
        lease.ViewIndex.ShouldBe(1u);
        lease.SupportsHiddenAreaMask.ShouldBeTrue();
    }

    private static void RecordDeterministicClear(
        Vk api,
        CommandBuffer commandBuffer,
        VulkanRenderFrameTarget target)
    {
        ImageMemoryBarrier toTransferDestination = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = target.InitialColorLayout,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcAccessMask = target.InitialColorLayout == ImageLayout.Undefined
                ? 0
                : AccessFlags.TransferReadBit,
            DstAccessMask = AccessFlags.TransferWriteBit,
            Image = target.ColorImage,
            SubresourceRange = new ImageSubresourceRange(
                ImageAspectFlags.ColorBit,
                0,
                1,
                0,
                target.Layers),
        };
        api.CmdPipelineBarrier(
            commandBuffer,
            target.InitialColorLayout == ImageLayout.Undefined
                ? PipelineStageFlags.TopOfPipeBit
                : PipelineStageFlags.TransferBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            1,
            in toTransferDestination);

        ClearColorValue clear = new(0.25f, 0.5f, 0.75f, 1.0f);
        ImageSubresourceRange range = toTransferDestination.SubresourceRange;
        api.CmdClearColorImage(
            commandBuffer,
            target.ColorImage,
            ImageLayout.TransferDstOptimal,
            in clear,
            1,
            in range);

        ImageMemoryBarrier toFinal = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = target.RequiredFinalColorLayout,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = target.RequiredFinalColorLayout == ImageLayout.TransferSrcOptimal
                ? AccessFlags.TransferReadBit
                : 0,
            Image = target.ColorImage,
            SubresourceRange = range,
        };
        api.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.TransferBit,
            target.RequiredFinalColorLayout == ImageLayout.TransferSrcOptimal
                ? PipelineStageFlags.TransferBit
                : PipelineStageFlags.BottomOfPipeBit,
            0,
            0,
            null,
            0,
            null,
            1,
            in toFinal);
    }

    private static bool IsUnavailableVulkanEnvironment(Exception exception)
        => exception is DllNotFoundException or TypeInitializationException or NotSupportedException ||
           exception.Message.Contains("No Vulkan", StringComparison.OrdinalIgnoreCase) ||
           exception.Message.Contains("Failed to create", StringComparison.OrdinalIgnoreCase);
}
