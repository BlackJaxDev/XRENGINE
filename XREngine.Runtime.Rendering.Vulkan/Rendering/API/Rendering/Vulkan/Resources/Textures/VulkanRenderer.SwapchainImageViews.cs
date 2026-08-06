using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{

    private void DestroyImageViews()
    {
        if (OutputRuntime.Desktop.ImageViews is null)
            return;

        foreach (var imageView in OutputRuntime.Desktop.ImageViews)
        {
            if (imageView.Handle != 0 && TryBeginDestroyImageView(imageView, "DestroySwapchainImageViews"))
                Api!.DestroyImageView(_deviceContext.Device, imageView, null);
        }

        OutputRuntime.Desktop.ImageViews = null;
    }

    private void CreateImageViews()
    {
        OutputRuntime.Desktop.ImageViews = new ImageView[OutputRuntime.Desktop.Images!.Length];

        for (int i = 0; i < OutputRuntime.Desktop.Images.Length; i++)
        {
            SetDebugObjectName(ObjectType.Image, OutputRuntime.Desktop.Images[i].Handle, $"Swapchain.ColorImage[{i}]");

            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = OutputRuntime.Desktop.Images[i],
                ViewType = ImageViewType.Type2D,
                Format = OutputRuntime.Desktop.ImageFormat,
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

            if (Api!.CreateImageView(_deviceContext.Device, ref createInfo, null, out OutputRuntime.Desktop.ImageViews[i]) != Result.Success)
                throw new Exception("Failed to create image views.");

            TrackLiveImageView(OutputRuntime.Desktop.ImageViews[i], in createInfo, "Swapchain.Color");
            SetDebugObjectName(ObjectType.ImageView, OutputRuntime.Desktop.ImageViews[i].Handle, $"Swapchain.ColorView[{i}]");
        }
    }
}
