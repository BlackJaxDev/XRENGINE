using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;
internal interface IVkImageDescriptorSource
{
    /// <summary>
    /// Synchronizes a complete descriptor snapshot with native descriptor-table
    /// publication. Callers that must keep a snapshot current through a second
    /// authority commit hold this monitor across both operations.
    /// </summary>
    object DescriptorSnapshotSyncRoot => this;
    Image DescriptorImage { get; }
    DeviceMemory DescriptorMemory { get; }
    ImageView DescriptorView { get; }
    ImageViewType DescriptorViewType { get; }
    Sampler DescriptorSampler { get; }
    Format DescriptorFormat { get; }
    ImageAspectFlags DescriptorAspect { get; }
    ImageUsageFlags DescriptorUsage { get; }
    SampleCountFlags DescriptorSamples { get; }
    uint DescriptorMipLevels => 1u;
    uint DescriptorArrayLayers => 1u;
    ulong DescriptorGeneration => 0UL;
    bool IsDescriptorReady => true;
    bool TryEnsureDescriptorReadyForUse(string reason) => IsDescriptorReady;
    bool TryEnsureDescriptorReadyForUse(string reason, bool allowSynchronousUpload)
        => allowSynchronousUpload ? TryEnsureDescriptorReadyForUse(reason) : IsDescriptorReady;

    bool TryGetDescriptorSnapshot(
        ImageViewType? requestedViewType,
        ImageAspectFlags? requestedAspectMask,
        string reason,
        bool allowSynchronousUpload,
        out VkImageDescriptorSnapshot snapshot)
    {
        if (!TryEnsureDescriptorReadyForUse(reason, allowSynchronousUpload))
        {
            snapshot = default;
            return false;
        }

        ImageView view = requestedAspectMask switch
        {
            ImageAspectFlags.DepthBit => GetDepthOnlyDescriptorView(),
            ImageAspectFlags.StencilBit => GetStencilOnlyDescriptorView(),
            _ => requestedViewType is { } viewType
                ? GetDescriptorView(viewType)
                : DescriptorView
        };
        snapshot = new(
            DescriptorImage,
            DescriptorMemory,
            view,
            requestedViewType ?? DescriptorViewType,
            DescriptorSampler,
            DescriptorFormat,
            DescriptorAspect,
            DescriptorUsage,
            DescriptorSamples,
            DescriptorMipLevels,
            DescriptorArrayLayers,
            DescriptorGeneration,
            TrackedImageLayout,
            UsesAllocatorImage,
            view.Handle != 0 && IsDescriptorReady);
        return snapshot.IsReady;
    }

    /// <summary>
    /// Returns the most recently tracked <see cref="ImageLayout"/> for the backing VkImage.
    /// Implementations that do not track layout should return <see cref="ImageLayout.Undefined"/>.
    /// </summary>
    ImageLayout TrackedImageLayout => ImageLayout.Undefined;

    /// <summary>
    /// <c>true</c> when the image is borrowed from a resource-planner physical group;
    /// <c>false</c> when it owns a dedicated image allocation.
    /// </summary>
    bool UsesAllocatorImage => false;

    /// <summary>
    /// Attempts to transition a dedicated image before descriptor binding.
    /// Render-graph allocator images should leave this as <c>false</c> and rely
    /// on the command-buffer barrier planner.
    /// </summary>
    bool TryTransitionDedicatedImageLayout(ImageLayout oldLayout, ImageLayout newLayout) => false;

    /// <summary>
    /// Returns a depth-only <see cref="ImageView"/> for combined depth-stencil textures,
    /// suitable for sampled image descriptor bindings where a single depth aspect is required.
    /// Implementations that do not support this should return <c>default</c>.
    /// </summary>
    ImageView GetDepthOnlyDescriptorView() => default;

    /// <summary>
    /// Returns a stencil-only <see cref="ImageView"/> for combined depth-stencil textures,
    /// suitable for unsigned-integer stencil sampler descriptor bindings.
    /// Implementations that do not support this should return <c>default</c>.
    /// </summary>
    ImageView GetStencilOnlyDescriptorView() => default;

    /// <summary>
    /// Returns a descriptor view with the requested dimensionality when the
    /// backing image can legally expose one.
    /// </summary>
    ImageView GetDescriptorView(ImageViewType viewType)
        => viewType == DescriptorViewType ? DescriptorView : default;

    /// <summary>
    /// Returns a view for the exact mip/layer range captured by an image binding.
    /// Storage-image descriptors must not use a view spanning multiple mip levels.
    /// </summary>
    ImageView GetStorageDescriptorView(int mipLevel, bool layered, int layerIndex)
    {
        if (mipLevel > 0)
            return default;

        if (layered || (DescriptorArrayLayers <= 1u && layerIndex <= 0))
            return DescriptorView;

        return default;
    }

    /// <summary>
    /// Looks up an already-published storage view without allocating or refreshing
    /// image backing. Implementations that need a subresource view should return
    /// <c>false</c> until that exact view has been published.
    /// </summary>
    bool TryGetPublishedStorageDescriptorView(
        int mipLevel,
        bool layered,
        int layerIndex,
        out ImageView view)
    {
        view = default;
        if (mipLevel != 0 || (!layered && layerIndex > 0))
            return false;
        view = DescriptorView;
        return view.Handle != 0 && IsDescriptorReady;
    }
}
