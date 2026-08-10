using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanDesktopSwapchainService
{
    /// <summary>Creates one color view for every image in the live WSI generation.</summary>
    internal void CreateImageViews()
    {
        Image[] images = _output.Desktop.Images
            ?? throw new InvalidOperationException("Swapchain images must exist before their image views are created.");

        _output.Desktop.ImageViews = new ImageView[images.Length];
        for (int index = 0; index < images.Length; index++)
        {
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = images[index],
                ViewType = ImageViewType.Type2D,
                Format = _output.Desktop.ImageFormat,
                Components = new ComponentMapping
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity,
                },
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };

            if (_api.CreateImageView(_device.Device, ref createInfo, null, out ImageView view) != Result.Success)
                throw new InvalidOperationException("Failed to create a swapchain image view.");

            _output.Desktop.ImageViews[index] = view;
            _services.TrackLiveImageView(view, in createInfo, "Swapchain.Color");
        }
    }

    /// <summary>
    /// Releases color views before detaching the WSI generation.  The lifetime
    /// ledger refuses a destroy while a recorded command still pins the view;
    /// the following generation-retirement drain then observes the same proof.
    /// </summary>
    internal void DestroyImageViews()
    {
        ImageView[]? views = _output.Desktop.ImageViews;
        if (views is null)
            return;

        // The old primary artifacts can still be waiting on their submission
        // receipt when this method runs during a resize.  Queue every view with
        // its own lifetime ticket instead of dropping a failed immediate-destroy
        // attempt; the retirement drain then proves it is safe before native
        // destruction and releases the retired WSI generation.
        _resources.Images.RetireOwnedResources(new RetiredImageResources(
            default,
            default,
            default,
            views,
            default,
            0),
            "Swapchain.ImageViews");

        _output.Desktop.ImageViews = null;
    }
}
