using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded per-operation ownership of interned image views used by a sealed
/// native-compute closure. The primary plan releases these references when
/// its frame operation is retired.
/// </summary>
internal sealed class VulkanAdvancedNativeComputeClosureStorage
{
    // Identity, metadata, depth, HDR, velocity, reactive, diagnostics, and
    // the shared AO storage/sampled view retained by one sealed closure.
    private readonly ImageView[] _views = new ImageView[8];
    private int _count;
    private VulkanImageResourceService? _images;

    internal bool TryTrack(VulkanImageResourceService images, ImageView view)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (view.Handle == 0 || _count == _views.Length ||
            _images is not null && !ReferenceEquals(_images, images))
            return false;
        _images = images;
        _views[_count++] = view;
        return true;
    }

    /// <summary>Balances views retained by the sealed physical frame plan.</summary>
    internal void ReleaseAcquiredViews()
    {
        if (_images is { } images)
            Release(images);
    }

    internal void Release(VulkanImageResourceService images)
    {
        for (int index = _count - 1; index >= 0; --index)
        {
            _ = images.ReleaseInternedView(_views[index]);
            _views[index] = default;
        }
        _count = 0;
        _images = null;
    }
}
