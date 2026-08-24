using Silk.NET.Vulkan;
using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Device-lifetime image-view registry.  The service owns the single interning
/// table and publishes view/image generations into the shared lifetime tracker.
/// Native destruction remains retirement-queue work, so replacement views cannot
/// race an in-flight command buffer.
/// </summary>
internal unsafe sealed class VulkanImageResourceService(
    VulkanAllocationAuthority allocations,
    VulkanLifetimeAuthority lifetime)
{
    private VulkanImageViewLifetimeState Views => lifetime.ImageViews;
    private int _frameSlot;
    private VulkanCommandRuntime? _commandRuntime;

    /// <summary>
    /// Publishes the command authority required to close the lifetime graph when
    /// an image generation retires.
    /// </summary>
    internal void ConfigureCommandRuntime(VulkanCommandRuntime commandRuntime)
    {
        ArgumentNullException.ThrowIfNull(commandRuntime);
        if (_commandRuntime is not null && !ReferenceEquals(_commandRuntime, commandRuntime))
            throw new InvalidOperationException(
                "The Vulkan image resource service cannot be rebound to a different command runtime.");

        _commandRuntime = commandRuntime;
    }

    internal void PublishFrameSlot(int frameSlot)
    {
        if ((uint)frameSlot >= (uint)lifetime.Retirement.Images.Length)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
        Volatile.Write(ref _frameSlot, frameSlot);
    }

    /// <summary>Queues a wrapper-owned image/view generation for deferred destruction.</summary>
    internal void RetireOwnedResources(
        in RetiredImageResources resources,
        [System.Runtime.CompilerServices.CallerMemberName] string owner = "")
    {
        if (!CanQueueOwnedImageRetirement(resources.Image, resources.Memory, owner))
            return;

        ImageView primaryView = CanQueueImageViewRetirement(resources.PrimaryView, owner)
            ? resources.PrimaryView
            : default;
        ImageView[] sourceAttachments = FilterOwnedImageViewRetirementCandidates(resources.AttachmentViews, owner);
        VulkanRetirementTicket imageTicket = CaptureTicket(new(ObjectType.Image, resources.Image.Handle), owner);
        VulkanRetirementTicket ticket = imageTicket;
        if (resources.Image.Handle == 0 && resources.Memory.Handle != 0)
            ticket = ticket.Merge(lifetime.Tracker.CaptureRetirementWatermark());
        VulkanRetirementTicket primaryViewTicket = CaptureTicket(
            new(ObjectType.ImageView, primaryView.Handle), owner);
        ticket = ticket.Merge(primaryViewTicket);
        ulong[] attachmentGenerations = sourceAttachments.Length == 0 ? [] : new ulong[sourceAttachments.Length];
        for (int index = 0; index < sourceAttachments.Length; index++)
        {
            VulkanRetirementTicket attachmentTicket = CaptureTicket(
                new(ObjectType.ImageView, sourceAttachments[index].Handle), owner);
            attachmentGenerations[index] = attachmentTicket.ResourceGeneration;
            ticket = ticket.Merge(attachmentTicket);
        }
        VulkanRetirementTicket samplerTicket = CaptureTicket(
            new(ObjectType.Sampler, resources.Sampler.Handle), owner);
        ticket = ticket.Merge(samplerTicket);

        int frameSlot = Volatile.Read(ref _frameSlot);
        lock (lifetime.Retirement.SyncRoot)
        {
            Image image = resources.Image;
            DeviceMemory memory = resources.Memory;
            Sampler sampler = resources.Sampler;
            if (image.Handle != 0)
            {
                RequireCommandRuntime().ClearTrackedImageLayouts(image);
                Views.RetiringImageHandles[image.Handle] = 0;
                if (!lifetime.Retirement.AllImageHandles.Add(image.Handle))
                    image = default;
                else
                    lifetime.Retirement.ImageHandles[frameSlot].Add(image.Handle);
            }
            if (memory.Handle != 0 && !lifetime.Retirement.AllImageMemoryHandles.Add(memory.Handle))
                memory = default;
            else if (memory.Handle != 0)
                lifetime.Retirement.ImageMemoryHandles[frameSlot].Add(memory.Handle);

            primaryView = TakeImageViewForRetirement(primaryView, primaryViewTicket.ResourceGeneration, frameSlot);
            ImageView[] attachments = FilterRetiredAttachmentViews(
                sourceAttachments, attachmentGenerations, frameSlot, out ulong[] retainedAttachmentGenerations);
            if (sampler.Handle != 0 && !lifetime.Retirement.AllSamplerHandles.Add(sampler.Handle))
                sampler = default;
            else if (sampler.Handle != 0)
                lifetime.Retirement.SamplerHandles[frameSlot].Add(sampler.Handle);

            if (image.Handle == 0 && memory.Handle == 0 && primaryView.Handle == 0 &&
                attachments.Length == 0 && sampler.Handle == 0)
                return;

            lifetime.Retirement.Images[frameSlot].Add(new RetiredImageResourceEntry(
                new RetiredImageResources(image, memory, primaryView, attachments, sampler, resources.AllocatedVRAMBytes),
                ticket, imageTicket.ResourceGeneration, primaryViewTicket.ResourceGeneration,
                retainedAttachmentGenerations, samplerTicket.ResourceGeneration));
        }
    }

    /// <summary>
    /// Creates a fresh persistent image and publishes its generation before any
    /// wrapper can expose a view. This is intentionally only for CPU-owned image
    /// creation; recorded image work is carried by a transient command encoder.
    /// </summary>
    internal Result CreateOwnedImage(
        VulkanBackendObjectContext context,
        ref ImageCreateInfo createInfo,
        string owner,
        out Image image)
    {
        image = default;
        if (!context.IsDeviceOperational)
            return Result.ErrorDeviceLost;

        Result result = context.Api.CreateImage(context.Device, ref createInfo, null, out image);
        if (result == Result.Success)
        {
            lifetime.Tracker.RegisterResource(new VulkanResourceLifetimeKey(ObjectType.Image, image.Handle), owner, externallyOwned: false);
            RequireCommandRuntime().RegisterTrackedImageInitialLayouts(image, in createInfo);
        }
        return result;
    }

    internal VulkanMemoryAllocation AllocateOwnedImageMemory(
        VulkanBackendObjectContext context,
        Image image,
        MemoryPropertyFlags requiredProperties)
    {
        IVulkanMemoryAllocator allocator = RequireAllocator();
        if (allocator.TryAllocateForImage(context.Api, context.Device, image, requiredProperties, out VulkanMemoryAllocation allocation, out _))
            return allocation;

        if (requiredProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit) &&
            allocator.TryAllocateForImage(
                context.Api,
                context.Device,
                image,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out allocation,
                out _))
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanOomFallback();
            return allocation;
        }

        throw new VulkanOutOfMemoryException($"Vulkan image allocation failed. Requested={requiredProperties}", requiredProperties);
    }

    internal void RegisterOwnedImageAllocation(Image image, in VulkanMemoryAllocation allocation)
        => allocations.Images.Allocations[image.Handle] = allocation;

    internal void RemoveOwnedImageAllocation(Image image)
        => allocations.Images.Allocations.TryRemove(image.Handle, out _);

    /// <summary>Clears command-layout state through the image lifetime authority.</summary>
    internal void ClearTrackedLayouts(Image image)
    {
        if (image.Handle != 0)
            RequireCommandRuntime().ClearTrackedImageLayouts(image);
    }

    internal void DestroyUnpublishedOwnedImage(VulkanBackendObjectContext context, Image image, string owner)
    {
        if (image.Handle == 0)
            return;
        context.Api.DestroyImage(context.Device, image, null);
        allocations.Images.Allocations.TryRemove(image.Handle, out _);
        lock (lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeKey key = new(ObjectType.Image, image.Handle);
            if (lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? record))
                record.State = EVulkanResourceLifetimeState.Destroyed;
        }
    }

    internal void FreeMemory(VulkanBackendObjectContext context, in VulkanMemoryAllocation allocation)
    {
        if (allocation.Memory.Handle != 0)
            RequireAllocator().Free(context.Api, context.Device, allocation);
    }

    internal bool TryAllocatePhysicalImage(
        VulkanBackendObjectContext context,
        VulkanPhysicalImageGroup group,
        ref Image image,
        ref DeviceMemory memory,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (!context.IsDeviceOperational)
        {
            failureReason = "The Vulkan device is not operational.";
            return false;
        }
        if (image.Handle != 0)
            return true;

        ImageCreateInfo info = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = group.ResolvedExtent,
            MipLevels = Math.Max(group.MipLevels, 1u),
            ArrayLayers = Math.Max(group.Template.Layers, 1u),
            Format = group.Format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = group.Usage,
            Samples = group.Samples,
            SharingMode = SharingMode.Exclusive,
        };
        if (CreateOwnedImage(context, ref info, $"ResourcePlanner.{group.Key}", out image) != Result.Success)
        {
            failureReason = $"Failed to create Vulkan image for resource group '{group.Key}'.";
            return false;
        }

        try
        {
            VulkanMemoryAllocation allocation = AllocateOwnedImageMemory(context, image, group.MemoryProperties);
            RegisterOwnedImageAllocation(image, in allocation);
            memory = allocation.Memory;
            Result bind = context.Api.BindImageMemory(context.Device, image, memory, allocation.Offset);
            if (bind == Result.Success)
            {
                string allocationName = group.LogicalResources.Count == 1
                    ? group.LogicalResources[0].Name
                    : $"{group.Key} ({group.LogicalResources.Count} logical resources)";
                context.Resources.TrackImageAllocation(
                    context.DeviceContext,
                    image,
                    allocation,
                    allocationName,
                    "ResourcePlanner",
                    group.ResolvedExtent.Width,
                    group.ResolvedExtent.Height,
                    group.ResolvedExtent.Depth,
                    Math.Max(group.Template.Layers, 1u),
                    group.MipLevels,
                    group.Format,
                    group.Usage,
                    group.Samples);
                return true;
            }

            allocations.Images.Allocations.TryRemove(image.Handle, out _);
            DestroyUnpublishedOwnedImage(context, image, "ResourcePlanner.BindFailure");
            FreeMemory(context, in allocation);
            image = default;
            memory = default;
            failureReason = $"Failed to bind Vulkan image memory for resource group '{group.Key}'. Result={bind}.";
            return false;
        }
        catch (Exception ex)
        {
            if (image.Handle != 0)
                DestroyUnpublishedOwnedImage(context, image, "ResourcePlanner.ExceptionCleanup");
            image = default;
            memory = default;
            failureReason = ex.Message;
            return false;
        }
    }

    internal bool TryAcquireInternedView(
        VulkanBackendObjectContext context,
        in ImageViewCreateInfo createInfo,
        string owner,
        out ImageView imageView)
    {
        VulkanImageViewStructuralKey key = BuildKey(createInfo);
        lock (Views.InternGate)
        {
            if (Views.InternedViews.TryGetValue(key, out InternedImageViewEntry? existing) &&
                IsAvailableForDescriptor(existing.View))
            {
                existing.ReferenceCount++;
                imageView = existing.View;
                return true;
            }

            if (existing is not null)
            {
                Views.InternedViews.Remove(key);
                Views.InternedKeysByHandle.Remove(existing.View.Handle);
            }

            ImageViewCreateInfo mutableInfo = createInfo;
            if (context.Api.CreateImageView(context.Device, ref mutableInfo, null, out imageView) != Result.Success)
                return false;

            RegisterView(imageView, in mutableInfo, owner);
            Views.InternedViews[key] = new InternedImageViewEntry(imageView);
            Views.InternedKeysByHandle[imageView.Handle] = key;
            return true;
        }
    }

    internal bool ReleaseInternedView(ImageView imageView)
    {
        if (imageView.Handle == 0)
            return false;
        lock (Views.InternGate)
        {
            if (!Views.InternedKeysByHandle.TryGetValue(imageView.Handle, out VulkanImageViewStructuralKey key) ||
                !Views.InternedViews.TryGetValue(key, out InternedImageViewEntry? entry))
            {
                return true;
            }

            entry.ReferenceCount = Math.Max(0, entry.ReferenceCount - 1);
            return false;
        }
    }

    internal void RegisterView(ImageView imageView, in ImageViewCreateInfo createInfo, string owner)
    {
        if (imageView.Handle == 0)
            return;

        Views.LiveHandles[imageView.Handle] = owner;
        Views.DescriptorHeapCreateInfos[imageView.Handle] = createInfo with { PNext = null };
        lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.ImageView, imageView.Handle),
            owner,
            IsExternalOwner(owner));
        lock (lifetime.Tracker.SyncRoot)
            lifetime.Tracker.ImageViewBackingImages[imageView.Handle] = createInfo.Image.Handle;
    }

    internal bool IsAvailableForDescriptor(ImageView imageView)
    {
        if (!IsLiveBackedByLiveImage(imageView))
            return false;

        lock (lifetime.Tracker.SyncRoot)
        {
            return !lifetime.Tracker.ResourceLifetimes.TryGetValue(
                new VulkanResourceLifetimeKey(ObjectType.ImageView, imageView.Handle),
                out VulkanResourceLifetimeRecord? record) ||
                (record.State & (EVulkanResourceLifetimeState.PendingRetirement | EVulkanResourceLifetimeState.Destroyed)) == 0;
        }
    }

    internal bool IsLiveBackedByLiveImage(ImageView imageView)
    {
        if (imageView.Handle == 0 || !Views.LiveHandles.TryGetValue(imageView.Handle, out string? owner))
            return false;
        if (!Views.DescriptorHeapCreateInfos.TryGetValue(imageView.Handle, out ImageViewCreateInfo createInfo))
            return true;
        return IsExternalOwner(owner) ||
            (createInfo.Image.Handle != 0 &&
             allocations.Images.Allocations.ContainsKey(createInfo.Image.Handle) &&
             !Views.RetiringImageHandles.ContainsKey(createInfo.Image.Handle));
    }

    internal bool IsStructurallyEquivalent(ImageView imageView, in ImageViewCreateInfo createInfo)
        => imageView.Handle != 0 &&
           IsLiveBackedByLiveImage(imageView) &&
           Views.DescriptorHeapCreateInfos.TryGetValue(imageView.Handle, out ImageViewCreateInfo existing) &&
           BuildKey(existing) == BuildKey(createInfo);

    internal bool TryGetBackingImage(ImageView imageView, out Image image)
    {
        if (imageView.Handle != 0 && Views.DescriptorHeapCreateInfos.TryGetValue(imageView.Handle, out ImageViewCreateInfo info))
        {
            image = info.Image;
            return image.Handle != 0;
        }
        image = default;
        return false;
    }

    internal bool TryGetDescriptorHeapCreateInfo(
        ImageView imageView,
        out ImageViewCreateInfo createInfo)
    {
        if (imageView.Handle != 0 &&
            Views.DescriptorHeapCreateInfos.TryGetValue(imageView.Handle, out createInfo))
        {
            return true;
        }

        createInfo = default;
        return false;
    }

    /// <summary>
    /// Removes a view from publication once its exact retirement ticket is ready.
    /// Callers perform the native destroy after this method returns <see langword="true"/>.
    /// </summary>
    internal bool TryBeginDestroy(ImageView imageView, string owner)
    {
        if (imageView.Handle == 0)
            return false;

        VulkanRetirementTicket ticket = CaptureTicket(
            new VulkanResourceLifetimeKey(ObjectType.ImageView, imageView.Handle), owner);
        if (!RequireResourceRuntime().IsRetirementReady(ticket))
        {
            RetireOwnedResources(
                new RetiredImageResources(default, default, imageView, [], default, 0),
                owner);
            return false;
        }

        if (!Views.LiveHandles.TryRemove(imageView.Handle, out _))
            return false;

        Views.DescriptorHeapCreateInfos.TryRemove(imageView.Handle, out _);
        RequireResourceRuntime().CompleteResourceDestruction(ObjectType.ImageView, imageView.Handle);
        return true;
    }

    /// <summary>Destroys all remaining image views after the device is idle.</summary>
    internal unsafe int DestroyRemaining(Vk api, Device device)
    {
        ulong[] handles = Views.LiveHandles.Keys.ToArray();
        foreach (ulong handle in handles)
        {
            if (!Views.LiveHandles.TryRemove(handle, out _))
                continue;

            Views.DescriptorHeapCreateInfos.TryRemove(handle, out _);
            lock (Views.InternGate)
            {
                if (Views.InternedKeysByHandle.Remove(handle, out VulkanImageViewStructuralKey key))
                    Views.InternedViews.Remove(key);
            }
            api.DestroyImageView(device, new ImageView { Handle = handle }, null);
            RequireResourceRuntime().CompleteResourceDestruction(ObjectType.ImageView, handle);
        }

        return handles.Length;
    }

    private VulkanImageViewStructuralKey BuildKey(in ImageViewCreateInfo info)
        => new(
            info.Image.Handle,
            lifetime.Tracker.GetPublishedGeneration(new VulkanResourceLifetimeKey(ObjectType.Image, info.Image.Handle)),
            info.Flags, info.ViewType, info.Format,
            info.Components.R, info.Components.G, info.Components.B, info.Components.A,
            info.SubresourceRange.AspectMask, info.SubresourceRange.BaseMipLevel,
            info.SubresourceRange.LevelCount, info.SubresourceRange.BaseArrayLayer,
            info.SubresourceRange.LayerCount);

    private static bool IsExternalOwner(string owner)
        => owner.StartsWith("OpenXR.Swapchain", StringComparison.Ordinal) ||
           owner.StartsWith("Swapchain.Color", StringComparison.Ordinal);

    private IVulkanMemoryAllocator RequireAllocator()
        => allocations.Buffers.MemoryAllocator
            ?? throw new InvalidOperationException("The Vulkan memory allocator has not been initialized.");

    private bool CanQueueOwnedImageRetirement(Image image, DeviceMemory memory, string owner)
    {
        if (image.Handle == 0)
            return true;

        VulkanResourceLifetimeKey key = new(ObjectType.Image, image.Handle);
        lock (lifetime.Tracker.SyncRoot)
        {
            if (lifetime.Tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? record) &&
                (record.State & EVulkanResourceLifetimeState.External) != 0)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Retirement.SkipExternalImage.{image.Handle}.{record.Generation}.{owner}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan.ResourceLifetime] Rejected stale owned-image retirement because {0} generation {1} is externally owned by {2}; requested by {3}.",
                    key, record.Generation, record.Owner, owner);
                return false;
            }
        }

        if (memory.Handle == 0 || !allocations.Images.Allocations.TryGetValue(image.Handle, out VulkanMemoryAllocation allocation) ||
            allocation.Memory.Handle == memory.Handle)
            return true;

        Debug.VulkanWarningEvery(
            $"Vulkan.Retirement.SkipRecycledImageAllocation.{image.Handle}.{memory.Handle}.{owner}",
            TimeSpan.FromSeconds(1),
            "[Vulkan.ResourceLifetime] Rejected stale image retirement for 0x{0:X}; requested memory=0x{1:X}, current memory=0x{2:X}, owner={3}.",
            image.Handle, memory.Handle, allocation.Memory.Handle, owner);
        return false;
    }

    private bool CanQueueImageViewRetirement(ImageView view, string owner)
    {
        if (view.Handle == 0 || !Views.LiveHandles.TryGetValue(view.Handle, out string? currentOwner) ||
            !IsExternalOwner(currentOwner))
            return true;

        bool compatible =
            (currentOwner.StartsWith("Swapchain.Color", StringComparison.Ordinal) && owner.Contains("Swapchain", StringComparison.Ordinal)) ||
            (currentOwner.StartsWith("OpenXR.Swapchain", StringComparison.Ordinal) && owner.Contains("OpenXR", StringComparison.OrdinalIgnoreCase));
        if (compatible)
            return true;

        ulong generation = lifetime.Tracker.GetPublishedGeneration(new VulkanResourceLifetimeKey(ObjectType.ImageView, view.Handle));
        Debug.VulkanWarningEvery(
            $"Vulkan.Retirement.SkipExternalImageView.{view.Handle}.{generation}.{owner}",
            TimeSpan.FromSeconds(1),
            "[Vulkan.ResourceLifetime] Rejected stale image-view retirement. ImageView=0x{0:X} Generation={1} CurrentOwner={2} RequestedBy={3}.",
            view.Handle, generation, currentOwner, owner);
        return false;
    }

    private ImageView[] FilterOwnedImageViewRetirementCandidates(ImageView[]? views, string owner)
    {
        if (views is null || views.Length == 0)
            return [];

        List<ImageView>? filtered = null;
        for (int index = 0; index < views.Length; index++)
        {
            if (!CanQueueImageViewRetirement(views[index], owner))
                continue;
            (filtered ??= new List<ImageView>(views.Length)).Add(views[index]);
        }
        return filtered is null ? [] : [.. filtered];
    }

    private ImageView TakeImageViewForRetirement(ImageView view, ulong generation, int frameSlot)
    {
        if (view.Handle == 0 || generation == 0)
            return default;
        VulkanPinnedResourceGeneration key = new(new VulkanResourceLifetimeKey(ObjectType.ImageView, view.Handle), generation);
        if (!lifetime.Retirement.AllImageViewHandles.Add(key))
            return default;
        lifetime.Retirement.ImageViewHandles[frameSlot].Add(key);
        return view;
    }

    private ImageView[] FilterRetiredAttachmentViews(
        ImageView[] views,
        ulong[] generations,
        int frameSlot,
        out ulong[] retainedGenerations)
    {
        if (views.Length == 0)
        {
            retainedGenerations = [];
            return [];
        }

        List<ImageView>? retainedViews = null;
        List<ulong>? retained = null;
        for (int index = 0; index < views.Length; index++)
        {
            ulong generation = index < generations.Length ? generations[index] : 0;
            ImageView view = TakeImageViewForRetirement(views[index], generation, frameSlot);
            if (view.Handle == 0)
                continue;
            (retainedViews ??= new List<ImageView>(views.Length)).Add(view);
            (retained ??= new List<ulong>(views.Length)).Add(generation);
        }
        retainedGenerations = retained is null ? [] : [.. retained];
        return retainedViews is null ? [] : [.. retainedViews];
    }

    private VulkanRetirementTicket CaptureTicket(VulkanResourceLifetimeKey key, string owner)
    {
        RequireCommandRuntime().PublishTrackingDependenciesBeforeResourceRetirement(key);
        return RequireResourceRuntime().CaptureRetirementTicket(key, owner);
    }

    private VulkanResourceRuntime? _resourceRuntime;

    internal void ConfigureRetirementRuntime(VulkanResourceRuntime resourceRuntime)
        => _resourceRuntime = resourceRuntime ?? throw new ArgumentNullException(nameof(resourceRuntime));

    private VulkanResourceRuntime RequireResourceRuntime()
        => _resourceRuntime ?? throw new InvalidOperationException("Image retirement runtime has not been configured.");

    internal void RetireViewsForBackingImage(ulong imageHandle, string owner)
    {
        if (imageHandle == 0)
            return;

        List<ImageView>? views = null;
        HashSet<ulong>? handles = null;
        lock (Views.InternGate)
        {
            foreach ((VulkanImageViewStructuralKey key, InternedImageViewEntry entry) in Views.InternedViews)
            {
                if (key.ImageHandle != imageHandle)
                    continue;
                (views ??= []).Add(entry.View);
                (handles ??= []).Add(entry.View.Handle);
            }
            if (views is not null)
                foreach (ImageView view in views)
                    if (Views.InternedKeysByHandle.Remove(view.Handle, out VulkanImageViewStructuralKey key))
                        Views.InternedViews.Remove(key);
        }

        foreach ((ulong handle, ImageViewCreateInfo info) in Views.DescriptorHeapCreateInfos)
        {
            if (info.Image.Handle == imageHandle && Views.LiveHandles.ContainsKey(handle) &&
                (handles is null || handles.Add(handle)))
                (views ??= []).Add(new ImageView { Handle = handle });
        }
        if (views is null)
            return;
        foreach (ImageView view in views)
            RetireOwnedResources(new RetiredImageResources(default, default, view, [], default, 0), $"{owner}.BackingImageView");
    }

    private VulkanCommandRuntime RequireCommandRuntime()
        => _commandRuntime ?? throw new InvalidOperationException("Image retirement command runtime has not been configured.");
}
