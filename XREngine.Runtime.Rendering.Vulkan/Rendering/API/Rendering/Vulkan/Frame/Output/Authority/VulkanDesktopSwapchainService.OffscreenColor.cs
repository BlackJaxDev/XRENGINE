using System.Threading;
using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanDesktopSwapchainService
{
    /// <summary>
    /// Returns the offscreen desktop color targets of the live WSI generation and
    /// creates them on first use. The images match the swapchain format and extent,
    /// so the copy pass is a plain image copy. Only desktop worker recording uses
    /// them, so a session that does not select it allocates no memory here.
    /// </summary>
    internal VulkanDesktopOffscreenColorResources EnsureOffscreenColorResources(int slotCount)
    {
        if (slotCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(slotCount));

        lock (_output.Desktop.DepthMutationGate)
        {
            VulkanDesktopOffscreenColorResources? existing = _output.Desktop.OffscreenColorResources;
            if (existing is not null)
            {
                if (existing.SlotCount != slotCount)
                {
                    throw new InvalidOperationException(
                        $"The desktop offscreen color targets have {existing.SlotCount} slots, but the frame loop uses {slotCount}.");
                }

                return existing;
            }

            Format format = _output.Desktop.ImageFormat;
            Extent2D extent = _output.Desktop.Extent;
            if (format == Format.Undefined || extent.Width == 0 || extent.Height == 0)
                throw new InvalidOperationException("The desktop offscreen color targets need a live swapchain format and extent.");

            Image[] images = new Image[slotCount];
            DeviceMemory[] memories = new DeviceMemory[slotCount];
            ImageView[] views = new ImageView[slotCount];
            int created = 0;
            try
            {
                for (; created < slotCount; created++)
                    CreateOffscreenColorSlot(format, extent, created, out images[created], out memories[created], out views[created]);
            }
            catch
            {
                for (int index = 0; index < created; index++)
                {
                    _resources.Images.RetireOwnedResources(
                        new RetiredImageResources(images[index], memories[index], views[index], [], default, 0),
                        "Swapchain.OffscreenColor.CreateFailure");
                }

                throw;
            }

            VulkanDesktopOffscreenColorResources resources = new(
                images,
                memories,
                views,
                format,
                extent,
                _output.Desktop.Generation);
            Volatile.Write(ref _output.Desktop.OffscreenColorResources, resources);
            Debug.Vulkan(
                "[Vulkan] Published {0} desktop offscreen color targets. Format={1} Extent={2}x{3}.",
                slotCount,
                format,
                extent.Width,
                extent.Height);
            return resources;
        }
    }

    /// <summary>
    /// Detaches the offscreen color targets and queues them for deferred
    /// destruction with the same retirement proof as the depth target.
    /// </summary>
    internal void RetireOffscreenColorResources(string owner)
    {
        VulkanDesktopOffscreenColorResources? resources;
        lock (_output.Desktop.DepthMutationGate)
            resources = Interlocked.Exchange(ref _output.Desktop.OffscreenColorResources, null);
        if (resources is null)
            return;

        for (int index = 0; index < resources.SlotCount; index++)
        {
            _resources.Images.RetireOwnedResources(
                new RetiredImageResources(
                    resources.Images[index],
                    resources.Memories[index],
                    resources.Views[index],
                    [],
                    default,
                    0),
                owner);
        }
    }

    private void CreateOffscreenColorSlot(
        Format format,
        Extent2D extent,
        int slot,
        out Image image,
        out DeviceMemory memory,
        out ImageView view)
    {
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(extent.Width, extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        string owner = $"Swapchain.OffscreenColor[{slot}]";
        if (_services.CreateVulkanImageTracked(ref imageInfo, out image, owner) != Result.Success)
            throw new InvalidOperationException($"Failed to create desktop offscreen color image {slot}.");

        VulkanMemoryAllocation allocation = _services.AllocateImageMemoryWithFallback(image, MemoryPropertyFlags.DeviceLocalBit);
        _resources.Allocations.Images.Allocations[image.Handle] = allocation;
        if (_api.BindImageMemory(_device.Device, image, allocation.Memory, allocation.Offset) != Result.Success)
        {
            _resources.Allocations.Images.Allocations.TryRemove(image.Handle, out _);
            _services.DestroyVulkanImageImmediateTracked(image, $"{owner}.BindFailure");
            _services.FreeMemoryAllocation(allocation);
            throw new InvalidOperationException($"Failed to bind desktop offscreen color memory {slot}.");
        }

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        if (_api.CreateImageView(_device.Device, ref viewInfo, null, out view) != Result.Success)
        {
            _resources.Allocations.Images.Allocations.TryRemove(image.Handle, out _);
            _services.DestroyVulkanImageImmediateTracked(image, $"{owner}.ViewFailure");
            _services.FreeMemoryAllocation(allocation);
            throw new InvalidOperationException($"Failed to create desktop offscreen color view {slot}.");
        }

        _services.TrackLiveImageView(view, in viewInfo, owner);
        memory = allocation.Memory;
    }
}
