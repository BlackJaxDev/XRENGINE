using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private ImageView[]? swapChainImageViews;

    private void DestroyImageViews()
    {
        if (swapChainImageViews is null)
            return;

        foreach (var imageView in swapChainImageViews)
        {
            if (imageView.Handle != 0 && TryBeginDestroyImageView(imageView, "DestroySwapchainImageViews"))
                Api!.DestroyImageView(device, imageView, null);
        }

        swapChainImageViews = null;
    }

    private void CreateImageViews()
    {
        swapChainImageViews = new ImageView[swapChainImages!.Length];

        for (int i = 0; i < swapChainImages.Length; i++)
        {
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = swapChainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = swapChainImageFormat,
                Components =
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity,
                },
                SubresourceRange =
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                }

            };

            if (Api!.CreateImageView(device, ref createInfo, null, out swapChainImageViews[i]) != Result.Success)
                throw new Exception("Failed to create image views.");

            TrackLiveImageView(swapChainImageViews[i], "Swapchain.Color");
        }
    }
}
