using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Maths;
using XREngine.Data.Geometry;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const string GetSurfaceCapabilities2ExtensionName = "VK_KHR_get_surface_capabilities2";
    private const string SurfaceMaintenance1ExtensionName = "VK_EXT_surface_maintenance1";
    private const string SwapchainMaintenance1ExtensionName = "VK_EXT_swapchain_maintenance1";

    private bool _surfacePresentScalingInstanceExtensionsEnabled;
    private bool _swapchainMaintenance1Enabled;
    private bool _swapchainPresentScalingActive;
    private SurfacePresentScalingCapabilitiesEXT _swapchainPresentScalingCapabilities;

    private bool QuerySwapchainMaintenance1FeatureSupport()
    {
        PhysicalDeviceSwapchainMaintenance1FeaturesEXT supportedFeatures = new()
        {
            SType = StructureType.PhysicalDeviceSwapchainMaintenance1FeaturesExt,
        };
        PhysicalDeviceFeatures2 features = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &supportedFeatures,
        };

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features);
        return supportedFeatures.SwapchainMaintenance1;
    }

    private bool TryGetSwapchainPresentScalingConfiguration(
        PresentModeKHR presentMode,
        Extent2D imageExtent,
        out SwapchainPresentScalingCreateInfoEXT createInfo,
        out SurfacePresentScalingCapabilitiesEXT capabilities)
    {
        createInfo = default;
        capabilities = default;
        if (!_surfacePresentScalingInstanceExtensionsEnabled || !_swapchainMaintenance1Enabled)
            return false;

        if (!Api!.TryGetInstanceExtension<KhrGetSurfaceCapabilities2>(
                instance,
                out KhrGetSurfaceCapabilities2? surfaceCapabilities2))
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.PresentScaling.SurfaceCapabilities2Unavailable.{GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[Vulkan] Present scaling was enabled but VK_KHR_get_surface_capabilities2 could not be loaded. Falling back to strict swapchain extents.");
            return false;
        }

        SurfacePresentModeEXT surfacePresentMode = new()
        {
            SType = StructureType.SurfacePresentModeExt,
            PresentMode = presentMode,
        };
        PhysicalDeviceSurfaceInfo2KHR surfaceInfo = new()
        {
            SType = StructureType.PhysicalDeviceSurfaceInfo2Khr,
            PNext = &surfacePresentMode,
            Surface = surface,
        };
        SurfacePresentScalingCapabilitiesEXT queriedCapabilities = new()
        {
            SType = StructureType.SurfacePresentScalingCapabilitiesExt,
        };
        SurfaceCapabilities2KHR surfaceCapabilities = new()
        {
            SType = StructureType.SurfaceCapabilities2Khr,
            PNext = &queriedCapabilities,
        };

        Result queryResult = surfaceCapabilities2.GetPhysicalDeviceSurfaceCapabilities2(
            _physicalDevice,
            &surfaceInfo,
            &surfaceCapabilities);
        if (queryResult != Result.Success)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.PresentScaling.CapabilityQueryFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[Vulkan] Present-scaling capability query failed ({0}). Falling back to strict swapchain extents.",
                queryResult);
            return false;
        }

        capabilities = queriedCapabilities;
        bool supportsStretch =
            (capabilities.SupportedPresentScaling & PresentScalingFlagsKHR.StretchBitExt) != 0;
        bool imageExtentSupported =
            imageExtent.Width >= capabilities.MinScaledImageExtent.Width &&
            imageExtent.Height >= capabilities.MinScaledImageExtent.Height &&
            imageExtent.Width <= capabilities.MaxScaledImageExtent.Width &&
            imageExtent.Height <= capabilities.MaxScaledImageExtent.Height;
        if (!supportsStretch || !imageExtentSupported)
        {
            Debug.Vulkan(
                "[Vulkan] Present scaling unavailable for this swapchain. Stretch={0} ImageExtent={1}x{2} ScaledRange={3}x{4}-{5}x{6}.",
                supportsStretch,
                imageExtent.Width,
                imageExtent.Height,
                capabilities.MinScaledImageExtent.Width,
                capabilities.MinScaledImageExtent.Height,
                capabilities.MaxScaledImageExtent.Width,
                capabilities.MaxScaledImageExtent.Height);
            return false;
        }

        createInfo = new SwapchainPresentScalingCreateInfoEXT
        {
            SType = StructureType.SwapchainPresentScalingCreateInfoExt,
            ScalingBehavior = PresentScalingFlagsKHR.StretchBitExt,
            PresentGravityX = PresentGravityFlagsKHR.CenteredBitExt,
            PresentGravityY = PresentGravityFlagsKHR.CenteredBitExt,
        };
        return true;
    }

    private bool IsSwapchainPresentScalingExtentSupported(
        uint swapchainWidth,
        uint swapchainHeight)
    {
        if (!_swapchainPresentScalingActive ||
            swapchainWidth == 0 ||
            swapchainHeight == 0)
        {
            return false;
        }

        // The scaled-image range constrains the swapchain image extent, while
        // the compositor owns the changing destination surface extent.
        Extent2D min = _swapchainPresentScalingCapabilities.MinScaledImageExtent;
        Extent2D max = _swapchainPresentScalingCapabilities.MaxScaledImageExtent;
        return swapchainWidth >= min.Width &&
            swapchainHeight >= min.Height &&
            swapchainWidth <= max.Width &&
            swapchainHeight <= max.Height;
    }

    /// <summary>
    /// Keeps layout and projection in the live presentation space while converting
    /// the final window composite to the fixed swapchain image that WSI will stretch.
    /// Scaling shared rectangle edges independently avoids gaps between split viewports.
    /// </summary>
    internal override BoundingRectangle MapWindowPresentationRegionToBackbuffer(BoundingRectangle region)
    {
        if (!_swapchainPresentScalingActive || !XRWindow.IsInteractiveResizeInProgress)
            return region;

        Vector2D<int> presentationExtent = XRWindow.ResizeExtents.PresentationExtent;
        if (presentationExtent.X <= 0 || presentationExtent.Y <= 0)
            presentationExtent = XRWindow.EffectiveFramebufferSize;

        return ScalePresentationRegionToBackbuffer(
            region,
            presentationExtent,
            new Vector2D<int>((int)swapChainExtent.Width, (int)swapChainExtent.Height));
    }

    internal static BoundingRectangle ScalePresentationRegionToBackbuffer(
        BoundingRectangle region,
        Vector2D<int> presentationExtent,
        Vector2D<int> backbufferExtent)
    {
        if (presentationExtent.X <= 0 ||
            presentationExtent.Y <= 0 ||
            backbufferExtent.X <= 0 ||
            backbufferExtent.Y <= 0 ||
            (presentationExtent.X == backbufferExtent.X &&
             presentationExtent.Y == backbufferExtent.Y))
        {
            return region;
        }

        int left = ScalePresentationEdge(region.X, presentationExtent.X, backbufferExtent.X);
        int right = ScalePresentationEdge(
            checked(region.X + region.Width),
            presentationExtent.X,
            backbufferExtent.X);
        int bottom = ScalePresentationEdge(region.Y, presentationExtent.Y, backbufferExtent.Y);
        int top = ScalePresentationEdge(
            checked(region.Y + region.Height),
            presentationExtent.Y,
            backbufferExtent.Y);

        left = Math.Clamp(left, 0, backbufferExtent.X);
        right = Math.Clamp(right, left, backbufferExtent.X);
        bottom = Math.Clamp(bottom, 0, backbufferExtent.Y);
        top = Math.Clamp(top, bottom, backbufferExtent.Y);
        return new BoundingRectangle(left, bottom, right - left, top - bottom);
    }

    private static int ScalePresentationEdge(int edge, int presentationExtent, int backbufferExtent)
        => (int)Math.Round(
            edge * (double)backbufferExtent / presentationExtent,
            MidpointRounding.AwayFromZero);
}
