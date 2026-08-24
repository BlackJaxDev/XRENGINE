using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns the DLSS-G UI images whose lifetime exactly follows desktop WSI images.</summary>
internal sealed unsafe partial class VulkanDesktopSwapchainService
{
    internal void CreateStreamlineUiResources()
    {
        if (!_output.Desktop.StreamlineFrameGenerationActive || _output.Desktop.Images is null)
            return;

        int count = _output.Desktop.Images.Length;
        VulkanStreamlineUiOutputState ui = _output.StreamlineUi;
        ui.Images = new Image[count];
        ui.ImageMemories = new DeviceMemory[count];
        ui.ImageViews = new ImageView[count];
        ui.ImagesInitialized = new bool[count];
        try
        {
            for (int index = 0; index < count; index++)
                CreateStreamlineUiResource(index);
        }
        catch
        {
            RetireStreamlineUiResources();
            throw;
        }
    }

    internal void RetireStreamlineUiResources()
    {
        VulkanStreamlineUiOutputState ui = _output.StreamlineUi;
        if (ui.Images is not null)
            for (int index = 0; index < ui.Images.Length; index++)
                _resources.Images.RetireOwnedResources(new RetiredImageResources(
                    ui.Images[index],
                    ui.ImageMemories is not null && index < ui.ImageMemories.Length ? ui.ImageMemories[index] : default,
                    ui.ImageViews is not null && index < ui.ImageViews.Length ? ui.ImageViews[index] : default,
                    [], default, 0),
                    "Swapchain.StreamlineUi");

        ui.Images = null;
        ui.ImageMemories = null;
        ui.ImageViews = null;
        ui.ImagesInitialized = null;
    }

    private void CreateStreamlineUiResource(int index)
    {
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(_output.Desktop.Extent.Width, _output.Desktop.Extent.Height, 1),
            MipLevels = 1, ArrayLayers = 1, Format = _output.Desktop.ImageFormat,
            Tiling = ImageTiling.Optimal, InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            Samples = SampleCountFlags.Count1Bit, SharingMode = SharingMode.Exclusive,
        };
        if (_services.CreateVulkanImageTracked(ref imageInfo, out Image image, $"Swapchain.StreamlineUi[{index}]") != Result.Success)
            throw new InvalidOperationException("Failed to create a Streamline UI image.");
        VulkanMemoryAllocation allocation = _services.AllocateImageMemoryWithFallback(image, MemoryPropertyFlags.DeviceLocalBit);
        _resources.Allocations.Images.Allocations[image.Handle] = allocation;
        if (_api.BindImageMemory(_device.Device, image, allocation.Memory, allocation.Offset) != Result.Success)
            throw new InvalidOperationException("Failed to bind a Streamline UI image.");

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo, Image = image, ViewType = ImageViewType.Type2D,
            Format = _output.Desktop.ImageFormat,
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        };
        if (_api.CreateImageView(_device.Device, ref viewInfo, null, out ImageView view) != Result.Success)
            throw new InvalidOperationException("Failed to create a Streamline UI image view.");
        _services.TrackLiveImageView(view, in viewInfo, $"Swapchain.StreamlineUi[{index}]");
        _output.StreamlineUi.Images![index] = image;
        _output.StreamlineUi.ImageMemories![index] = allocation.Memory;
        _output.StreamlineUi.ImageViews![index] = view;
    }
}
