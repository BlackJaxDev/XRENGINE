using System;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
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

    struct SwapChainSupportDetails
    {
        public SurfaceCapabilitiesKHR Capabilities;
        public SurfaceFormatKHR[] Formats;
        public PresentModeKHR[] PresentModes;
    }

    private KhrSwapchain? khrSwapChain;
    private SwapchainKHR swapChain;
    private Image[]? swapChainImages;
    private uint _lastPresentedImageIndex;
    //private VkBuffer<UniformBufferObject>[]? uniformBuffers;
    private Format swapChainImageFormat;
    private Extent2D swapChainExtent;

    private Image _swapchainDepthImage;
    private DeviceMemory _swapchainDepthMemory;
    private ImageView _swapchainDepthView;
    private Format _swapchainDepthFormat;
    private ImageAspectFlags _swapchainDepthAspect;

    private void RecreateSwapChain()
    {
        Vector2D<int> framebufferSize = Window!.FramebufferSize;
        while (framebufferSize.X == 0 || framebufferSize.Y == 0)
        {
            framebufferSize = Window.FramebufferSize;
            Window.DoEvents();
        }

        DeviceWaitIdle();
        DestroyAllSwapChainObjects();
        CreateAllSwapChainObjects();

        imagesInFlight = new Fence[swapChainImages!.Length];
    }
    private void DestroyAllSwapChainObjects()
    {
        DestroyDebugTriangleResources();
        DestroyDepth();
        DestroyCommandBuffers();
        DestroyFrameBuffers();
        DestroyFrameBufferRenderPasses();
        //_testModel?.Destroy();
        DestroyRenderPasses();
        DestroyImageViews();
        DestroySwapChain();
        //DestroyUniformBuffers();
        DestroyDescriptorPool();
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
            Api!.DestroyImageView(device, _swapchainDepthView, null);
            _swapchainDepthView = default;
        }

        if (_swapchainDepthImage.Handle != 0)
        {
            Api!.DestroyImage(device, _swapchainDepthImage, null);
            _swapchainDepthImage = default;
        }

        if (_swapchainDepthMemory.Handle != 0)
        {
            Api!.FreeMemory(device, _swapchainDepthMemory, null);
            _swapchainDepthMemory = default;
        }
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
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        if (Api!.CreateImage(device, ref imageInfo, null, out _swapchainDepthImage) != Result.Success)
            throw new Exception("Failed to create swapchain depth image.");

        Api!.GetImageMemoryRequirements(device, _swapchainDepthImage, out MemoryRequirements memRequirements);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };

        if (Api!.AllocateMemory(device, ref allocInfo, null, out _swapchainDepthMemory) != Result.Success)
            throw new Exception("Failed to allocate swapchain depth memory.");

        if (Api!.BindImageMemory(device, _swapchainDepthImage, _swapchainDepthMemory, 0) != Result.Success)
            throw new Exception("Failed to bind swapchain depth memory.");

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
        => khrSwapChain!.DestroySwapchain(device, swapChain, null);

    private void CreateSwapChain()
    {
        var swapChainSupport = QuerySwapChainSupport(_physicalDevice);
        var surfaceFormat = ChooseSwapSurfaceFormat(swapChainSupport.Formats);
        var presentMode = ChoosePresentMode(swapChainSupport.PresentModes);
        var extent = ChooseSwapExtent(swapChainSupport.Capabilities);

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
        
        if (khrSwapChain!.CreateSwapchain(device, ref createInfo, null, out swapChain) != Result.Success)
            throw new Exception("Failed to create swap chain.");
        
        khrSwapChain.GetSwapchainImages(device, swapChain, ref imageCount, null);
        swapChainImages = new Image[imageCount];
        fixed (Image* swapChainImagesPtr = swapChainImages)
        {
            khrSwapChain.GetSwapchainImages(device, swapChain, ref imageCount, swapChainImagesPtr);
        }

        swapChainImageFormat = surfaceFormat.Format;
        swapChainExtent = extent;
        OnSwapchainExtentChanged(swapChainExtent);
    }

    private SurfaceFormatKHR ChooseSwapSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> availableFormats)
    {
        bool requestHdr = XRWindow.PreferHDROutput;

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
        foreach (var availablePresentMode in availablePresentModes)
            if (availablePresentMode == PreferredPresentMode)
                return availablePresentMode;

        return FallbackPresentMode;
    }

    private Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
            return capabilities.CurrentExtent;
        else
        {
            var framebufferSize = Window!.FramebufferSize;

            Extent2D actualExtent = new()
            {
                Width = (uint)framebufferSize.X,
                Height = (uint)framebufferSize.Y
            };

            actualExtent.Width = Math.Clamp(actualExtent.Width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width);
            actualExtent.Height = Math.Clamp(actualExtent.Height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height);

            return actualExtent;
        }
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