using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> _liveImageViewHandles = new();

    internal void TrackLiveImageView(ImageView imageView, string owner = "unknown")
    {
        if (imageView.Handle != 0)
            _liveImageViewHandles[imageView.Handle] = owner;
    }

    internal bool IsLiveImageView(ImageView imageView)
        => imageView.Handle != 0 && _liveImageViewHandles.ContainsKey(imageView.Handle);

    internal bool TryBeginDestroyImageView(ImageView imageView, string owner)
    {
        if (imageView.Handle == 0)
            return false;

        if (_liveImageViewHandles.TryRemove(imageView.Handle, out _))
            return true;

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

            ImageView imageView = new() { Handle = pair.Key };
            Debug.Vulkan(
                "[Vulkan] Destroying remaining tracked image view 0x{0:X} owner={1} during renderer shutdown.",
                pair.Key,
                owner);
            Api!.DestroyImageView(device, imageView, null);
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
