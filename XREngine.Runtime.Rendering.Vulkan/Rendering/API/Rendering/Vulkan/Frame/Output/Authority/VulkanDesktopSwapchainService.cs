using System.Diagnostics;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.Vulkan.RenderGraph;
using Image = Silk.NET.Vulkan.Image;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns desktop swapchain-generation retirement sequencing and its bounded output state.</summary>
internal sealed unsafe partial class VulkanDesktopSwapchainService
{
    private static readonly SurfaceFormatPreference[] HdrSurfacePreferences =
    [
        new(Format.R16G16B16A16Sfloat, ColorSpaceKHR.SpaceExtendedSrgbLinearExt),
        new(Format.R16G16B16A16Sfloat, ColorSpaceKHR.SpaceDisplayP3NonlinearExt),
        new(Format.R16G16B16A16Sfloat, ColorSpaceKHR.SpaceHdr10ST2084Ext),
        new(Format.A2B10G10R10UnormPack32, ColorSpaceKHR.SpaceHdr10ST2084Ext),
        new(Format.A2R10G10B10UnormPack32, ColorSpaceKHR.SpaceHdr10ST2084Ext),
    ];
    private static readonly SurfaceFormatPreference[] DlssFrameGenerationHdrSurfacePreferences =
    [
        new(Format.A2B10G10R10UnormPack32, ColorSpaceKHR.SpaceHdr10ST2084Ext),
        new(Format.A2R10G10B10UnormPack32, ColorSpaceKHR.SpaceHdr10ST2084Ext),
    ];
    private static readonly SurfaceFormatPreference[] SdrSurfacePreferences =
    [
        new(Format.B8G8R8A8Srgb, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
        new(Format.R8G8B8A8Srgb, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
        new(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
        new(Format.R8G8B8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
    ];
    private static readonly SurfaceFormatPreference[] DlssFrameGenerationSdrSurfacePreferences =
    [
        new(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
        new(Format.R8G8B8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
    ];
    private static readonly PresentModeKHR[] DlssFrameGenerationPresentModePreferences =
    [PresentModeKHR.MailboxKhr, PresentModeKHR.ImmediateKhr];
    private readonly VulkanOutputRuntime _output;
    private readonly Vk _api;
    private readonly VulkanDeviceContext _device;
    private readonly VulkanResourceRuntime _resources;
    private readonly VulkanImGuiOutputPipelineService _imguiPipeline;
    private readonly VulkanDesktopWsiTargetDriver? _desktopWsiTarget;
    private readonly IVulkanTargetOutputHost _services;

    internal VulkanDesktopSwapchainService(
        VulkanOutputRuntime output,
        Vk api,
        VulkanDeviceContext device,
        VulkanResourceRuntime resources,
        VulkanFrameTelemetry telemetry,
        VulkanImGuiOutputPipelineService imguiPipeline,
        VulkanDesktopWsiTargetDriver? desktopWsiTarget,
        IVulkanTargetOutputHost services)
    {
        _output = output;
        _api = api;
        _device = device;
        _resources = resources;
        _imguiPipeline = imguiPipeline;
        _desktopWsiTarget = desktopWsiTarget;
        _services = services;
    }

    internal void QueueRetiredGeneration(RetiredSwapchainGeneration generation)
    {
        _output._retiredSwapchainGenerations.Add(generation);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanSwapchainRetirement(
            queued: 1,
            pending: _output._retiredSwapchainGenerations.Count);
        Debug.Vulkan(
            "[Vulkan] Queued swapchain generation retirement. Extent={0}x{1} Pending={2}/8 Handle=0x{3:X}.",
            generation.Width,
            generation.Height,
            _output._retiredSwapchainGenerations.Count,
            generation.Swapchain.Handle);
    }

    /// <summary>Builds the initial desktop WSI generation through the same output-owned stages used by recreation.</summary>
    internal void CreateInitialGeneration()
    {
        if (_output.Desktop.Swapchain.Handle != 0)
            throw new InvalidOperationException("The initial desktop swapchain generation has already been created.");

        CreateSwapchain();
        try
        {
            CreateImageViews();
            CreateStreamlineUiResources();
            CreateDepthResources();
            CreateRenderPasses();
            CreateFramebuffers();
            _output._imguiResources.OverlayCommandBuffers =
                _services.CreateDesktopOutputArtifacts(
                _output.Desktop.Images?.Length
                    ?? throw new InvalidOperationException("Desktop images were not published."));
            _services.ReserveOpenXrFrameDataSlots(_output.Desktop.Images.Length);
            PublishPlannerExtent(_output.Desktop.Extent);
        }
        catch
        {
            RetireDesktopGenerationAfterFailedCreation();
            throw;
        }
    }

    /// <summary>Releases the live desktop output after the shutdown path has made the device idle.</summary>
    internal void DestroyLiveGenerationForShutdown()
    {
        RetireDesktopCommandArtifacts();
        RetireStreamlineUiResources();
        RetireLiveFramebuffers();
        DestroyRenderPassesImmediately();
        DestroyImageViews();
        VulkanSwapchainDepthResources? depth = DetachDepthResources();
        if (depth is not null)
            _resources.Images.RetireOwnedResources(new RetiredImageResources(
                depth.Image, depth.Memory, depth.View, [], default, 0),
                "Swapchain.Depth.Shutdown");
        DrainCompletedDependencies();
        DestroySwapchain();
    }

    internal bool TryPrepareRetirementMarkers(out Fence graphicsMarkerFence, out Fence presentMarkerFence)
    {
        graphicsMarkerFence = default;
        presentMarkerFence = default;
        DrainRetiredGenerations();
        if (_output._retiredSwapchainGenerations.Count >= 8 || _output._orphanedSwapchainMarkerFences.Count != 0)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanSwapchainRetirement(pending: _output._retiredSwapchainGenerations.Count, deferred: 1);
            return false;
        }

        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        if (_api.CreateFence(_device.Device, ref fenceInfo, null, out graphicsMarkerFence) != Result.Success)
            return false;

        bool distinctPresentQueue = _device.PresentQueue.Handle != _device.GraphicsQueue.Handle;
        if (distinctPresentQueue && _api.CreateFence(_device.Device, ref fenceInfo, null, out presentMarkerFence) != Result.Success)
        {
            _api.DestroyFence(_device.Device, graphicsMarkerFence, null);
            graphicsMarkerFence = default;
            return false;
        }

        if (!TrySubmitRetirementMarker(_device.GraphicsQueue, graphicsMarkerFence, "SwapchainRetirement.Graphics"))
        {
            _api.DestroyFence(_device.Device, graphicsMarkerFence, null);
            if (presentMarkerFence.Handle != 0)
                _api.DestroyFence(_device.Device, presentMarkerFence, null);
            graphicsMarkerFence = default;
            presentMarkerFence = default;
            return false;
        }

        if (distinctPresentQueue && !TrySubmitRetirementMarker(_device.PresentQueue, presentMarkerFence, "SwapchainRetirement.Present"))
        {
            _output._orphanedSwapchainMarkerFences.Add(graphicsMarkerFence);
            _api.DestroyFence(_device.Device, presentMarkerFence, null);
            graphicsMarkerFence = default;
            presentMarkerFence = default;
            return false;
        }

        return true;
    }

    /// <summary>Replaces one complete desktop WSI generation using only output, resource, and command authorities.</summary>
    internal bool TryRecreateGeneration()
    {
        if (Interlocked.CompareExchange(ref _output.Desktop.RecreateInProgress, 1, 0) != 0)
            return false;

        try
        {
            if (!IsSurfacePresentable(out string reason))
            {
                Debug.VulkanEvery(
                    $"Vulkan.Swapchain.RecreateDeferred.{GetHashCode()}",
                    TimeSpan.FromMilliseconds(500),
                    "[Vulkan] Deferring desktop swapchain recreation because the surface is not presentable. Reason={0}",
                    reason);
                return false;
            }

            DisableStreamlineFrameGenerationBeforeMutation("swapchain recreation");
            if (!TryPrepareRetirementMarkers(out Fence graphicsMarker, out Fence presentMarker))
                return false;

            SwapchainKHR oldSwapchain = _output.Desktop.Swapchain;
            Image[] oldImages = _output.Desktop.Images ?? [];
            ImageView[] oldViews = _output.Desktop.ImageViews ?? [];
            Framebuffer[] oldFramebuffers = _output.Desktop.Framebuffers ?? [];
            Semaphore[] oldPresentBridges = _output.Desktop.PresentBridgeSemaphores ?? [];
            bool oldStreamlineProxy = _output.Desktop.StreamlineFrameGenerationActive;
            uint oldWidth = _output.Desktop.Extent.Width;
            uint oldHeight = _output.Desktop.Extent.Height;
            ulong oldGraphicsCompletionValue = 0;
            if (_output.Desktop.ImageTimelineValues is { } oldImageTimelineValues)
            {
                for (int i = 0; i < oldImageTimelineValues.Length; i++)
                    oldGraphicsCompletionValue = Math.Max(
                        oldGraphicsCompletionValue,
                        oldImageTimelineValues[i]);
            }
            ulong[] oldImageGenerations = _resources.DetachExternalImageLifetimesForHandleReuse(oldImages);

            RetireDesktopCommandArtifacts();
            _imguiPipeline.InvalidateForDesktopOutputMutation();
            RetireStreamlineUiResources();
            // The resource runtime carries the frame slot published by the frame
            // loop; retirement therefore remains correct even while no renderer
            // facade is available here.
            RetireLiveFramebuffers();
            (RenderPass oldClear, RenderPass oldLoad) = DetachRenderPasses();
            DestroyImageViews();
            VulkanSwapchainDepthResources? depth = DetachDepthResources();
            if (depth is not null)
                _resources.Images.RetireOwnedResources(new RetiredImageResources(
                    depth.Image, depth.Memory, depth.View, [], default, 0),
                    "Swapchain.Depth");

            _output.Desktop.PresentBridgeSemaphores = null;
            _output.Desktop.Swapchain = default;
            _output.Desktop.Images = null;
            _output.Desktop.ImageEverPresented = null;
            _output.Desktop.ImageHasValidPresentedContent = null;
            _output.Desktop.StreamlineFrameGenerationActive = false;
            _output.Desktop.StreamlineFrameGenerationIncludesDlss = false;

            RetiredSwapchainGeneration retired = new(
                oldSwapchain, oldImages, oldImageGenerations, oldViews, oldFramebuffers,
                oldPresentBridges, oldClear, oldLoad, graphicsMarker, presentMarker,
                oldStreamlineProxy, oldWidth, oldHeight, Stopwatch.GetTimestamp());
            try
            {
                CreateSwapchain(oldSwapchain);
                CreateImageViews();
                CreateStreamlineUiResources();
                CreateDepthResources();
                CreateRenderPasses();
                CreateFramebuffers();
                _output.Desktop.PresentBridgeSemaphores = CreatePresentBridgeSemaphores(_output.Desktop.Images!.Length);
                _output._imguiResources.OverlayCommandBuffers =
                    _services.CreateDesktopOutputArtifacts(
                        _output.Desktop.Images.Length);
                _services.ReserveOpenXrFrameDataSlots(_output.Desktop.Images.Length);
                ulong[] imageTimelineValues = new ulong[_output.Desktop.Images.Length];
                if (oldGraphicsCompletionValue != 0)
                {
                    // Mapped frame-data slots are indexed by swapchain image. A
                    // replacement generation may reuse an index before the old
                    // image's accepted graphics work completes, so every new image
                    // inherits the strongest old completion proof. Clearing this
                    // ledger wedged the arena in Submitted after a resize/recreate.
                    Array.Fill(imageTimelineValues, oldGraphicsCompletionValue);
                }
                _output.Desktop.ImageTimelineValues = imageTimelineValues;
                PublishPlannerExtent(_output.Desktop.Extent);
                return true;
            }
            catch
            {
                RetireLiveFramebuffers();
                (RenderPass clear, RenderPass load) = DetachRenderPasses();
                RetireStreamlineUiResources();
                VulkanSwapchainDepthResources? failedDepth = DetachDepthResources();
                if (failedDepth is not null)
                    _resources.Images.RetireOwnedResources(new RetiredImageResources(
                        failedDepth.Image, failedDepth.Memory, failedDepth.View, [], default, 0),
                        "Swapchain.Depth.RecreateFailure");

                Image[] failedImages = _output.Desktop.Images ?? [];
                ulong[] failedImageGenerations = _resources.DetachExternalImageLifetimesForHandleReuse(failedImages);
                ImageView[] failedViews = _output.Desktop.ImageViews ?? [];
                DestroyImageViews();
                Semaphore[] failedPresentBridges = _output.Desktop.PresentBridgeSemaphores ?? [];
                SwapchainKHR failedSwapchain = _output.Desktop.Swapchain;
                bool failedStreamlineProxy = _output.Desktop.StreamlineFrameGenerationActive;
                uint failedWidth = _output.Desktop.Extent.Width;
                uint failedHeight = _output.Desktop.Extent.Height;

                // A failed replacement must leave no live desktop generation
                // visible to the next recreation attempt.  Its resources still
                // share the already-submitted retirement proof with the old
                // generation, so queue it rather than destroying in-place.
                _output.Desktop.Swapchain = default;
                _output.Desktop.Images = null;
                _output.Desktop.ImageEverPresented = null;
                _output.Desktop.ImageHasValidPresentedContent = null;
                _output.Desktop.PresentBridgeSemaphores = null;
                _output.Desktop.ImageTimelineValues = null;
                _output.Desktop.StreamlineFrameGenerationActive = false;
                _output.Desktop.StreamlineFrameGenerationIncludesDlss = false;
                QueueRetiredGeneration(new(
                    failedSwapchain,
                    failedImages,
                    failedImageGenerations,
                    failedViews,
                    [],
                    failedPresentBridges,
                    clear, load, default, default,
                    failedStreamlineProxy,
                    failedWidth, failedHeight,
                    Stopwatch.GetTimestamp()));
                throw;
            }
            finally
            {
                QueueRetiredGeneration(retired);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _output.Desktop.RecreateInProgress, 0);
        }
    }

    /// <summary>Creates the native desktop WSI swapchain and publishes its image generation.</summary>
    internal void CreateSwapchain(SwapchainKHR oldSwapchain = default)
    {
        SwapChainSupportDetails support = QuerySwapchainSupport(_device.PhysicalDevice);
        SurfaceFormatKHR surfaceFormat = ChooseSurfaceFormat(support.Formats);
        PresentModeKHR presentMode = ChoosePresentMode(support.PresentModes);
        if (!TryChooseExtent(support.Capabilities, out Extent2D extent, out string unavailableReason))
            throw new InvalidOperationException($"Cannot create Vulkan swapchain while the surface is not presentable: {unavailableReason}");

        uint imageCount = support.Capabilities.MinImageCount + 1;
        if (support.Capabilities.MaxImageCount > 0 && imageCount > support.Capabilities.MaxImageCount)
            imageCount = support.Capabilities.MaxImageCount;

        SwapchainCreateInfoKHR createInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _output.Surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            PreTransform = support.Capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = oldSwapchain,
        };

        QueueFamilyIndices indices = _device.QueueFamilies;
        uint* queueFamilyIndices = stackalloc[] { indices.GraphicsFamilyIndex!.Value, indices.PresentFamilyIndex!.Value };
        if (indices.GraphicsFamilyIndex != indices.PresentFamilyIndex)
        {
            createInfo.ImageSharingMode = SharingMode.Concurrent;
            createInfo.QueueFamilyIndexCount = 2;
            createInfo.PQueueFamilyIndices = queueFamilyIndices;
        }
        else
            createInfo.ImageSharingMode = SharingMode.Exclusive;

        bool usePresentScaling = TryGetPresentScalingConfiguration(
            presentMode,
            extent,
            out SwapchainPresentScalingCreateInfoEXT presentScalingCreateInfo,
            out SurfacePresentScalingCapabilitiesEXT presentScalingCapabilities);
        if (usePresentScaling)
            createInfo.PNext = &presentScalingCreateInfo;

        if (!_api.TryGetDeviceExtension(_device.Instance, _device.Device, out KhrSwapchain? resolvedSwapchainExtension))
            throw new NotSupportedException("VK_KHR_swapchain extension not found.");
        KhrSwapchain swapchainExtension = resolvedSwapchainExtension
            ?? throw new NotSupportedException("VK_KHR_swapchain extension was returned without an implementation.");
        _output.Desktop.SwapchainExtension = swapchainExtension;

        bool requestFrameGeneration = _output._streamlineFrameGenerationProvisioned;
        bool requestFrameGenerationDlss = requestFrameGeneration && _output._streamlineDlssProvisioned;
        VulkanStreamlineDeviceBinding binding = _output.CaptureStreamlineDeviceBinding(_device);
        Result result;
        if (requestFrameGeneration)
        {
            if (!NvidiaDlssManager.Native.TryCreateProxySwapchain(
                    binding, ref createInfo, requestFrameGenerationDlss, out _output.Desktop.Swapchain, out result, out string failureReason))
            {
                if (NvidiaDlssManager.IsFrameGenerationRequested)
                    throw new InvalidOperationException($"Requested NVIDIA DLSS frame generation could not create a Streamline proxy swapchain: {failureReason}");

                Debug.RenderingWarning("[Vulkan] Optional DLSS-G proxy-swapchain provisioning failed; creating a direct Vulkan swapchain and disabling the live DLSS-G toggle. Reason={0}", failureReason);
                _output._streamlineFrameGenerationProvisioned = false;
                requestFrameGeneration = false;
                requestFrameGenerationDlss = false;
                result = swapchainExtension.CreateSwapchain(_device.Device, ref createInfo, null, out _output.Desktop.Swapchain);
            }
        }
        else
            result = swapchainExtension.CreateSwapchain(_device.Device, ref createInfo, null, out _output.Desktop.Swapchain);

        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create swap chain ({result}){(requestFrameGeneration ? " through Streamline for NVIDIA DLSS frame generation" : string.Empty)}.");

        _output.Desktop.Generation++;
        _output.Desktop.PresentScalingActive = usePresentScaling;
        _output.Desktop.PresentScalingCapabilities = usePresentScaling ? presentScalingCapabilities : default;
        _output.Desktop.StreamlineFrameGenerationActive = requestFrameGeneration;
        _output.Desktop.StreamlineFrameGenerationIncludesDlss = requestFrameGenerationDlss;
        binding = _output.CaptureStreamlineDeviceBinding(_device);

        if (_output.Desktop.StreamlineFrameGenerationActive)
        {
            if (!NvidiaDlssManager.Native.TryGetProxySwapchainImages(binding, _output.Desktop.Swapchain, ref imageCount, null, out result, out string failureReason))
                throw new InvalidOperationException($"Requested NVIDIA DLSS frame generation could not query Streamline proxy swapchain images: {failureReason}");
        }
        else
            result = swapchainExtension.GetSwapchainImages(_device.Device, _output.Desktop.Swapchain, ref imageCount, null);

        if (result != Result.Success || imageCount == 0)
            throw new InvalidOperationException($"Failed to query swapchain image count ({result}).");

        _output.Desktop.Images = new Image[imageCount];
        _resources.Descriptors.EnsureFrameSlotCountFloor(checked((int)imageCount));
        _output.Desktop.ImageEverPresented = new bool[imageCount];
        _output.Desktop.ImageHasValidPresentedContent = new bool[imageCount];
        fixed (Image* images = _output.Desktop.Images)
        {
            if (_output.Desktop.StreamlineFrameGenerationActive)
            {
                if (!NvidiaDlssManager.Native.TryGetProxySwapchainImages(binding, _output.Desktop.Swapchain, ref imageCount, images, out result, out string failureReason))
                    throw new InvalidOperationException($"Requested NVIDIA DLSS frame generation could not fetch Streamline proxy swapchain images: {failureReason}");
            }
            else
                result = swapchainExtension.GetSwapchainImages(_device.Device, _output.Desktop.Swapchain, ref imageCount, images);
        }

        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to fetch swapchain images ({result}).");

        _output.Desktop.ImageFormat = surfaceFormat.Format;
        _output.Desktop.ImageColorSpace = surfaceFormat.ColorSpace;
        _output.Desktop.Extent = extent;
        Debug.VulkanWarningEvery("Vulkan.Swapchain.SelectedSurfaceFormat", TimeSpan.FromSeconds(10),
            "[Vulkan] Swapchain surface selected: format={0} colorSpace={1} presentMode={2} extent={3}x{4} images={5}",
            surfaceFormat.Format, surfaceFormat.ColorSpace, presentMode, extent.Width, extent.Height, imageCount);
    }

    /// <summary>Destroys the active native WSI generation after Streamline has been disabled.</summary>
    internal void DestroySwapchain()
    {
        if (_output.Desktop.Swapchain.Handle == 0)
            return;

        VulkanStreamlineDeviceBinding binding = _output.CaptureStreamlineDeviceBinding(_device);
        KhrSwapchain swapchainExtension = RequireSwapchainExtension();
        if (_output.Desktop.StreamlineFrameGenerationActive &&
            !NvidiaDlssManager.Native.TryDestroyProxySwapchain(binding, _output.Desktop.Swapchain, out string failureReason))
        {
            Debug.RenderingError("NVIDIA DLSS frame generation failed to destroy the Streamline proxy swapchain cleanly ({0}). Attempting direct VK_KHR_swapchain destruction for teardown cleanup.", failureReason);
            swapchainExtension.DestroySwapchain(_device.Device, _output.Desktop.Swapchain, null);
        }
        else if (!_output.Desktop.StreamlineFrameGenerationActive)
            swapchainExtension.DestroySwapchain(_device.Device, _output.Desktop.Swapchain, null);

        ResetLiveSwapchainState();
    }

    internal void DisableStreamlineFrameGenerationBeforeMutation(string reason)
    {
        if (!_output.Desktop.StreamlineFrameGenerationActive)
            return;

        var viewports = RequireDesktopWsiTarget().Window.Viewports;
        VulkanStreamlineDeviceBinding binding = _output.CaptureStreamlineDeviceBinding(_device);
        for (int index = 0; index < viewports.Count; index++)
            if (!NvidiaDlssManager.Native.TryDisableFrameGeneration(binding, viewports[index], out string failureReason))
                Debug.RenderingError("NVIDIA DLSS frame generation could not be disabled before {0} for viewport {1}: {2}", reason, viewports[index].Index, failureReason);
    }

    internal void DrainStreamlineFrameGenerationDisableBeforePresent()
    {
        if (!_output.Desktop.StreamlineFrameGenerationActive || NvidiaDlssManager.IsFrameGenerationRequested)
            return;

        var viewports = RequireDesktopWsiTarget().Window.Viewports;
        VulkanStreamlineDeviceBinding binding = _output.CaptureStreamlineDeviceBinding(_device);
        for (int index = 0; index < viewports.Count; index++)
            if (!NvidiaDlssManager.Native.TryDrainFrameGenerationDisableForPresent(binding, viewports[index], out string failureReason))
                Debug.RenderingError("NVIDIA DLSS frame generation could not finish its disable drain for viewport {0}: {1}", viewports[index].Index, failureReason);
    }

    internal bool IsSurfacePresentable(out string reason)
    {
        if (_output.SurfaceApi is null || _output.Surface.Handle == 0 || _device.PhysicalDevice.Handle == 0)
        {
            reason = "surface query is not initialized";
            return false;
        }

        SwapChainSupportDetails support = QuerySwapchainSupport(_device.PhysicalDevice);
        if (support.Formats.Length == 0 || support.PresentModes.Length == 0)
        {
            reason = support.Formats.Length == 0 ? "surface reported no formats" : "surface reported no present modes";
            return false;
        }

        return TryChooseExtent(support.Capabilities, out _, out reason);
    }

    internal SwapChainSupportDetails QuerySupport(PhysicalDevice physicalDevice)
        => QuerySwapchainSupport(physicalDevice);

    private void ResetLiveSwapchainState()
    {
        _output.Desktop.Swapchain = default;
        _output.Desktop.Images = null;
        _output.Desktop.ImageEverPresented = null;
        _output.Desktop.ImageHasValidPresentedContent = null;
        _output.Desktop.StreamlineFrameGenerationActive = false;
        _output.Desktop.StreamlineFrameGenerationIncludesDlss = false;
    }

    private KhrSwapchain RequireSwapchainExtension()
        => _output.Desktop.SwapchainExtension
            ?? throw new InvalidOperationException("The desktop swapchain extension is not initialized.");

    private void RetireDesktopGenerationAfterFailedCreation()
    {
        RetireDesktopCommandArtifacts();
        _imguiPipeline.InvalidateForDesktopOutputMutation();
        RetireStreamlineUiResources();
        RetireLiveFramebuffers();
        DestroyRenderPassesImmediately();
        DestroyImageViews();
        VulkanSwapchainDepthResources? depth = DetachDepthResources();
        if (depth is not null)
            _resources.Images.RetireOwnedResources(new RetiredImageResources(
                depth.Image, depth.Memory, depth.View, [], default, 0),
                "Swapchain.Depth.CreateFailure");
        DestroySwapchain();
    }

    private void PublishPlannerExtent(Extent2D extent)
    {
        _services.PublishDesktopSwapchainExtent(extent);
    }

    /// <summary>
    /// Retires command- and output-owned command buffers as one swapchain
    /// generation so ImGui can never retain buffers from a replaced image set.
    /// </summary>
    private void RetireDesktopCommandArtifacts()
    {
        CommandBuffer[]? imguiOverlayCommandBuffers =
            _output._imguiResources.OverlayCommandBuffers;
        _services.RetireDesktopOutputArtifacts(imguiOverlayCommandBuffers);
        _output._imguiResources.OverlayCommandBuffers = null;
    }

    /// <summary>
    /// Creates the image-indexed present bridge semaphores for the live desktop
    /// WSI generation.  They are output objects rather than global sync state:
    /// a replacement generation owns an independent set until the old one has
    /// passed its retirement marker.
    /// </summary>
    internal void CreateLivePresentBridgeSemaphores(int count)
    {
        if (_output.Desktop.PresentBridgeSemaphores is not null)
            throw new InvalidOperationException("The desktop WSI generation already owns present bridge semaphores.");

        _output.Desktop.PresentBridgeSemaphores = CreatePresentBridgeSemaphores(count);
    }

    /// <summary>Destroys the still-live desktop present bridges during full device shutdown.</summary>
    internal void DestroyLivePresentBridgeSemaphores()
    {
        Semaphore[]? semaphores = _output.Desktop.PresentBridgeSemaphores;
        if (semaphores is null)
            return;

        for (int index = 0; index < semaphores.Length; index++)
            if (semaphores[index].Handle != 0)
                _api.DestroySemaphore(_device.Device, semaphores[index], null);

        _output.Desktop.PresentBridgeSemaphores = null;
    }

    private Semaphore[] CreatePresentBridgeSemaphores(int count)
    {
        Semaphore[] semaphores = new Semaphore[Math.Max(1, count)];
        SemaphoreCreateInfo createInfo = new() { SType = StructureType.SemaphoreCreateInfo };
        for (int index = 0; index < semaphores.Length; index++)
        {
            if (_api.CreateSemaphore(_device.Device, ref createInfo, null, out semaphores[index]) == Result.Success)
                continue;
            for (int created = 0; created < index; created++)
                _api.DestroySemaphore(_device.Device, semaphores[created], null);
            throw new InvalidOperationException("Failed to create a desktop present bridge semaphore.");
        }
        return semaphores;
    }

    private SurfaceFormatKHR ChooseSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> formats)
    {
        bool requestHdr = RequireDesktopWsiTarget().PreferHdrOutput;
        bool frameGeneration = _output._streamlineFrameGenerationProvisioned;
        if (_device.MutableCapabilities._supportsSwapchainColorspace && requestHdr && frameGeneration && TrySelectFormat(formats, DlssFrameGenerationHdrSurfacePreferences, out SurfaceFormatKHR dlssHdr))
            return SetPreferredFormat(dlssHdr);
        if (frameGeneration && TrySelectFormat(formats, DlssFrameGenerationSdrSurfacePreferences, out SurfaceFormatKHR dlssSdr))
            return SetPreferredFormat(dlssSdr);
        if (frameGeneration)
            throw new NotSupportedException("NVIDIA DLSS frame generation requires an RGB10 HDR10 or UNORM SDR Vulkan swapchain format; this surface exposes no Streamline-compatible back-buffer format.");
        if (_device.MutableCapabilities._supportsSwapchainColorspace && requestHdr && TrySelectFormat(formats, HdrSurfacePreferences, out SurfaceFormatKHR hdr))
            return SetPreferredFormat(hdr);
        if (TrySelectFormat(formats, SdrSurfacePreferences, out SurfaceFormatKHR sdr))
            return SetPreferredFormat(sdr);
        return formats[0];
    }

    private SurfaceFormatKHR SetPreferredFormat(SurfaceFormatKHR format)
    {
        _output.Desktop.PreferredFormat = format.Format;
        _output.Desktop.PreferredColorSpace = format.ColorSpace;
        return format;
    }

    private PresentModeKHR ChoosePresentMode(IReadOnlyList<PresentModeKHR> modes)
    {
        if (_output._streamlineFrameGenerationProvisioned)
            for (int preferenceIndex = 0; preferenceIndex < DlssFrameGenerationPresentModePreferences.Length; preferenceIndex++)
                foreach (PresentModeKHR mode in modes)
                    if (mode == DlssFrameGenerationPresentModePreferences[preferenceIndex])
                        return mode;

        foreach (PresentModeKHR mode in modes)
            if (mode == _output.Desktop.PreferredPresentMode)
                return mode;
        return _output.Desktop.FallbackPresentMode;
    }

    private bool TryChooseExtent(SurfaceCapabilitiesKHR capabilities, out Extent2D extent, out string reason)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            extent = capabilities.CurrentExtent;
            reason = extent.Width == 0 || extent.Height == 0 ? $"surface current extent is {extent.Width}x{extent.Height}" : string.Empty;
            return extent.Width != 0 && extent.Height != 0;
        }

        VulkanDesktopWsiTargetDriver desktopWsiTarget = RequireDesktopWsiTarget();
        Vector2D<int> framebuffer = desktopWsiTarget.EffectiveFramebufferSize;
        Vector2D<int> window = desktopWsiTarget.Window.Window.Size;
        if ((framebuffer.X <= 0 || framebuffer.Y <= 0) && (window.X <= 0 || window.Y <= 0))
        {
            extent = default;
            reason = $"window/framebuffer extents are {window.X}x{window.Y}/{framebuffer.X}x{framebuffer.Y}";
            return false;
        }

        extent = new Extent2D
        {
            Width = Math.Clamp((uint)Math.Max(Math.Max(framebuffer.X, window.X), 1), capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Height = Math.Clamp((uint)Math.Max(Math.Max(framebuffer.Y, window.Y), 1), capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height),
        };
        reason = extent.Width == 0 || extent.Height == 0 ? "surface clamp produced a zero extent" : string.Empty;
        return extent.Width != 0 && extent.Height != 0;
    }

    private VulkanDesktopWsiTargetDriver RequireDesktopWsiTarget()
        => _desktopWsiTarget
            ?? throw new InvalidOperationException(
                "The desktop swapchain service is unavailable for the active Vulkan target.");

    private SwapChainSupportDetails QuerySwapchainSupport(PhysicalDevice physicalDevice)
    {
        SwapChainSupportDetails details = new();
        KhrSurface surface = _output.SurfaceApi ?? throw new InvalidOperationException("Vulkan surface API is not initialized.");
        surface.GetPhysicalDeviceSurfaceCapabilities(physicalDevice, _output.Surface, out details.Capabilities);
        uint count = 0;
        surface.GetPhysicalDeviceSurfaceFormats(physicalDevice, _output.Surface, ref count, null);
        details.Formats = count == 0 ? [] : new SurfaceFormatKHR[count];
        if (count != 0)
            fixed (SurfaceFormatKHR* formats = details.Formats)
                surface.GetPhysicalDeviceSurfaceFormats(physicalDevice, _output.Surface, ref count, formats);
        count = 0;
        surface.GetPhysicalDeviceSurfacePresentModes(physicalDevice, _output.Surface, ref count, null);
        details.PresentModes = count == 0 ? [] : new PresentModeKHR[count];
        if (count != 0)
            fixed (PresentModeKHR* modes = details.PresentModes)
                surface.GetPhysicalDeviceSurfacePresentModes(physicalDevice, _output.Surface, ref count, modes);
        return details;
    }

    private static bool TrySelectFormat(IReadOnlyList<SurfaceFormatKHR> formats, SurfaceFormatPreference[] preferences, out SurfaceFormatKHR selected)
    {
        foreach (SurfaceFormatPreference preference in preferences)
            foreach (SurfaceFormatKHR format in formats)
                if (format.Format == preference.Format && format.ColorSpace == preference.ColorSpace)
                {
                    selected = format;
                    return true;
                }
        selected = default;
        return false;
    }

    private bool TryGetPresentScalingConfiguration(PresentModeKHR presentMode, Extent2D extent, out SwapchainPresentScalingCreateInfoEXT createInfo, out SurfacePresentScalingCapabilitiesEXT capabilities)
    {
        createInfo = default;
        capabilities = default;
        if (!_device.MutableCapabilities._surfacePresentScalingInstanceExtensionsEnabled || !_output.Desktop.Maintenance1Enabled ||
            !_api.TryGetInstanceExtension<KhrGetSurfaceCapabilities2>(_device.Instance, out KhrGetSurfaceCapabilities2? surfaceCapabilities2))
            return false;

        SurfacePresentModeEXT presentModeInfo = new() { SType = StructureType.SurfacePresentModeExt, PresentMode = presentMode };
        PhysicalDeviceSurfaceInfo2KHR surfaceInfo = new() { SType = StructureType.PhysicalDeviceSurfaceInfo2Khr, PNext = &presentModeInfo, Surface = _output.Surface };
        SurfacePresentScalingCapabilitiesEXT queried = new() { SType = StructureType.SurfacePresentScalingCapabilitiesExt };
        SurfaceCapabilities2KHR surfaceCapabilities = new() { SType = StructureType.SurfaceCapabilities2Khr, PNext = &queried };
        if (surfaceCapabilities2.GetPhysicalDeviceSurfaceCapabilities2(_device.PhysicalDevice, &surfaceInfo, &surfaceCapabilities) != Result.Success)
            return false;

        capabilities = queried;
        bool supported = (queried.SupportedPresentScaling & PresentScalingFlagsKHR.StretchBitExt) != 0 &&
            extent.Width >= queried.MinScaledImageExtent.Width && extent.Height >= queried.MinScaledImageExtent.Height &&
            extent.Width <= queried.MaxScaledImageExtent.Width && extent.Height <= queried.MaxScaledImageExtent.Height;
        if (!supported)
            return false;
        createInfo = new SwapchainPresentScalingCreateInfoEXT { SType = StructureType.SwapchainPresentScalingCreateInfoExt, ScalingBehavior = PresentScalingFlagsKHR.StretchBitExt, PresentGravityX = PresentGravityFlagsKHR.CenteredBitExt, PresentGravityY = PresentGravityFlagsKHR.CenteredBitExt };
        return true;
    }

    internal void DrainRetiredGenerations(bool force = false)
    {
        if (force)
            BeginForcedRetirementDrain();
        try
        {
            DrainOrphanedMarkers(force);
            int drained = 0;
            for (int index = _output._retiredSwapchainGenerations.Count - 1; index >= 0; index--)
            {
                RetiredSwapchainGeneration generation = _output._retiredSwapchainGenerations[index];
                if (!force &&
                    (!IsMarkerComplete(generation.GraphicsMarkerFence) ||
                     !IsMarkerComplete(generation.PresentMarkerFence)))
                    continue;

                PublishCompletedMarker(generation.GraphicsMarkerFence, force);
                PublishCompletedMarker(generation.PresentMarkerFence, force);
                DrainCompletedDependencies();
                if (!force && HasLiveDependencies(generation))
                    continue;

                DestroyGeneration(generation, force);
                DestroyMarker(generation.GraphicsMarkerFence);
                DestroyMarker(generation.PresentMarkerFence);
                _output._retiredSwapchainGenerations.RemoveAt(index);
                drained++;
            }

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanSwapchainRetirement(
                drained: drained,
                pending: _output._retiredSwapchainGenerations.Count);
        }
        finally
        {
            if (force)
                EndForcedRetirementDrain();
        }
    }

    private void DrainOrphanedMarkers(bool force)
    {
        for (int index = _output._orphanedSwapchainMarkerFences.Count - 1; index >= 0; index--)
        {
            Fence fence = _output._orphanedSwapchainMarkerFences[index];
            if (!force && !IsMarkerComplete(fence))
                continue;

            PublishCompletedMarker(fence, force);
            DestroyMarker(fence);
            _output._orphanedSwapchainMarkerFences.RemoveAt(index);
        }
    }

    private bool TrySubmitRetirementMarker(Queue queue, Fence fence, string owner)
    {
        SubmitInfo submitInfo = new() { SType = StructureType.SubmitInfo };
        Result result = _services.SubmitToQueueTracked(queue, ref submitInfo, fence, owner);
        if (result == Result.Success)
            return true;
        Debug.VulkanWarning("[Vulkan] Swapchain retirement marker submission failed. Owner={0} Result={1}.", owner, result);
        return false;
    }

    private void BeginForcedRetirementDrain()
    {
        lock (_resources.Lifetime.Tracker.SyncRoot)
            _resources.Lifetime.Tracker.ForcedRetirementDrainDepth++;
    }

    private void EndForcedRetirementDrain()
    {
        lock (_resources.Lifetime.Tracker.SyncRoot)
            _resources.Lifetime.Tracker.ForcedRetirementDrainDepth = Math.Max(0, _resources.Lifetime.Tracker.ForcedRetirementDrainDepth - 1);
    }

    private bool IsMarkerComplete(Fence fence)
    {
        if (fence.Handle == 0)
            return true;
        Result result = _api.GetFenceStatus(_device.Device, fence);
        if (result == Result.Success)
            return true;
        return false;
    }

    private void PublishCompletedMarker(Fence fence, bool force)
    {
        if (fence.Handle == 0 || force)
            return;
        VulkanResourceLifetimeTracker tracker = _resources.Lifetime.Tracker;
        lock (tracker.SyncRoot)
            for (int i = tracker.LifetimeSubmissions.Count - 1; i >= 0; i--)
                if (tracker.LifetimeSubmissions[i].FenceHandle == unchecked((ulong)fence.Handle))
                {
                    VulkanLifetimeSubmission submission = tracker.LifetimeSubmissions[i];
                    tracker.MarkQueueSequenceCompletedNoLock(submission.QueueDomain, submission.QueueSequence);
                    tracker.LifetimeSubmissions.RemoveAt(i);
                }
    }

    private void DestroyMarker(Fence fence)
    {
        if (fence.Handle != 0)
            _api.DestroyFence(_device.Device, fence, null);
    }

    private void DrainCompletedDependencies()
    {
        for (int slot = 0; slot < _resources.Lifetime.Retirement.CommandBuffers.Length; slot++)
        {
            _services.DrainRetiredDesktopCommandBuffers(slot);
            _resources.DrainRetiredDescriptorSets(_api, _device.Device, slot, int.MaxValue);
            _resources.DrainRetiredDescriptorPools(_api, _device.Device, slot, int.MaxValue);
            _resources.DrainRetiredFramebuffers(_api, _device.Device, slot, int.MaxValue);
            _resources.DrainRetiredImages(_api, _device.Device, slot, int.MaxValue);
        }
    }

    private bool HasLiveDependencies(RetiredSwapchainGeneration generation)
    {
        for (int i = 0; i < generation.ImageViews.Length; i++)
            if (_resources.Lifetime.ImageViews.LiveHandles.ContainsKey(generation.ImageViews[i].Handle))
                return true;
        lock (_resources.Lifetime.Tracker.SyncRoot)
            for (int i = 0; i < generation.Framebuffers.Length; i++)
                if (_resources.Lifetime.Tracker.ResourceLifetimes.TryGetValue(new(ObjectType.Framebuffer, generation.Framebuffers[i].Handle), out VulkanResourceLifetimeRecord? lifetime) &&
                    (lifetime.State & EVulkanResourceLifetimeState.Destroyed) == 0)
                    return true;
        return false;
    }

    private void DestroyGeneration(RetiredSwapchainGeneration generation, bool force)
    {
        DestroyRenderPass(generation.ClearRenderPass, force);
        DestroyRenderPass(generation.LoadRenderPass, force);
        for (int i = 0; i < generation.PresentBridgeSemaphores.Length; i++)
            if (generation.PresentBridgeSemaphores[i].Handle != 0)
                _api.DestroySemaphore(_device.Device, generation.PresentBridgeSemaphores[i], null);
        if (generation.Swapchain.Handle != 0)
        {
            if (generation.StreamlineProxy &&
                !NvidiaDlssManager.Native.TryDestroyProxySwapchain(
                    _output.CaptureStreamlineDeviceBinding(_device),
                    generation.Swapchain,
                    out string failureReason))
            {
                Debug.RenderingError(
                    "NVIDIA DLSS frame generation failed to destroy retired proxy swapchain cleanly ({0}). Falling back to VK_KHR_swapchain destruction.",
                    failureReason);
                RequireSwapchainExtension().DestroySwapchain(_device.Device, generation.Swapchain, null);
            }
            else if (!generation.StreamlineProxy)
                RequireSwapchainExtension().DestroySwapchain(_device.Device, generation.Swapchain, null);
        }
        for (int i = 0; i < generation.Images.Length; i++)
            _resources.CompleteDetachedExternalResourceDestruction(ObjectType.Image, generation.Images[i].Handle, i < generation.ImageLifetimeGenerations.Length ? generation.ImageLifetimeGenerations[i] : 0, force);
    }

    private void DestroyRenderPass(RenderPass renderPass, bool force)
    {
        if (renderPass.Handle == 0)
            return;
        _resources.UnregisterRenderPass(renderPass);
        _api.DestroyRenderPass(_device.Device, renderPass, null);
        _resources.CompleteDetachedExternalResourceDestruction(ObjectType.RenderPass, renderPass.Handle, _resources.GetPublishedGeneration(ObjectType.RenderPass, renderPass.Handle), force);
    }
}
