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
    private readonly object _internedImageViewsLock = new();
    private readonly Dictionary<VulkanImageViewStructuralKey, InternedImageViewEntry> _internedImageViews = new();
    private readonly Dictionary<ulong, VulkanImageViewStructuralKey> _internedImageViewKeysByHandle = new();

    private readonly record struct VulkanImageViewStructuralKey(
        ulong ImageHandle,
        ulong ImageGeneration,
        ImageViewCreateFlags Flags,
        ImageViewType ViewType,
        Format Format,
        ComponentSwizzle R,
        ComponentSwizzle G,
        ComponentSwizzle B,
        ComponentSwizzle A,
        ImageAspectFlags AspectMask,
        uint BaseMipLevel,
        uint LevelCount,
        uint BaseArrayLayer,
        uint LayerCount);

    private sealed class InternedImageViewEntry(ImageView view)
    {
        public ImageView View { get; } = view;
        public int ReferenceCount { get; set; } = 1;
    }

    private VulkanImageViewStructuralKey BuildImageViewStructuralKey(in ImageViewCreateInfo createInfo)
        => new(
            createInfo.Image.Handle,
            GetCurrentVulkanResourceGeneration(ObjectType.Image, createInfo.Image.Handle),
            createInfo.Flags,
            createInfo.ViewType,
            createInfo.Format,
            createInfo.Components.R,
            createInfo.Components.G,
            createInfo.Components.B,
            createInfo.Components.A,
            createInfo.SubresourceRange.AspectMask,
            createInfo.SubresourceRange.BaseMipLevel,
            createInfo.SubresourceRange.LevelCount,
            createInfo.SubresourceRange.BaseArrayLayer,
            createInfo.SubresourceRange.LayerCount);

    internal bool TryAcquireInternedImageView(
        in ImageViewCreateInfo createInfo,
        string owner,
        out ImageView imageView)
    {
        VulkanImageViewStructuralKey key = BuildImageViewStructuralKey(createInfo);
        lock (_internedImageViewsLock)
        {
            if (_internedImageViews.TryGetValue(key, out InternedImageViewEntry? existing) &&
                IsLiveImageViewBackedByLiveImage(existing.View))
            {
                existing.ReferenceCount++;
                imageView = existing.View;
                return true;
            }

            if (existing is not null)
            {
                _internedImageViews.Remove(key);
                _internedImageViewKeysByHandle.Remove(existing.View.Handle);
            }

            ImageViewCreateInfo mutableCreateInfo = createInfo;
            if (Api!.CreateImageView(device, ref mutableCreateInfo, null, out imageView) != Result.Success)
                return false;

            TrackLiveImageView(imageView, in mutableCreateInfo, owner);
            _internedImageViews[key] = new InternedImageViewEntry(imageView);
            _internedImageViewKeysByHandle[imageView.Handle] = key;
            return true;
        }
    }

    internal bool IsLiveImageViewStructurallyEquivalent(
        ImageView imageView,
        in ImageViewCreateInfo createInfo)
    {
        if (imageView.Handle == 0 || !IsLiveImageViewBackedByLiveImage(imageView))
            return false;

        if (!_descriptorHeapImageViewCreateInfos.TryGetValue(imageView.Handle, out ImageViewCreateInfo existing))
            return false;

        return BuildImageViewStructuralKey(existing) == BuildImageViewStructuralKey(createInfo);
    }

    internal bool ReleaseInternedImageView(ImageView imageView)
    {
        if (imageView.Handle == 0)
            return false;

        lock (_internedImageViewsLock)
        {
            if (!_internedImageViewKeysByHandle.TryGetValue(imageView.Handle, out VulkanImageViewStructuralKey key) ||
                !_internedImageViews.TryGetValue(key, out InternedImageViewEntry? entry))
            {
                return true;
            }

            entry.ReferenceCount--;
            if (entry.ReferenceCount > 0)
                return false;

            // Keep an unreferenced structural view dormant while its backing allocation
            // remains live. Rotating target slots can then reacquire the same handle
            // without a create/retire cycle. Backing-image retirement purges it.
            entry.ReferenceCount = 0;
            return false;
        }
    }

    private void RetireImageViewsForBackingImage(ulong imageHandle)
    {
        if (imageHandle == 0)
            return;

        List<ImageView>? retiredViews = null;
        HashSet<ulong>? retiredHandles = null;
        lock (_internedImageViewsLock)
        {
            foreach ((VulkanImageViewStructuralKey key, InternedImageViewEntry entry) in _internedImageViews)
            {
                if (key.ImageHandle != imageHandle)
                    continue;

                retiredViews ??= [];
                retiredHandles ??= [];
                retiredViews.Add(entry.View);
                retiredHandles.Add(entry.View.Handle);
            }

            if (retiredViews is not null)
            {
                for (int i = 0; i < retiredViews.Count; i++)
                {
                    ImageView view = retiredViews[i];
                    if (_internedImageViewKeysByHandle.Remove(view.Handle, out VulkanImageViewStructuralKey key))
                        _internedImageViews.Remove(key);
                }
            }
        }

        // Texture wrappers also create non-interned primary/attachment views. The
        // image owns every one of those Vulkan objects even when a wrapper caches
        // the handle for a planner context. Retire the complete backing-image set
        // so a dormant wrapper cannot keep its destroyed physical image pending.
        foreach (KeyValuePair<ulong, ImageViewCreateInfo> pair in _descriptorHeapImageViewCreateInfos)
        {
            if (pair.Value.Image.Handle != imageHandle ||
                !_liveImageViewHandles.ContainsKey(pair.Key) ||
                (retiredHandles is not null && retiredHandles.Contains(pair.Key)))
            {
                continue;
            }

            retiredViews ??= [];
            retiredHandles ??= [];
            retiredHandles.Add(pair.Key);
            retiredViews.Add(new ImageView { Handle = pair.Key });
        }

        if (retiredViews is null)
            return;

        for (int i = 0; i < retiredViews.Count; i++)
        {
            RetireImageResources(new RetiredImageResources(
                default,
                default,
                retiredViews[i],
                [],
                default,
                0));
        }
    }

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
            lock (_internedImageViewsLock)
            {
                if (_internedImageViewKeysByHandle.Remove(pair.Key, out VulkanImageViewStructuralKey key))
                    _internedImageViews.Remove(key);
            }
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
