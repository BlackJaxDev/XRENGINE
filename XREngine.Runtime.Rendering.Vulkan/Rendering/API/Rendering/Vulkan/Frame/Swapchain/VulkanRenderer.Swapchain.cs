using System;
using System.Diagnostics;
using System.Threading;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using XREngine.Rendering.DLSS;
using Image = Silk.NET.Vulkan.Image;
using Format = Silk.NET.Vulkan.Format;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    internal void CreateDesktopFinalOutput()
        => CreateAllSwapChainObjects();

    internal void DestroyDesktopFinalOutput()
        => DestroyAllSwapChainObjects();

    private static readonly SurfaceFormatPreference[] HDRSurfacePreferences =
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

    private static readonly SurfaceFormatPreference[] SDRSurfacePreferences =
    [
        new(Format.B8G8R8A8Srgb, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
        new(Format.R8G8B8A8Srgb, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
        new(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
        new(Format.R8G8B8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
    ];

    private static readonly SurfaceFormatPreference[] DlssFrameGenerationSdrSurfacePreferences =
    [
        // Streamline DLSS-G rejects sRGB VkFormats for back buffers. The nonlinear
        // color space still applies the expected SDR transfer function.
        new(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
        new(Format.R8G8B8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
    ];

    public Format PreferredFormat
    {
        get => OutputRuntime.Desktop.PreferredFormat;
        set => OutputRuntime.Desktop.PreferredFormat = value;
    }
    public ColorSpaceKHR PreferredColorSpace
    {
        get => OutputRuntime.Desktop.PreferredColorSpace;
        set => OutputRuntime.Desktop.PreferredColorSpace = value;
    }
    public PresentModeKHR PreferredPresentMode
    {
        get => OutputRuntime.Desktop.PreferredPresentMode;
        set => OutputRuntime.Desktop.PreferredPresentMode = value;
    }
    public PresentModeKHR FallbackPresentMode
    {
        get => OutputRuntime.Desktop.FallbackPresentMode;
        set => OutputRuntime.Desktop.FallbackPresentMode = value;
    }
    private static readonly PresentModeKHR[] DlssFrameGenerationPresentModePreferences =
    [
        PresentModeKHR.MailboxKhr,
        PresentModeKHR.ImmediateKhr,
    ];

    //private VkBuffer<UniformBufferObject>[]? uniformBuffers;

    private VulkanSwapchainDepthResources? CurrentSwapchainDepthResources
        => Volatile.Read(ref OutputRuntime.Desktop.DepthResources);

    private Image _swapchainDepthImage
        => CurrentSwapchainDepthResources?.Image ?? default;

    private ImageView _swapchainDepthView
        => CurrentSwapchainDepthResources?.View ?? default;

    private Format _swapchainDepthFormat
        => CurrentSwapchainDepthResources?.Format ?? default;

    private ImageAspectFlags _swapchainDepthAspect
        => CurrentSwapchainDepthResources?.Aspect ?? default;

    internal bool StreamlineFrameGenerationSwapchainActive => OutputRuntime.Desktop.StreamlineFrameGenerationActive;
    internal bool StreamlineFrameGenerationSwapchainIncludesDlss => OutputRuntime.Desktop.StreamlineFrameGenerationIncludesDlss;
    internal uint SwapchainImageCount => (uint)(OutputRuntime.Desktop.Images?.Length ?? 0);
    internal Format SwapchainImageFormat => OutputRuntime.Desktop.ImageFormat;
    internal Extent2D SwapchainExtent => OutputRuntime.Desktop.Extent;

    internal bool RecreateDesktopSwapchainCore()
    {
        if (Interlocked.CompareExchange(ref OutputRuntime.Desktop.RecreateInProgress, 1, 0) != 0)
            return false;

        try
        {
            WindowSurfaceSnapshot snapshot = XRWindow.LatestWindowSurfaceSnapshot;
            Vector2D<int> framebufferSize = snapshot.HasValidFramebufferExtent
                ? snapshot.FramebufferExtent
                : DesktopWsiOutput.EffectiveFramebufferSize;
            Vector2D<int> windowSize = snapshot.HasValidClientExtent
                ? snapshot.ClientExtent
                : XRWindow.EffectiveWindowSize;

            if (snapshot.IsMinimized ||
                framebufferSize.X <= 0 ||
                framebufferSize.Y <= 0 ||
                windowSize.X <= 0 ||
                windowSize.Y <= 0)
            {
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.RecreateDeferredForZeroSurface",
                    TimeSpan.FromMilliseconds(500),
                    "[Vulkan] Deferring swapchain recreation because the surface is not presentable. SnapshotSeq={0} Minimized={1} Framebuffer={2}x{3} Window={4}x{5}",
                    snapshot.Sequence,
                    snapshot.IsMinimized,
                    framebufferSize.X,
                    framebufferSize.Y,
                    windowSize.X,
                    windowSize.Y);
                return false;
            }

            if (!IsSurfacePresentableForSwapchain(out string surfaceUnavailableReason))
            {
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.RecreateDeferredForSurfaceCapabilities",
                    TimeSpan.FromMilliseconds(500),
                    "[Vulkan] Deferring swapchain recreation because the surface capabilities are not presentable. Reason={0}",
                    surfaceUnavailableReason);
                return false;
            }

            DisableStreamlineFrameGenerationBeforeSwapchainMutation("swapchain recreation");
            if (!TryPrepareSwapchainRetirementMarkers(
                    out Fence graphicsMarkerFence,
                    out Fence presentMarkerFence))
                return false;

            SwapchainKHR oldSwapchain = OutputRuntime.Desktop.Swapchain;
            Image[] oldImages = OutputRuntime.Desktop.Images ?? [];
            ImageView[] oldImageViews = OutputRuntime.Desktop.ImageViews ?? [];
            Framebuffer[] oldFramebuffers = OutputRuntime.Desktop.Framebuffers ?? [];
            Semaphore[] oldPresentBridgeSemaphores = OutputRuntime.Desktop.PresentBridgeSemaphores ?? [];
            bool oldStreamlineProxy = OutputRuntime.Desktop.StreamlineFrameGenerationActive;
            uint oldWidth = OutputRuntime.Desktop.Extent.Width;
            uint oldHeight = OutputRuntime.Desktop.Extent.Height;

            DestroySwapchainImGuiResources();
            DestroyStreamlineUiResources();
            DestroyDepth();
            DestroySwapchainCommandBuffers();
            DestroyFrameBuffers();
            (RenderPass oldClearRenderPass, RenderPass oldLoadRenderPass) =
                DetachSwapchainRenderPassesForRetirement();
            DestroyImageViews();
            DestroyDescriptorPool();
            ulong[] oldImageLifetimeGenerations =
                DetachSwapchainImageLifetimesForHandleReuse(oldImages);

            OutputRuntime.Desktop.Swapchain = default;
            OutputRuntime.Desktop.Images = null;
            OutputRuntime.Desktop.ImageEverPresented = null;
            OutputRuntime.Desktop.ImageHasValidPresentedContent = null;
            OutputRuntime.Desktop.PresentBridgeSemaphores = null;
            OutputRuntime.Desktop.StreamlineFrameGenerationActive = false;
            OutputRuntime.Desktop.StreamlineFrameGenerationIncludesDlss = false;

            RetiredSwapchainGeneration retiredGeneration = new(
                oldSwapchain,
                oldImages,
                oldImageLifetimeGenerations,
                oldImageViews,
                oldFramebuffers,
                oldPresentBridgeSemaphores,
                oldClearRenderPass,
                oldLoadRenderPass,
                graphicsMarkerFence,
                presentMarkerFence,
                oldStreamlineProxy,
                oldWidth,
                oldHeight,
                Stopwatch.GetTimestamp());
            try
            {
                CreateAllSwapChainObjects(oldSwapchain);
                OutputRuntime.Desktop.PresentBridgeSemaphores = CreatePresentBridgeSemaphores(OutputRuntime.Desktop.Images?.Length ?? MAX_FRAMES_IN_FLIGHT);
                ReserveOpenXrFrameDataSlotsIfRequired("swapchain recreation");
                EnsureSwapchainTimelineState();
                return true;
            }
            catch (Exception ex) when (IsExpectedVulkanImageAllocationDeferral(ex))
            {
                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.RecreateDeferredForAllocatorPressure",
                    TimeSpan.FromMilliseconds(500),
                    "[Vulkan] Deferring swapchain recreation under allocator pressure. Reason={0}",
                    ex.Message);
                DestroyAllSwapChainObjects();
                return false;
            }
            finally
            {
                QueueRetiredSwapchainGeneration(retiredGeneration);
            }
        }
        finally
        {
            Interlocked.Exchange(ref OutputRuntime.Desktop.RecreateInProgress, 0);
        }
    }
    private void DestroyAllSwapChainObjects()
    {
        DestroySwapchainImGuiResources();
        DestroyStreamlineUiResources();
        DestroyDepth();
        DestroySwapchainCommandBuffers();
        DestroyFrameBuffers();
        // FBO render passes are independent of the swapchain (they describe FBO
        // attachment formats, not swapchain images) and must NOT be destroyed
        // during swapchain recreation.  VkFrameBuffer objects cache the render
        // pass handle returned by GetOrCreateFrameBufferRenderPass(); destroying
        // the cache here leaves them holding stale VkRenderPass handles, which
        // causes ExecutionEngineException in CmdBeginRenderPass on the next frame.
        // Cleanup is handled separately during full renderer shutdown.
        //_testModel?.Destroy();
        DestroyRenderPasses();
        DestroyImageViews();
        DestroySwapChain();
        //DestroyUniformBuffers();
        DestroyDescriptorPool();
    }

    private void DisableStreamlineFrameGenerationBeforeSwapchainMutation(string reason)
    {
        if (!OutputRuntime.Desktop.StreamlineFrameGenerationActive)
            return;

        var viewports = XRWindow.Viewports;
        if (viewports.Count == 0)
        {
            Debug.RenderingWarning(
                "NVIDIA DLSS frame generation is active, but no viewport was available to send DLSSGMode.Off before {0}.",
                reason);
            return;
        }

        for (int i = 0; i < viewports.Count; i++)
        {
            XRViewport viewport = viewports[i];
            if (NvidiaDlssManager.Native.TryDisableFrameGeneration(this, viewport, out string failureReason))
                continue;

            Debug.RenderingError(
                "NVIDIA DLSS frame generation could not be disabled before {0} for viewport {1}: {2}",
                reason,
                viewport.Index,
                failureReason);
        }
    }

    private void DrainStreamlineFrameGenerationDisableBeforePresent()
    {
        if (!OutputRuntime.Desktop.StreamlineFrameGenerationActive || NvidiaDlssManager.IsFrameGenerationRequested)
            return;

        var viewports = XRWindow.Viewports;
        for (int i = 0; i < viewports.Count; i++)
        {
            XRViewport viewport = viewports[i];
            if (NvidiaDlssManager.Native.TryDrainFrameGenerationDisableForPresent(this, viewport, out string failureReason))
                continue;

            Debug.RenderingError(
                "NVIDIA DLSS frame generation could not finish its disable drain for viewport {0}: {1}",
                viewport.Index,
                failureReason);
        }
    }

    private void CreateAllSwapChainObjects(SwapchainKHR oldSwapchain = default)
    {
        CreateSwapChain(oldSwapchain);
        CreateImageViews();
        CreateStreamlineUiResources();

        Format depthFormat = FindDepthFormat();
        ImageAspectFlags depthAspect = IsDepthStencilFormat(depthFormat)
            ? (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)
            : ImageAspectFlags.DepthBit;

        CreateDepth(depthFormat, depthAspect);
        CreateRenderPass();
        //_testModel?.Generate();
        CreateFramebuffers();
        //CreateUniformBuffers();
        CreateDescriptorPool();
        CreateDescriptorSets();
        CreateCommandBuffers();
    }

    private void DestroyDepth()
    {
        VulkanSwapchainDepthResources? resources;
        lock (OutputRuntime.Desktop.DepthMutationGate)
            resources = Interlocked.Exchange(ref OutputRuntime.Desktop.DepthResources, null);

        if (resources is null)
            return;

        Debug.Vulkan(
            "[Vulkan] Detached swapchain depth target for retirement. Image=0x{0:X} Generation={1} Extent={2}x{3}.",
            resources.Image.Handle,
            GetCurrentVulkanResourceGeneration(ObjectType.Image, resources.Image.Handle),
            resources.Extent.Width,
            resources.Extent.Height);
        RetireImageResources(new RetiredImageResources(
            resources.Image,
            resources.Memory,
            resources.View,
            [],
            default,
            0));
    }

    private void CreateDepth(Format depthFormat, ImageAspectFlags depthAspect)
    {
        lock (OutputRuntime.Desktop.DepthMutationGate)
        {
            if (CurrentSwapchainDepthResources is not null)
                return;

            ImageCreateInfo imageInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Extent = new Extent3D(OutputRuntime.Desktop.Extent.Width, OutputRuntime.Desktop.Extent.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Format = depthFormat,
                Tiling = ImageTiling.Optimal,
                InitialLayout = ImageLayout.Undefined,
                Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, // TransferSrcBit for depth readback
                Samples = SampleCountFlags.Count1Bit,
                SharingMode = SharingMode.Exclusive,
            };

            if (CreateVulkanImageTracked(ref imageInfo, out Image depthImage, "Swapchain.Depth") != Result.Success)
                throw new Exception("Failed to create swapchain depth image.");

            ClearTrackedImageLayouts(depthImage);
            VulkanMemoryAllocation allocation = AllocateImageMemoryWithFallback(depthImage, MemoryPropertyFlags.DeviceLocalBit);
            ResourceRuntime.Allocations.Images.Allocations[depthImage.Handle] = allocation;
            DeviceMemory depthMemory = allocation.Memory;

            if (Api!.BindImageMemory(_deviceContext.Device, depthImage, depthMemory, allocation.Offset) != Result.Success)
            {
                ResourceRuntime.Allocations.Images.Allocations.TryRemove(depthImage.Handle, out _);
                DestroyVulkanImageImmediateTracked(depthImage, "Swapchain.Depth.BindFailure");
                FreeMemoryAllocation(allocation);
                throw new Exception("Failed to bind swapchain depth memory.");
            }

            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = depthImage,
                ViewType = ImageViewType.Type2D,
                Format = depthFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = depthAspect,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                }
            };

            if (Api!.CreateImageView(_deviceContext.Device, ref viewInfo, null, out ImageView depthView) != Result.Success)
            {
                ResourceRuntime.Allocations.Images.Allocations.TryRemove(depthImage.Handle, out _);
                DestroyVulkanImageImmediateTracked(depthImage, "Swapchain.Depth.ViewFailure");
                FreeMemoryAllocation(allocation);
                throw new Exception("Failed to create swapchain depth view.");
            }

            TrackLiveImageView(depthView, in viewInfo, "Swapchain.Depth");

            VulkanSwapchainDepthResources resources = new(
                depthImage,
                depthMemory,
                depthView,
                depthFormat,
                depthAspect,
                OutputRuntime.Desktop.Extent);
            Volatile.Write(ref OutputRuntime.Desktop.DepthResources, resources);
            Debug.Vulkan(
                "[Vulkan] Published swapchain depth target. Image=0x{0:X} Generation={1} Extent={2}x{3}.",
                depthImage.Handle,
                GetCurrentVulkanResourceGeneration(ObjectType.Image, depthImage.Handle),
                OutputRuntime.Desktop.Extent.Width,
                OutputRuntime.Desktop.Extent.Height);
        }
    }

    private static bool IsDepthStencilFormat(Format format)
        => format is Format.D32SfloatS8Uint or Format.D24UnormS8Uint or Format.D16UnormS8Uint;

    private Format FindSupportedFormat(IEnumerable<Format> candidates, ImageTiling tiling, FormatFeatureFlags features)
    {
        foreach (var format in candidates)
        {
            Api!.GetPhysicalDeviceFormatProperties(_deviceContext.PhysicalDevice, format, out var props);
            if ((tiling == ImageTiling.Linear && (props.LinearTilingFeatures & features) == features) ||
                (tiling == ImageTiling.Optimal && (props.OptimalTilingFeatures & features) == features))
                return format;
        }

        throw new Exception("failed to find supported format!");
    }

    private Format FindDepthFormat()
        => FindSupportedFormat([Format.D32Sfloat, Format.D32SfloatS8Uint, Format.D24UnormS8Uint], ImageTiling.Optimal, FormatFeatureFlags.DepthStencilAttachmentBit);

    private void DestroySwapChain()
    {
        if (OutputRuntime.Desktop.Swapchain.Handle == 0)
            return;

        DisableStreamlineFrameGenerationBeforeSwapchainMutation("swapchain destruction");

        if (OutputRuntime.Desktop.StreamlineFrameGenerationActive)
        {
            if (!NvidiaDlssManager.Native.TryDestroyProxySwapchain(this, OutputRuntime.Desktop.Swapchain, out string failureReason))
            {
                Debug.RenderingError(
                    $"NVIDIA DLSS frame generation failed to destroy the Streamline proxy swapchain cleanly ({failureReason}). Attempting direct VK_KHR_swapchain destruction for teardown cleanup.");
                OutputRuntime.Desktop.SwapchainExtension!.DestroySwapchain(_deviceContext.Device, OutputRuntime.Desktop.Swapchain, null);
            }
        }
        else
        {
            OutputRuntime.Desktop.SwapchainExtension!.DestroySwapchain(_deviceContext.Device, OutputRuntime.Desktop.Swapchain, null);
        }

        OutputRuntime.Desktop.Swapchain = default;
        OutputRuntime.Desktop.Images = null;
        OutputRuntime.Desktop.ImageEverPresented = null;
        OutputRuntime.Desktop.ImageHasValidPresentedContent = null;
        OutputRuntime.Desktop.StreamlineFrameGenerationActive = false;
        OutputRuntime.Desktop.StreamlineFrameGenerationIncludesDlss = false;
    }

    private void CreateSwapChain(SwapchainKHR oldSwapchain = default)
    {
        var swapChainSupport = QuerySwapChainSupport(_deviceContext.PhysicalDevice);
        var surfaceFormat = ChooseSwapSurfaceFormat(swapChainSupport.Formats);
        var presentMode = ChoosePresentMode(swapChainSupport.PresentModes);
        if (!TryChooseSwapExtent(swapChainSupport.Capabilities, out Extent2D extent, out string unavailableReason))
            throw new InvalidOperationException($"Cannot create Vulkan swapchain while the surface is not presentable: {unavailableReason}");

        var imageCount = swapChainSupport.Capabilities.MinImageCount + 1;
        if (swapChainSupport.Capabilities.MaxImageCount > 0 && imageCount > swapChainSupport.Capabilities.MaxImageCount)
            imageCount = swapChainSupport.Capabilities.MaxImageCount;

        SwapchainCreateInfoKHR createInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _outputRuntime.Surface,

            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
        };

        var indices = _deviceContext.QueueFamilies;
        var queueFamilyIndices = stackalloc[] { indices.GraphicsFamilyIndex!.Value, indices.PresentFamilyIndex!.Value };

        if (indices.GraphicsFamilyIndex != indices.PresentFamilyIndex)
        {
            createInfo = createInfo with
            {
                ImageSharingMode = SharingMode.Concurrent,
                QueueFamilyIndexCount = 2,
                PQueueFamilyIndices = queueFamilyIndices,
            };
        }
        else
            createInfo.ImageSharingMode = SharingMode.Exclusive;

        createInfo = createInfo with
        {
            PreTransform = swapChainSupport.Capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,

            OldSwapchain = oldSwapchain
        };

        bool usePresentScaling = TryGetSwapchainPresentScalingConfiguration(
            presentMode,
            extent,
            out SwapchainPresentScalingCreateInfoEXT presentScalingCreateInfo,
            out SurfacePresentScalingCapabilitiesEXT presentScalingCapabilities);
        if (usePresentScaling)
            createInfo.PNext = &presentScalingCreateInfo;

        if (!Api!.TryGetDeviceExtension(_deviceContext.Instance, _deviceContext.Device, out OutputRuntime.Desktop.SwapchainExtension))
            throw new NotSupportedException("VK_KHR_swapchain extension not found.");

        // Streamline's proxy swapchain is a provisioned device capability, not a live
        // frame-generation option. Keeping it for the renderer lifetime lets the UI
        // toggle DLSS-G without destroying the swapchain and all generation-bound
        // descriptors. Frame generation itself remains disabled until requested.
        bool requestStreamlineFrameGeneration = _outputRuntime._streamlineFrameGenerationProvisioned;
        bool requestStreamlineFrameGenerationDlss = requestStreamlineFrameGeneration
            && _outputRuntime._streamlineDlssProvisioned;
        Result createResult;
        if (requestStreamlineFrameGeneration)
        {
            if (!NvidiaDlssManager.Native.TryCreateProxySwapchain(this, ref createInfo, requestStreamlineFrameGenerationDlss, out OutputRuntime.Desktop.Swapchain, out createResult, out string failureReason))
            {
                if (NvidiaDlssManager.IsFrameGenerationRequested)
                {
                    throw new InvalidOperationException(
                        $"Requested NVIDIA DLSS frame generation could not create a Streamline proxy swapchain: {failureReason}");
                }

                Debug.RenderingWarning(
                    "[Vulkan] Optional DLSS-G proxy-swapchain provisioning failed; creating a direct Vulkan swapchain and disabling the live DLSS-G toggle. Reason={0}",
                    failureReason);
                _outputRuntime._streamlineFrameGenerationProvisioned = false;
                requestStreamlineFrameGeneration = false;
                requestStreamlineFrameGenerationDlss = false;
                createResult = OutputRuntime.Desktop.SwapchainExtension!.CreateSwapchain(_deviceContext.Device, ref createInfo, null, out OutputRuntime.Desktop.Swapchain);
            }
        }
        else
        {
            createResult = OutputRuntime.Desktop.SwapchainExtension!.CreateSwapchain(_deviceContext.Device, ref createInfo, null, out OutputRuntime.Desktop.Swapchain);
        }

        if (createResult != Result.Success)
            throw new InvalidOperationException($"Failed to create swap chain ({createResult}){(requestStreamlineFrameGeneration ? " through Streamline for NVIDIA DLSS frame generation" : string.Empty)}.");

        OutputRuntime.Desktop.Generation++;

        OutputRuntime.Desktop.PresentScalingActive = usePresentScaling;
        OutputRuntime.Desktop.PresentScalingCapabilities = usePresentScaling
            ? presentScalingCapabilities
            : default;
        Debug.Vulkan(
            "[Vulkan] Swapchain present scaling: active={0} behavior={1} imageExtent={2}x{3} scaledRange={4}x{5}-{6}x{7}.",
            OutputRuntime.Desktop.PresentScalingActive,
            usePresentScaling ? presentScalingCreateInfo.ScalingBehavior : PresentScalingFlagsKHR.None,
            extent.Width,
            extent.Height,
            presentScalingCapabilities.MinScaledImageExtent.Width,
            presentScalingCapabilities.MinScaledImageExtent.Height,
            presentScalingCapabilities.MaxScaledImageExtent.Width,
            presentScalingCapabilities.MaxScaledImageExtent.Height);

        OutputRuntime.Desktop.StreamlineFrameGenerationActive = requestStreamlineFrameGeneration;
        OutputRuntime.Desktop.StreamlineFrameGenerationIncludesDlss = requestStreamlineFrameGenerationDlss;

        Result getImagesResult;
        if (OutputRuntime.Desktop.StreamlineFrameGenerationActive)
        {
            if (!NvidiaDlssManager.Native.TryGetProxySwapchainImages(this, OutputRuntime.Desktop.Swapchain, ref imageCount, null, out getImagesResult, out string failureReason))
                throw new InvalidOperationException($"Requested NVIDIA DLSS frame generation could not query Streamline proxy swapchain images: {failureReason}");
        }
        else
        {
            getImagesResult = OutputRuntime.Desktop.SwapchainExtension!.GetSwapchainImages(_deviceContext.Device, OutputRuntime.Desktop.Swapchain, ref imageCount, null);
        }

        if (getImagesResult != Result.Success)
            throw new InvalidOperationException($"Failed to query swapchain image count ({getImagesResult}){(OutputRuntime.Desktop.StreamlineFrameGenerationActive ? " through Streamline" : string.Empty)}.");

        if (imageCount == 0)
            throw new InvalidOperationException("Swapchain image count was zero.");

        OutputRuntime.Desktop.Images = new Image[imageCount];
        ResourceRuntime.Descriptors.EnsureFrameSlotCountFloor(checked((int)imageCount));
        OutputRuntime.Desktop.ImageEverPresented = new bool[imageCount];
        OutputRuntime.Desktop.ImageHasValidPresentedContent = new bool[imageCount];
        fixed (Image* swapChainImagesPtr = OutputRuntime.Desktop.Images)
        {
            if (OutputRuntime.Desktop.StreamlineFrameGenerationActive)
            {
                if (!NvidiaDlssManager.Native.TryGetProxySwapchainImages(this, OutputRuntime.Desktop.Swapchain, ref imageCount, swapChainImagesPtr, out getImagesResult, out string failureReason))
                    throw new InvalidOperationException($"Requested NVIDIA DLSS frame generation could not fetch Streamline proxy swapchain images: {failureReason}");
            }
            else
            {
                getImagesResult = OutputRuntime.Desktop.SwapchainExtension!.GetSwapchainImages(_deviceContext.Device, OutputRuntime.Desktop.Swapchain, ref imageCount, swapChainImagesPtr);
            }
        }

        if (getImagesResult != Result.Success)
            throw new InvalidOperationException($"Failed to fetch swapchain images ({getImagesResult}){(OutputRuntime.Desktop.StreamlineFrameGenerationActive ? " through Streamline" : string.Empty)}.");

        for (int i = 0; i < OutputRuntime.Desktop.Images.Length; i++)
            ClearTrackedImageLayouts(OutputRuntime.Desktop.Images[i]);

        OutputRuntime.Desktop.ImageFormat = surfaceFormat.Format;
        OutputRuntime.Desktop.ImageColorSpace = surfaceFormat.ColorSpace;
        OutputRuntime.Desktop.Extent = extent;
        Debug.VulkanWarningEvery(
            "Vulkan.Swapchain.SelectedSurfaceFormat",
            TimeSpan.FromSeconds(10),
            "[Vulkan] Swapchain surface selected: format={0} colorSpace={1} presentMode={2} extent={3}x{4} images={5} imguiSrgbPassthroughEmulation={6}",
            OutputRuntime.Desktop.ImageFormat,
            OutputRuntime.Desktop.ImageColorSpace,
            presentMode,
            OutputRuntime.Desktop.Extent.Width,
            OutputRuntime.Desktop.Extent.Height,
            imageCount,
            ShouldEmulateOpenGlImGuiSrgbPassthrough());
        if (OutputRuntime.Desktop.StreamlineFrameGenerationActive)
        {
            Debug.Rendering(
                "[Vulkan] NVIDIA DLSS frame generation provisioned: swapchain created through Streamline proxy. format={0} extent={1}x{2} images={3}",
                OutputRuntime.Desktop.ImageFormat,
                OutputRuntime.Desktop.Extent.Width,
                OutputRuntime.Desktop.Extent.Height,
                imageCount);
        }
        OnSwapchainExtentChanged(OutputRuntime.Desktop.Extent);
    }

    private SurfaceFormatKHR ChooseSwapSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> availableFormats)
    {
        bool requestHdr = DesktopWsiOutput.PreferHdrOutput;

        if (_deviceContext.MutableCapabilities._supportsSwapchainColorspace
            && requestHdr
            && _outputRuntime._streamlineFrameGenerationProvisioned
            && TrySelectSurfaceFormat(availableFormats, DlssFrameGenerationHdrSurfacePreferences, out SurfaceFormatKHR dlssFrameGenerationHdrFormat))
        {
            PreferredFormat = dlssFrameGenerationHdrFormat.Format;
            PreferredColorSpace = dlssFrameGenerationHdrFormat.ColorSpace;
            return dlssFrameGenerationHdrFormat;
        }

        if (_outputRuntime._streamlineFrameGenerationProvisioned
            && TrySelectSurfaceFormat(availableFormats, DlssFrameGenerationSdrSurfacePreferences, out SurfaceFormatKHR dlssFrameGenerationSdrFormat))
        {
            PreferredFormat = dlssFrameGenerationSdrFormat.Format;
            PreferredColorSpace = dlssFrameGenerationSdrFormat.ColorSpace;
            return dlssFrameGenerationSdrFormat;
        }

        if (_outputRuntime._streamlineFrameGenerationProvisioned)
        {
            throw new NotSupportedException(
                "NVIDIA DLSS frame generation requires an RGB10 HDR10 or UNORM SDR Vulkan swapchain format; this surface exposes no Streamline-compatible back-buffer format.");
        }

        if (_deviceContext.MutableCapabilities._supportsSwapchainColorspace &&
            requestHdr &&
            TrySelectSurfaceFormat(availableFormats, HDRSurfacePreferences, out SurfaceFormatKHR hdrFormat))
        {
            PreferredFormat = hdrFormat.Format;
            PreferredColorSpace = hdrFormat.ColorSpace;
            return hdrFormat;
        }

        if (TrySelectSurfaceFormat(availableFormats, SDRSurfacePreferences, out SurfaceFormatKHR sdrFormat))
        {
            PreferredFormat = sdrFormat.Format;
            PreferredColorSpace = sdrFormat.ColorSpace;
            return sdrFormat;
        }

        return availableFormats[0];
    }

    private static bool TrySelectSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> availableFormats, SurfaceFormatPreference[] preferences, out SurfaceFormatKHR chosen)
    {
        foreach (SurfaceFormatPreference preference in preferences)
        {
            foreach (SurfaceFormatKHR availableFormat in availableFormats)
            {
                if (availableFormat.Format == preference.Format && availableFormat.ColorSpace == preference.ColorSpace)
                {
                    chosen = availableFormat;
                    return true;
                }
            }
        }

        chosen = default;
        return false;
    }

    private PresentModeKHR ChoosePresentMode(IReadOnlyList<PresentModeKHR> availablePresentModes)
    {
        if (_outputRuntime._streamlineFrameGenerationProvisioned)
        {
            for (int preferenceIndex = 0; preferenceIndex < DlssFrameGenerationPresentModePreferences.Length; preferenceIndex++)
            {
                PresentModeKHR preferred = DlssFrameGenerationPresentModePreferences[preferenceIndex];
                foreach (PresentModeKHR availablePresentMode in availablePresentModes)
                {
                    if (availablePresentMode == preferred)
                        return availablePresentMode;
                }
            }

            Debug.RenderingWarningEvery(
                "Vulkan.DLSSG.PresentMode.FifoFallback",
                TimeSpan.FromSeconds(5),
                "NVIDIA DLSS frame generation requested, but the Vulkan surface did not expose Mailbox or Immediate present modes. Falling back to {0}; Vulkan VSync with DLSS-G is not supported by Streamline.",
                FallbackPresentMode);
        }

        foreach (var availablePresentMode in availablePresentModes)
            if (availablePresentMode == PreferredPresentMode)
                return availablePresentMode;

        return FallbackPresentMode;
    }

    private bool IsSurfacePresentableForSwapchain(out string reason)
    {
        if (_outputRuntime.SurfaceApi is null || _outputRuntime.Surface.Handle == 0 || _deviceContext.PhysicalDevice.Handle == 0)
        {
            reason =
                $"surface query is not initialized (extension={_outputRuntime.SurfaceApi is not null}, " +
                $"surface=0x{_outputRuntime.Surface.Handle:X}, physicalDevice=0x{_deviceContext.PhysicalDevice.Handle:X})";
            return false;
        }

        var swapChainSupport = QuerySwapChainSupport(_deviceContext.PhysicalDevice);
        if (swapChainSupport.Formats.Length == 0)
        {
            reason = "surface reported no formats";
            return false;
        }

        if (swapChainSupport.PresentModes.Length == 0)
        {
            reason = "surface reported no present modes";
            return false;
        }

        return TryChooseSwapExtent(swapChainSupport.Capabilities, out _, out reason);
    }

    private bool TryChooseSwapExtent(
        SurfaceCapabilitiesKHR capabilities,
        out Extent2D extent,
        out string reason)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            extent = capabilities.CurrentExtent;
            if (extent.Width == 0 || extent.Height == 0)
            {
                reason = $"surface current extent is {extent.Width}x{extent.Height}";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        Vector2D<int> framebufferSize = DesktopWsiOutput.EffectiveFramebufferSize;
        Vector2D<int> windowSize = Window.Size;

        if ((framebufferSize.X <= 0 || framebufferSize.Y <= 0) &&
            (windowSize.X <= 0 || windowSize.Y <= 0))
        {
            extent = default;
            reason = $"window/framebuffer extents are {windowSize.X}x{windowSize.Y}/{framebufferSize.X}x{framebufferSize.Y}";
            return false;
        }

        // Prefer the larger non-zero size signal. Some desktop configurations report a
        // framebuffer size that reflects logical coordinates while the visible window is larger.
        // Using the larger signal prevents persistent right/bottom black borders.
        uint width = (uint)Math.Max(Math.Max(framebufferSize.X, windowSize.X), 1);
        uint height = (uint)Math.Max(Math.Max(framebufferSize.Y, windowSize.Y), 1);

        extent = new()
        {
            Width = Math.Clamp(width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Height = Math.Clamp(height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height)
        };

        if (extent.Width == 0 || extent.Height == 0)
        {
            reason =
                $"surface clamp produced {extent.Width}x{extent.Height} from window/framebuffer " +
                $"{windowSize.X}x{windowSize.Y}/{framebufferSize.X}x{framebufferSize.Y}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private SwapChainSupportDetails QuerySwapChainSupport(PhysicalDevice physicalDevice)
    {
        var details = new SwapChainSupportDetails();

        _outputRuntime.SurfaceApi!.GetPhysicalDeviceSurfaceCapabilities(physicalDevice, _outputRuntime.Surface, out details.Capabilities);

        uint formatCount = 0;
        _outputRuntime.SurfaceApi.GetPhysicalDeviceSurfaceFormats(physicalDevice, _outputRuntime.Surface, ref formatCount, null);

        if (formatCount != 0)
        {
            details.Formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatsPtr = details.Formats)
            {
                _outputRuntime.SurfaceApi.GetPhysicalDeviceSurfaceFormats(physicalDevice, _outputRuntime.Surface, ref formatCount, formatsPtr);
            }
        }
        else
        {
            details.Formats = [];
        }

        uint presentModeCount = 0;
        _outputRuntime.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(physicalDevice, _outputRuntime.Surface, ref presentModeCount, null);

        if (presentModeCount != 0)
        {
            details.PresentModes = new PresentModeKHR[presentModeCount];
            fixed (PresentModeKHR* formatsPtr = details.PresentModes)
            {
                _outputRuntime.SurfaceApi.GetPhysicalDeviceSurfacePresentModes(physicalDevice, _outputRuntime.Surface, ref presentModeCount, formatsPtr);
            }
        }
        else
            details.PresentModes = [];

        return details;
    }


}
