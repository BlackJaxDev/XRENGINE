using Silk.NET.Core;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Output-runtime registry for detached ImGui platform-window WSI lifetimes.
/// It intentionally tracks output objects only; native window and input
/// lifetime remain owned by the UI adapter.
/// </summary>
internal sealed unsafe class VulkanImGuiPlatformWindowOutputAuthority
{
    private readonly object _gate = new();
    private readonly HashSet<VulkanImGuiPlatformWindowOutputLifetime> _active = [];

    internal void Register(VulkanImGuiPlatformWindowOutputLifetime lifetime)
    {
        lock (_gate)
            _active.Add(lifetime);
    }

    internal void Unregister(VulkanImGuiPlatformWindowOutputLifetime lifetime)
    {
        lock (_gate)
            _active.Remove(lifetime);
    }

    internal int ActiveLifetimeCount
    {
        get
        {
            lock (_gate)
                return _active.Count;
        }
    }

    internal SurfaceKHR CreateSurface(VulkanDeviceContext device, IWindow window)
        => window.VkSurface?.Create<AllocationCallbacks>(device.Instance.ToHandle(), null).ToSurface()
            ?? throw new NotSupportedException(
                "The detached ImGui window does not expose Vulkan surface services.");

    internal void DestroySurface(
        VulkanDeviceContext device,
        KhrSurface? surfaceApi,
        ref SurfaceKHR surface)
    {
        if (surface.Handle == 0)
            return;

        surfaceApi?.DestroySurface(device.Instance, surface, null);
        surface = default;
    }

    /// <summary>
    /// Creates the WSI portion of a detached ImGui viewport.  Command buffers,
    /// fences, and UI draw buffers stay with the command/UI authorities; this
    /// output authority owns only the swapchain image generation.
    /// </summary>
    internal unsafe bool TryCreateSwapchainGeneration(
        VulkanDeviceContext device,
        VulkanCommandRuntime commandRuntime,
        VulkanTargetOutputContext target,
        KhrSurface surfaceApi,
        KhrSwapchain swapchainApi,
        SurfaceKHR surface,
        Vector2D<int> framebufferSize,
        Format requiredFormat,
        ColorSpaceKHR requiredColorSpace,
        uint viewportId,
        out VulkanImGuiPlatformSwapchainGeneration generation)
    {
        generation = default;
        if (framebufferSize.X <= 0 || framebufferSize.Y <= 0)
            return false;

        ThrowIfFailed(
            surfaceApi.GetPhysicalDeviceSurfaceCapabilities(device.PhysicalDevice, surface, out SurfaceCapabilitiesKHR capabilities),
            "query detached-window surface capabilities");
        SurfaceFormatKHR surfaceFormat = ChooseSurfaceFormat(
            device.PhysicalDevice,
            surfaceApi,
            surface,
            requiredFormat,
            requiredColorSpace);
        PresentModeKHR presentMode = ChoosePresentMode(device.PhysicalDevice, surfaceApi, surface);
        Extent2D extent = ChooseExtent(capabilities, framebufferSize);
        if (extent.Width == 0 || extent.Height == 0)
            return false;
        if ((capabilities.SupportedUsageFlags & ImageUsageFlags.ColorAttachmentBit) == 0)
            throw new NotSupportedException("The detached ImGui surface does not support color-attachment swapchain images.");

        uint imageCount = Math.Max(capabilities.MinImageCount + 1, 2u);
        if (capabilities.MaxImageCount > 0)
            imageCount = Math.Min(imageCount, capabilities.MaxImageCount);

        uint graphicsFamily = device.QueueFamilies.GraphicsFamilyIndex
            ?? throw new InvalidOperationException("The Vulkan renderer has no graphics queue family.");
        uint presentFamily = device.QueueFamilies.PresentFamilyIndex
            ?? throw new InvalidOperationException("The Vulkan renderer has no presentation queue family.");
        uint* queueFamilies = stackalloc uint[2] { graphicsFamily, presentFamily };
        bool concurrent = graphicsFamily != presentFamily;
        SwapchainCreateInfoKHR createInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = concurrent ? SharingMode.Concurrent : SharingMode.Exclusive,
            QueueFamilyIndexCount = concurrent ? 2u : 0u,
            PQueueFamilyIndices = concurrent ? queueFamilies : null,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha),
            PresentMode = presentMode,
            Clipped = true,
        };

        ThrowIfFailed(swapchainApi.CreateSwapchain(device.Device, in createInfo, null, out SwapchainKHR swapchain), "create detached-window swapchain");
        try
        {
            uint actualImageCount = 0;
            ThrowIfFailed(swapchainApi.GetSwapchainImages(device.Device, swapchain, ref actualImageCount, null), "query detached-window swapchain image count");
            Image[] images = new Image[actualImageCount];
            fixed (Image* imagesPtr = images)
                ThrowIfFailed(swapchainApi.GetSwapchainImages(device.Device, swapchain, ref actualImageCount, imagesPtr), "query detached-window swapchain images");

            ImageView[] imageViews = new ImageView[images.Length];
            try
            {
                for (int index = 0; index < images.Length; index++)
                {
                    ImageViewCreateInfo viewInfo = new()
                    {
                        SType = StructureType.ImageViewCreateInfo,
                        Image = images[index],
                        ViewType = ImageViewType.Type2D,
                        Format = surfaceFormat.Format,
                        Components = new ComponentMapping(
                            ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                            ComponentSwizzle.Identity, ComponentSwizzle.Identity),
                        SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
                    };
                    ThrowIfFailed(device.Api.CreateImageView(device.Device, in viewInfo, null, out imageViews[index]), "create detached-window swapchain image view");
                    target.TrackLiveImageView(imageViews[index], in viewInfo, $"Swapchain.Color.ImGuiViewport[{viewportId:X8}]");
                    commandRuntime.ClearTrackedImageLayouts(images[index]);
                }
            }
            catch
            {
                DestroyImageViewsImmediately(device, target, imageViews, viewportId);
                throw;
            }

            generation = new VulkanImGuiPlatformSwapchainGeneration(
                swapchain, surfaceFormat.Format, surfaceFormat.ColorSpace, extent, images, imageViews);
            return true;
        }
        catch
        {
            swapchainApi.DestroySwapchain(device.Device, swapchain, null);
            throw;
        }
    }

    internal void DestroySwapchainGeneration(
        VulkanDeviceContext device,
        VulkanCommandRuntime commandRuntime,
        VulkanTargetOutputContext target,
        KhrSwapchain swapchainApi,
        SwapchainKHR swapchain,
        Image[] images,
        ImageView[] imageViews,
        uint viewportId)
    {
        DestroyImageViewsImmediately(device, target, imageViews, viewportId);
        for (int index = 0; index < images.Length; index++)
            commandRuntime.ClearTrackedImageLayouts(images[index]);
        if (swapchain.Handle != 0)
            swapchainApi.DestroySwapchain(device.Device, swapchain, null);
    }

    private static void DestroyImageViewsImmediately(
        VulkanDeviceContext device,
        VulkanTargetOutputContext target,
        ImageView[] imageViews,
        uint viewportId)
    {
        for (int index = 0; index < imageViews.Length; index++)
            if (imageViews[index].Handle != 0 &&
                target.TryBeginDestroyImageView(imageViews[index], $"ImGuiViewport.DestroySwapchainResources[{viewportId:X8}]"))
            {
                device.Api.DestroyImageView(device.Device, imageViews[index], null);
            }
    }

    private static SurfaceFormatKHR ChooseSurfaceFormat(PhysicalDevice physicalDevice, KhrSurface surfaceApi, SurfaceKHR surface, Format requiredFormat, ColorSpaceKHR requiredColorSpace)
    {
        uint count = 0;
        ThrowIfFailed(surfaceApi.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref count, null), "query detached-window surface format count");
        if (count == 0)
            throw new NotSupportedException("The detached ImGui surface exposes no Vulkan formats.");
        unsafe
        {
            SurfaceFormatKHR[] formats = new SurfaceFormatKHR[count];
            fixed (SurfaceFormatKHR* formatsPtr = formats)
                ThrowIfFailed(surfaceApi.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref count, formatsPtr), "query detached-window surface formats");
            if (formats.Length == 1 && formats[0].Format == Format.Undefined)
                return new SurfaceFormatKHR(requiredFormat, requiredColorSpace);
            for (int index = 0; index < formats.Length; index++)
                if (formats[index].Format == requiredFormat && formats[index].ColorSpace == requiredColorSpace)
                    return formats[index];
        }
        throw new NotSupportedException($"The detached ImGui surface does not support the primary swapchain format {requiredFormat}/{requiredColorSpace}; a compatible format is required to reuse the ImGui graphics pipeline.");
    }

    private static PresentModeKHR ChoosePresentMode(PhysicalDevice physicalDevice, KhrSurface surfaceApi, SurfaceKHR surface)
    {
        uint count = 0;
        ThrowIfFailed(surfaceApi.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref count, null), "query detached-window surface present mode count");
        if (count == 0)
            throw new NotSupportedException("The detached ImGui surface exposes no Vulkan present modes.");
        unsafe
        {
            PresentModeKHR[] modes = new PresentModeKHR[count];
            fixed (PresentModeKHR* modesPtr = modes)
                ThrowIfFailed(surfaceApi.GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, ref count, modesPtr), "query detached-window present modes");
            if (Array.IndexOf(modes, PresentModeKHR.MailboxKhr) >= 0)
                return PresentModeKHR.MailboxKhr;
            if (Array.IndexOf(modes, PresentModeKHR.ImmediateKhr) >= 0)
                return PresentModeKHR.ImmediateKhr;
        }
        return PresentModeKHR.FifoKhr;
    }

    private static Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities, Vector2D<int> framebufferSize)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
            return capabilities.CurrentExtent;
        uint width = (uint)Math.Max(framebufferSize.X, 1);
        uint height = (uint)Math.Max(framebufferSize.Y, 1);
        return new Extent2D(
            Math.Clamp(width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Math.Clamp(height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));
    }

    private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supported)
    {
        CompositeAlphaFlagsKHR[] preferences =
        [CompositeAlphaFlagsKHR.OpaqueBitKhr, CompositeAlphaFlagsKHR.PreMultipliedBitKhr, CompositeAlphaFlagsKHR.PostMultipliedBitKhr, CompositeAlphaFlagsKHR.InheritBitKhr];
        for (int index = 0; index < preferences.Length; index++)
            if ((supported & preferences[index]) != 0)
                return preferences[index];
        throw new NotSupportedException("The detached ImGui surface exposes no supported composite-alpha mode.");
    }

    private static void ThrowIfFailed(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}.");
    }
}

internal readonly record struct VulkanImGuiPlatformSwapchainGeneration(
    SwapchainKHR Swapchain,
    Format Format,
    ColorSpaceKHR ColorSpace,
    Extent2D Extent,
    Image[] Images,
    ImageView[] ImageViews);
