namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded per-operation ownership of interned image views used by a sealed
/// native-compute closure. The primary plan releases these references when
/// its frame operation is retired.
/// </summary>
internal sealed class VulkanAdvancedNativeComputeClosureStorage
{
    // Identity, metadata, depth, HDR, velocity, reactive, diagnostics, and
    // the shared AO view, four raw MSAA visibility views, plus four optional
    // DDGI surface exports.
    private readonly VulkanInternedImageViewReference[] _views = new VulkanInternedImageViewReference[17];
    private int _count;
    private VulkanImageResourceService? _images;

    internal bool TryTrack(VulkanImageResourceService images, in VulkanInternedImageViewReference reference)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (!reference.IsValid || _count == _views.Length ||
            _images is not null && !ReferenceEquals(_images, images))
            return false;
        _images = images;
        _views[_count++] = reference;
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
            images.ReleaseInternedView(_views[index]);
            _views[index] = default;
        }
        _count = 0;
        _images = null;
    }
}
