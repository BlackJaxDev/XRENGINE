using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frame-operation-owned bounded backing for one immutable late-visibility
/// closure. It is allocated with the reusable payload column and therefore
/// cannot alias another output or allocate while a frame is prepared.
/// </summary>
internal sealed class VulkanAdvancedVisibilityLateClosureStorage
{
    internal const int MaxMipLevels = 16;
    internal const int MaxViews = RenderFrameViewSet.MaxViewCount;

    internal DescriptorImageInfo[] PyramidSampled { get; } =
        new DescriptorImageInfo[MaxViews * (MaxMipLevels - 1)];
    internal DescriptorImageInfo[] PyramidStorage { get; } =
        new DescriptorImageInfo[MaxViews * (MaxMipLevels - 1)];
    internal DescriptorImageInfo[] LateSampled { get; } =
        new DescriptorImageInfo[MaxViews];
    internal DescriptorImageInfo[] LateStorage { get; } =
        new DescriptorImageInfo[MaxViews];
    internal DescriptorSet[] DescriptorSets { get; } =
        new DescriptorSet[MaxViews * MaxMipLevels];
    private readonly ImageView[] _acquiredViews =
        new ImageView[MaxViews * (2 * (MaxMipLevels - 1) + 1)];
    private int _acquiredViewCount;

    internal bool TryTrackAcquiredView(ImageView view)
    {
        if (view.Handle == 0 || _acquiredViewCount >= _acquiredViews.Length)
            return false;
        _acquiredViews[_acquiredViewCount++] = view;
        return true;
    }

    /// <summary>
    /// Releases every interner acquisition made while sealing this operation.
    /// The image-view cache and native lifetime ledger retain the handle; this
    /// only balances the caller reference acquired during descriptor capture.
    /// </summary>
    internal void ReleaseAcquiredViews(VulkanImageResourceService images)
    {
        ArgumentNullException.ThrowIfNull(images);
        for (int index = _acquiredViewCount - 1; index >= 0; --index)
        {
            _ = images.ReleaseInternedView(_acquiredViews[index]);
            _acquiredViews[index] = default;
        }
        _acquiredViewCount = 0;
    }
}
