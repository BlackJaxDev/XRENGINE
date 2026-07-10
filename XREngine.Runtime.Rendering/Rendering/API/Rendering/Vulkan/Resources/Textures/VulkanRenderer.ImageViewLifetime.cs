using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> _liveImageViewHandles = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, ImageViewCreateInfo> _descriptorHeapImageViewCreateInfos = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _retiringImageHandles = new();

    internal void TrackLiveImageView(ImageView imageView, string owner = "unknown")
    {
        if (imageView.Handle != 0)
        {
            _liveImageViewHandles[imageView.Handle] = owner;
            RegisterVulkanResource(ObjectType.ImageView, imageView.Handle, owner);
        }
    }

    internal void TrackLiveImageView(ImageView imageView, in ImageViewCreateInfo createInfo, string owner = "unknown")
    {
        if (imageView.Handle == 0)
            return;

        _liveImageViewHandles[imageView.Handle] = owner;
        _descriptorHeapImageViewCreateInfos[imageView.Handle] = createInfo with { PNext = null };
        RegisterVulkanImageViewResource(
            imageView,
            createInfo.Image,
            owner,
            IsExternalImageViewOwner(owner));
    }

    internal bool TryGetDescriptorHeapImageViewCreateInfo(ImageView imageView, out ImageViewCreateInfo createInfo)
    {
        if (imageView.Handle != 0 &&
            _descriptorHeapImageViewCreateInfos.TryGetValue(imageView.Handle, out createInfo))
        {
            return true;
        }

        createInfo = default;
        return false;
    }

    internal bool IsLiveImageView(ImageView imageView)
        => imageView.Handle != 0 && _liveImageViewHandles.ContainsKey(imageView.Handle);

    internal bool IsLiveImageViewBackedByLiveImage(ImageView imageView)
    {
        if (imageView.Handle == 0 ||
            !_liveImageViewHandles.TryGetValue(imageView.Handle, out string? owner))
        {
            return false;
        }

        if (!_descriptorHeapImageViewCreateInfos.TryGetValue(imageView.Handle, out ImageViewCreateInfo createInfo))
            return true;

        Image image = createInfo.Image;
        if (image.Handle == 0)
            return false;

        if (IsExternalImageViewOwner(owner))
            return true;

        return _imageAllocations.ContainsKey(image.Handle) && !_retiringImageHandles.ContainsKey(image.Handle);
    }

    internal bool TryGetImageViewBackingImage(ImageView imageView, out Image image)
    {
        if (imageView.Handle != 0 &&
            _descriptorHeapImageViewCreateInfos.TryGetValue(imageView.Handle, out ImageViewCreateInfo createInfo))
        {
            image = createInfo.Image;
            return image.Handle != 0;
        }

        image = default;
        return false;
    }

    private static bool IsExternalImageViewOwner(string owner)
        => owner.StartsWith("OpenXR.Swapchain", StringComparison.Ordinal)
        || owner.StartsWith("Swapchain.Color", StringComparison.Ordinal);

    internal bool TryBeginDestroyImageView(ImageView imageView, string owner)
    {
        if (imageView.Handle == 0)
            return false;

        VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
            ObjectType.ImageView,
            imageView.Handle,
            owner);
        if (!IsVulkanRetirementReady(ticket))
        {
            RetireImageResources(new RetiredImageResources(
                default,
                default,
                imageView,
                [],
                default,
                0));
            return false;
        }

        if (_liveImageViewHandles.TryRemove(imageView.Handle, out _))
        {
            _descriptorHeapImageViewCreateInfos.TryRemove(imageView.Handle, out _);
            CompleteVulkanResourceDestruction(ObjectType.ImageView, imageView.Handle);
            return true;
        }

        Debug.VulkanEvery(
            $"Vulkan.ImageView.SkipStaleDestroy.{GetHashCode()}.{owner}.{imageView.Handle}",
            TimeSpan.FromSeconds(5),
            "[Vulkan] Skipping stale destroy for image view 0x{0:X} in {1}; the handle is not live in renderer tracking.",
            imageView.Handle,
            owner);
        return false;
    }

    private void DestroyRemainingTrackedImageViews()
    {
        int destroyedViews = 0;
        foreach (KeyValuePair<ulong, string> pair in _liveImageViewHandles.ToArray())
        {
            if (!_liveImageViewHandles.TryRemove(pair.Key, out string? owner))
                continue;

            _descriptorHeapImageViewCreateInfos.TryRemove(pair.Key, out _);
            ImageView imageView = new() { Handle = pair.Key };
            Debug.Vulkan(
                "[Vulkan] Destroying remaining tracked image view 0x{0:X} owner={1} during renderer shutdown.",
                pair.Key,
                owner);
            Api!.DestroyImageView(device, imageView, null);
            CompleteVulkanResourceDestruction(ObjectType.ImageView, pair.Key);
            destroyedViews++;
        }

        if (destroyedViews > 0)
        {
            Debug.Vulkan(
                "[Vulkan] Destroyed {0} remaining tracked image view(s) during renderer shutdown.",
                destroyedViews);
        }
    }
}
