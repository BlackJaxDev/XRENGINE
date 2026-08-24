using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Image = Silk.NET.Vulkan.Image;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the complete mutable desktop WSI generation. The fields are internal so
/// native calls can use <c>ref</c>/<c>out</c> without renderer-owned mirrors.
/// Mutation remains serialized by the output/frame-loop protocols.
/// </summary>
internal sealed class VulkanDesktopOutputState
{
    internal Format PreferredFormat = Format.B8G8R8A8Srgb;
    internal ColorSpaceKHR PreferredColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr;
    internal PresentModeKHR PreferredPresentMode = PresentModeKHR.MailboxKhr;
    internal PresentModeKHR FallbackPresentMode = PresentModeKHR.FifoKhr;
    internal KhrSwapchain? SwapchainExtension;
    internal SwapchainKHR Swapchain;
    internal Image[]? Images;
    internal ImageView[]? ImageViews;
    internal Framebuffer[]? Framebuffers;
    internal Semaphore[]? PresentBridgeSemaphores;
    internal ulong[]? ImageTimelineValues;
    internal bool[]? ImageEverPresented;

    internal bool IsImageEverPresented(uint imageIndex)
        => ImageEverPresented is not null &&
           imageIndex < ImageEverPresented.Length &&
           ImageEverPresented[imageIndex];
    internal bool[]? ImageHasValidPresentedContent;
    internal uint LastPresentedImageIndex;
    internal ulong LastPresentedFrameNumber;
    internal bool StreamlineFrameGenerationActive;
    internal bool StreamlineFrameGenerationIncludesDlss;
    internal Format ImageFormat;
    internal ColorSpaceKHR ImageColorSpace;
    internal Extent2D Extent;
    internal ulong Generation;
    internal VulkanSwapchainDepthResources? DepthResources;
    internal object DepthMutationGate { get; } = new();
    internal int RecreateInProgress;
    internal bool Maintenance1Enabled;
    internal bool PresentScalingActive;
    internal SurfacePresentScalingCapabilitiesEXT PresentScalingCapabilities;

    internal bool IsPresentScalingExtentSupported(
        uint swapchainWidth,
        uint swapchainHeight)
    {
        if (!PresentScalingActive ||
            swapchainWidth == 0 ||
            swapchainHeight == 0)
        {
            return false;
        }

        Extent2D min = PresentScalingCapabilities.MinScaledImageExtent;
        Extent2D max = PresentScalingCapabilities.MaxScaledImageExtent;
        return swapchainWidth >= min.Width &&
            swapchainHeight >= min.Height &&
            swapchainWidth <= max.Width &&
            swapchainHeight <= max.Height;
    }
        }
