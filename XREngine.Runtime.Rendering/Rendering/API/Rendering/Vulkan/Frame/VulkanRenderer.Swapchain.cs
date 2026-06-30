using System;
using System.Threading;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using XREngine.Rendering.DLSS;
using Image = Silk.NET.Vulkan.Image;
using Format = Silk.NET.Vulkan.Format;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private readonly struct SurfaceFormatPreference(Format format, ColorSpaceKHR colorSpace)
    {
        public Format Format { get; } = format;
        public ColorSpaceKHR ColorSpace { get; } = colorSpace;
    }

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

    public Format PreferredFormat { get; set; } = Format.B8G8R8A8Srgb;
    public ColorSpaceKHR PreferredColorSpace { get; set; } = ColorSpaceKHR.SpaceSrgbNonlinearKhr;
    public PresentModeKHR PreferredPresentMode { get; set; } = PresentModeKHR.MailboxKhr;
    public PresentModeKHR FallbackPresentMode { get; set; } = PresentModeKHR.FifoKhr;
    private static readonly PresentModeKHR[] DlssFrameGenerationPresentModePreferences =
    [
        PresentModeKHR.MailboxKhr,
        PresentModeKHR.ImmediateKhr,
    ];

    struct SwapChainSupportDetails
    {
        public SurfaceCapabilitiesKHR Capabilities;
        public SurfaceFormatKHR[] Formats;
        public PresentModeKHR[] PresentModes;
    }

    private KhrSwapchain? khrSwapChain;
    private SwapchainKHR swapChain;
    private Image[]? swapChainImages;
    private bool[]? _swapchainImageEverPresented;
    private uint _lastPresentedImageIndex;
    private bool _streamlineFrameGenerationSwapchainActive;
    //private VkBuffer<UniformBufferObject>[]? uniformBuffers;
    private Format swapChainImageFormat;
    private ColorSpaceKHR swapChainImageColorSpace;
    private Extent2D swapChainExtent;

    private Image _swapchainDepthImage;
    private DeviceMemory _swapchainDepthMemory;
    private ImageView _swapchainDepthView;
    private Format _swapchainDepthFormat;
    private ImageAspectFlags _swapchainDepthAspect;
    private int _recreateSwapChainInProgress;

    internal bool StreamlineFrameGenerationSwapchainActive => _streamlineFrameGenerationSwapchainActive;
    internal uint SwapchainImageCount => (uint)(swapChainImages?.Length ?? 0);
    internal Format SwapchainImageFormat => swapChainImageFormat;
    internal Extent2D SwapchainExtent => swapChainExtent;

    private bool RecreateSwapChain()
    {
        if (Interlocked.CompareExchange(ref _recreateSwapChainInProgress, 1, 0) != 0)
            return false;

        try
        {
            WindowSurfaceSnapshot snapshot = XRWindow.LatestWindowSurfaceSnapshot;
            Vector2D<int> framebufferSize = snapshot.HasValidFramebufferExtent
                ? snapshot.FramebufferExtent
                : XRWindow.EffectiveFramebufferSize;
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
            DeviceWaitIdle();
            DestroyAllSwapChainObjects();
            CreateAllSwapChainObjects();
            ReserveOpenXrFrameDataSlotsIfRequired("swapchain recreation");
            EnsureSwapchainTimelineState();
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _recreateSwapChainInProgress, 0);
        }
    }
    private void DestroyAllSwapChainObjects()
    {
        DestroySwapchainImGuiResources();
        DestroyDepth();
        DestroyCommandBuffers();
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
        if (!_streamlineFrameGenerationSwapchainActive)
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

    private void CreateAllSwapChainObjects()
    {
        CreateSwapChain();
        CreateImageViews();

        _swapchainDepthFormat = FindDepthFormat();
        _swapchainDepthAspect = IsDepthStencilFormat(_swapchainDepthFormat)
            ? (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)
            : ImageAspectFlags.DepthBit;

        CreateRenderPass();
        //_testModel?.Generate();
        CreateDepth();
        CreateFramebuffers();
        //CreateUniformBuffers();
        CreateDescriptorPool();
        CreateDescriptorSets();
        CreateCommandBuffers();
    }

    private void DestroyDepth()
    {
        if (_swapchainDepthView.Handle != 0)
        {
            if (TryBeginDestroyImageView(_swapchainDepthView, "DestroySwapchainDepth"))
                Api!.DestroyImageView(device, _swapchainDepthView, null);
            _swapchainDepthView = default;
        }

        if (_swapchainDepthImage.Handle != 0)
        {
            Api!.DestroyImage(device, _swapchainDepthImage, null);
            if (_imageAllocations.TryRemove(_swapchainDepthImage.Handle, out VulkanMemoryAllocation alloc))
                FreeMemoryAllocation(alloc);
            else if (_swapchainDepthMemory.Handle != 0)
                Api!.FreeMemory(device, _swapchainDepthMemory, null);
            _swapchainDepthImage = default;
        }
        else if (_swapchainDepthMemory.Handle != 0)
        {
            Api!.FreeMemory(device, _swapchainDepthMemory, null);
        }

        _swapchainDepthMemory = default;
    }

    private void CreateDepth()
    {
        if (_swapchainDepthImage.Handle != 0)
            return;

        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(swapChainExtent.Width, swapChainExtent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = _swapchainDepthFormat,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit, // TransferSrcBit for depth readback
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        if (Api!.CreateImage(device, ref imageInfo, null, out _swapchainDepthImage) != Result.Success)
            throw new Exception("Failed to create swapchain depth image.");

        VulkanMemoryAllocation allocation = AllocateImageMemoryWithFallback(_swapchainDepthImage, MemoryPropertyFlags.DeviceLocalBit);
        _imageAllocations[_swapchainDepthImage.Handle] = allocation;
        _swapchainDepthMemory = allocation.Memory;

        if (Api!.BindImageMemory(device, _swapchainDepthImage, _swapchainDepthMemory, allocation.Offset) != Result.Success)
        {
            _imageAllocations.TryRemove(_swapchainDepthImage.Handle, out _);
            FreeMemoryAllocation(allocation);
            throw new Exception("Failed to bind swapchain depth memory.");
        }

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _swapchainDepthImage,
            ViewType = ImageViewType.Type2D,
            Format = _swapchainDepthFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = _swapchainDepthAspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            }
        };

        if (Api!.CreateImageView(device, ref viewInfo, null, out _swapchainDepthView) != Result.Success)
            throw new Exception("Failed to create swapchain depth view.");

        TrackLiveImageView(_swapchainDepthView, "Swapchain.Depth");
    }

    private static bool IsDepthStencilFormat(Format format)
        => format is Format.D32SfloatS8Uint or Format.D24UnormS8Uint or Format.D16UnormS8Uint;

    private Format FindSupportedFormat(IEnumerable<Format> candidates, ImageTiling tiling, FormatFeatureFlags features)
    {
        foreach (var format in candidates)
        {
            Api!.GetPhysicalDeviceFormatProperties(_physicalDevice, format, out var props);
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
        if (swapChain.Handle == 0)
            return;

        DisableStreamlineFrameGenerationBeforeSwapchainMutation("swapchain destruction");

        if (_streamlineFrameGenerationSwapchainActive)
        {
            if (!NvidiaDlssManager.Native.TryDestroyProxySwapchain(this, swapChain, out string failureReason))
            {
                Debug.RenderingError(
                    $"NVIDIA DLSS frame generation failed to destroy the Streamline proxy swapchain cleanly ({failureReason}). Attempting direct VK_KHR_swapchain destruction for teardown cleanup.");
                khrSwapChain!.DestroySwapchain(device, swapChain, null);
            }
        }
        else
        {
            khrSwapChain!.DestroySwapchain(device, swapChain, null);
        }

        swapChain = default;
        swapChainImages = null;
        _swapchainImageEverPresented = null;
        _streamlineFrameGenerationSwapchainActive = false;
    }

    private void CreateSwapChain()
    {
        var swapChainSupport = QuerySwapChainSupport(_physicalDevice);
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
            Surface = surface,

            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
        };

        var indices = FamilyQueueIndices;
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

            OldSwapchain = default
        };

        if (!Api!.TryGetDeviceExtension(instance, device, out khrSwapChain))
            throw new NotSupportedException("VK_KHR_swapchain extension not found.");

        bool requestStreamlineFrameGeneration = NvidiaDlssManager.IsFrameGenerationRequested;
        Result createResult;
        if (requestStreamlineFrameGeneration)
        {
            if (!NvidiaDlssManager.Native.TryCreateProxySwapchain(this, ref createInfo, out swapChain, out createResult, out string failureReason))
                throw new InvalidOperationException($"Requested NVIDIA DLSS frame generation could not create a Streamline proxy swapchain: {failureReason}");
        }
        else
        {
            createResult = khrSwapChain!.CreateSwapchain(device, ref createInfo, null, out swapChain);
        }

        if (createResult != Result.Success)
            throw new InvalidOperationException($"Failed to create swap chain ({createResult}){(requestStreamlineFrameGeneration ? " through Streamline for NVIDIA DLSS frame generation" : string.Empty)}.");

        _streamlineFrameGenerationSwapchainActive = requestStreamlineFrameGeneration;

        Result getImagesResult;
        if (_streamlineFrameGenerationSwapchainActive)
        {
            if (!NvidiaDlssManager.Native.TryGetProxySwapchainImages(this, swapChain, ref imageCount, null, out getImagesResult, out string failureReason))
                throw new InvalidOperationException($"Requested NVIDIA DLSS frame generation could not query Streamline proxy swapchain images: {failureReason}");
        }
        else
        {
            getImagesResult = khrSwapChain!.GetSwapchainImages(device, swapChain, ref imageCount, null);
        }

        if (getImagesResult != Result.Success)
            throw new InvalidOperationException($"Failed to query swapchain image count ({getImagesResult}){(_streamlineFrameGenerationSwapchainActive ? " through Streamline" : string.Empty)}.");

        if (imageCount == 0)
            throw new InvalidOperationException("Swapchain image count was zero.");

        swapChainImages = new Image[imageCount];
        _swapchainImageEverPresented = new bool[imageCount];
        fixed (Image* swapChainImagesPtr = swapChainImages)
        {
            if (_streamlineFrameGenerationSwapchainActive)
            {
                if (!NvidiaDlssManager.Native.TryGetProxySwapchainImages(this, swapChain, ref imageCount, swapChainImagesPtr, out getImagesResult, out string failureReason))
                    throw new InvalidOperationException($"Requested NVIDIA DLSS frame generation could not fetch Streamline proxy swapchain images: {failureReason}");
            }
            else
            {
                getImagesResult = khrSwapChain!.GetSwapchainImages(device, swapChain, ref imageCount, swapChainImagesPtr);
            }
        }

        if (getImagesResult != Result.Success)
            throw new InvalidOperationException($"Failed to fetch swapchain images ({getImagesResult}){(_streamlineFrameGenerationSwapchainActive ? " through Streamline" : string.Empty)}.");

        swapChainImageFormat = surfaceFormat.Format;
        swapChainImageColorSpace = surfaceFormat.ColorSpace;
        swapChainExtent = extent;
        Debug.VulkanWarningEvery(
            "Vulkan.Swapchain.SelectedSurfaceFormat",
            TimeSpan.FromSeconds(10),
            "[Vulkan] Swapchain surface selected: format={0} colorSpace={1} presentMode={2} extent={3}x{4} images={5} imguiSrgbPassthroughEmulation={6}",
            swapChainImageFormat,
            swapChainImageColorSpace,
            presentMode,
            swapChainExtent.Width,
            swapChainExtent.Height,
            imageCount,
            ShouldEmulateOpenGlImGuiSrgbPassthrough());
        if (_streamlineFrameGenerationSwapchainActive)
        {
            Debug.Rendering(
                "[Vulkan] NVIDIA DLSS frame generation requested: swapchain created through Streamline proxy. format={0} extent={1}x{2} images={3}",
                swapChainImageFormat,
                swapChainExtent.Width,
                swapChainExtent.Height,
                imageCount);
        }
        OnSwapchainExtentChanged(swapChainExtent);
    }

    private SurfaceFormatKHR ChooseSwapSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> availableFormats)
    {
        bool requestHdr = XRWindow.PreferHDROutput;

        if (requestHdr
            && NvidiaDlssManager.IsFrameGenerationRequested
            && TrySelectSurfaceFormat(availableFormats, DlssFrameGenerationHdrSurfacePreferences, out SurfaceFormatKHR dlssFrameGenerationHdrFormat))
        {
            PreferredFormat = dlssFrameGenerationHdrFormat.Format;
            PreferredColorSpace = dlssFrameGenerationHdrFormat.ColorSpace;
            return dlssFrameGenerationHdrFormat;
        }

        if (requestHdr && TrySelectSurfaceFormat(availableFormats, HDRSurfacePreferences, out SurfaceFormatKHR hdrFormat))
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
        if (NvidiaDlssManager.IsFrameGenerationRequested)
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
        var swapChainSupport = QuerySwapChainSupport(_physicalDevice);
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

        Vector2D<int> framebufferSize = XRWindow.EffectiveFramebufferSize;
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

        khrSurface!.GetPhysicalDeviceSurfaceCapabilities(physicalDevice, surface, out details.Capabilities);

        uint formatCount = 0;
        khrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref formatCount, null);

        if (formatCount != 0)
        {
            details.Formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatsPtr = details.Formats)
            {
                khrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref formatCount, formatsPtr);
            }
        }
        else
        {
            details.Formats = [];
        }

        uint presentModeCount = 0;
        khrSurface.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref presentModeCount, null);

        if (presentModeCount != 0)
        {
            details.PresentModes = new PresentModeKHR[presentModeCount];
            fixed (PresentModeKHR* formatsPtr = details.PresentModes)
            {
                khrSurface.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref presentModeCount, formatsPtr);
            }
        }
        else
            details.PresentModes = [];
        
        return details;
    }


}
