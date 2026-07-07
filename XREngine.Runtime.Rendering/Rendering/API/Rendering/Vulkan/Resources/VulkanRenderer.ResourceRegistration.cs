using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private static ImageLayout ResolveInitialPhysicalGroupLayout(ImageUsageFlags usage, bool isDepth)
    {
        bool colorAttachment = (usage & ImageUsageFlags.ColorAttachmentBit) != 0;
        bool sampled = (usage & (ImageUsageFlags.SampledBit | ImageUsageFlags.InputAttachmentBit)) != 0;
        bool storage = (usage & ImageUsageFlags.StorageBit) != 0;

        // Images that are used as storage (e.g. compute write targets) must be
        // accessible in GENERAL layout.  VK_IMAGE_LAYOUT_GENERAL is compatible
        // with color-attachment, sampled, and storage operations, so choosing it
        // here avoids the first-frame mismatch where a descriptor is written with
        // GENERAL (for StorageImage) but the image is still in
        // COLOR_ATTACHMENT_OPTIMAL.
        if (storage)
            return ImageLayout.General;

        // Prefer descriptor-compatible read-only layouts for sampled render targets.
        // Dynamic-rendering begin and render-graph pass barriers transition them into
        // attachment layouts at the actual write site. Starting sampled images in an
        // attachment layout made CPU tracking disagree with descriptor/final layouts
        // after plan rebuilds and window resizes.
        if (sampled)
            return isDepth
                ? ImageLayout.DepthStencilReadOnlyOptimal
                : ImageLayout.ShaderReadOnlyOptimal;

        if (isDepth)
            return ImageLayout.DepthStencilAttachmentOptimal;

        if (colorAttachment)
            return ImageLayout.ColorAttachmentOptimal;

        if ((usage & ImageUsageFlags.TransferDstBit) != 0)
            return ImageLayout.TransferDstOptimal;

        if ((usage & ImageUsageFlags.TransferSrcBit) != 0)
            return ImageLayout.TransferSrcOptimal;

        return ImageLayout.General;
    }

    private static bool HasStencilComponent(Format format)
        => format is Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.D16UnormS8Uint;

    internal void AllocatePhysicalImage(VulkanPhysicalImageGroup group, ref Image image, ref DeviceMemory memory)
    {
        if (TryAllocatePhysicalImage(group, ref image, ref memory, out string failureReason))
            return;

        throw new VulkanOutOfMemoryException(failureReason, group.MemoryProperties);
    }

    internal bool TryAllocatePhysicalImage(
        VulkanPhysicalImageGroup group,
        ref Image image,
        ref DeviceMemory memory,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (image.Handle != 0)
            return true;

        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            Flags = 0, // TODO: add alias bit when Silk.NET exposes VK_IMAGE_CREATE_ALIAS_BIT
            ImageType = ImageType.Type2D,
            Extent = group.ResolvedExtent,
            MipLevels = Math.Max(1u, group.MipLevels),
            ArrayLayers = Math.Max(group.Template.Layers, 1u),
            Format = group.Format,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = group.Usage,
            Samples = group.Samples,
            SharingMode = SharingMode.Exclusive,
        };

        fixed (Image* imagePtr = &image)
        {
            Result result = Api!.CreateImage(device, ref imageInfo, null, imagePtr);
            if (result != Result.Success)
            {
                image = default;
                failureReason = $"Failed to create Vulkan image for resource group '{group.Key}'. Result={result}.";
                return false;
            }
        }

        ClearTrackedImageLayouts(image);

        Debug.VulkanEvery(
            $"Vulkan.PhysicalImage.Alloc.{group.Key}",
            TimeSpan.FromSeconds(2),
            "[Vulkan] Physical image allocated: resource='{0}' handle=0x{1:X} format={2} usage={3} extent={4}x{5} layers={6} mips={7} samples={8}",
            group.Key,
            image.Handle,
            group.Format,
            group.Usage,
            group.ResolvedExtent.Width,
            group.ResolvedExtent.Height,
            Math.Max(group.Template.Layers, 1u),
            Math.Max(1u, group.MipLevels),
            group.Samples);

        if (group.TransientAttachmentPolicy == VulkanTransientAttachmentPolicy.PreferLazilyAllocated &&
            !SupportsLazyAllocation)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.TransientAttachment.LazyUnsupported.{group.Key}",
                TimeSpan.FromSeconds(5),
                "[Vulkan] Transient attachment group '{0}' requested lazy memory, but this device exposes no lazily allocated memory type. Falling back to device-local memory.",
                group.Key);
        }

        try
        {
            if (!TryAllocateImageMemoryWithFallback(
                image,
                group.MemoryProperties,
                out VulkanMemoryAllocation allocation,
                out failureReason))
            {
                if (image.Handle != 0)
                {
                    Api!.DestroyImage(device, image, null);
                    image = default;
                }

                memory = default;
                return false;
            }

            _imageAllocations[image.Handle] = allocation;
            memory = allocation.Memory;

            Result bindResult = Api!.BindImageMemory(device, image, memory, allocation.Offset);
            if (bindResult != Result.Success)
            {
                _imageAllocations.TryRemove(image.Handle, out _);
                FreeMemoryAllocation(allocation);
                if (image.Handle != 0)
                {
                    Api!.DestroyImage(device, image, null);
                    image = default;
                }

                memory = default;
                failureReason = $"Failed to bind device memory for Vulkan image group '{group.Key}'. Result={bindResult}.";
                return false;
            }
        }
        catch
        {
            if (memory.Handle != 0)
            {
                if (_imageAllocations.TryRemove(image.Handle, out VulkanMemoryAllocation fallbackAlloc))
                    FreeMemoryAllocation(fallbackAlloc);
                else
                    Api!.FreeMemory(device, memory, null);
                memory = default;
            }

            if (image.Handle != 0)
            {
                Api!.DestroyImage(device, image, null);
                image = default;
            }

            throw;
        }

        return true;
    }

    internal void DestroyPhysicalImage(ref Image image, ref DeviceMemory memory)
    {
        // Defer destruction â€” the image may still be referenced by in-flight
        // command buffers from other frame slots.  The retirement queue ensures
        // the handles are destroyed only after the current slot's timeline fence
        // signals (which is after all earlier submissions have completed).
        RetireImageResources(new RetiredImageResources(
            image, memory,
            PrimaryView: default,
            AttachmentViews: [],
            Sampler: default,
            AllocatedVRAMBytes: 0));

        image = default;
        memory = default;
    }

    internal void DestroyPhysicalImageImmediate(ref Image image, ref DeviceMemory memory)
    {
        Image imageToDestroy = image;
        DeviceMemory memoryToFree = memory;
        image = default;
        memory = default;

        if (imageToDestroy.Handle != 0)
            ClearTrackedImageLayouts(imageToDestroy);

        VulkanMemoryAllocation trackedAllocation = default;
        bool hasTrackedAllocation = imageToDestroy.Handle != 0 &&
            _imageAllocations.TryRemove(imageToDestroy.Handle, out trackedAllocation);

        if (imageToDestroy.Handle != 0)
            Api!.DestroyImage(device, imageToDestroy, null);

        if (hasTrackedAllocation)
        {
            FreeMemoryAllocation(trackedAllocation);
            return;
        }

        if (memoryToFree.Handle == 0)
            return;

        if (MemoryAllocator is VulkanLegacyAllocator)
        {
            Api!.FreeMemory(device, memoryToFree, null);
            return;
        }

        Debug.VulkanWarningEvery(
            $"Vulkan.ImageMemory.SkipUnknownRawFree.{GetHashCode()}.{memoryToFree.Handle}",
            TimeSpan.FromSeconds(5),
            "[Vulkan] Skipping raw vkFreeMemory for untracked image memory 0x{0:X}; current allocator is {1}, so the handle may be allocator-owned shared memory.",
            memoryToFree.Handle,
            MemoryAllocator.GetType().Name);
    }

    internal void AllocatePhysicalBuffer(VulkanPhysicalBufferGroup group, ref Buffer buffer, ref DeviceMemory memory)
    {
        if (buffer.Handle != 0)
            return;

        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = Math.Max(group.SizeInBytes, 1UL),
            Usage = group.Usage,
            SharingMode = SharingMode.Exclusive
        };

        fixed (Buffer* bufferPtr = &buffer)
        {
            Result createResult = Api!.CreateBuffer(device, ref bufferInfo, null, bufferPtr);
            if (createResult != Result.Success)
                throw new Exception($"Failed to create Vulkan buffer for resource group '{group.Key}'. Result={createResult}.");
        }
        TrackLiveBuffer(buffer);

        try
        {
            VulkanMemoryAllocation allocation = AllocateBufferMemoryWithFallback(buffer, MemoryPropertyFlags.DeviceLocalBit);
            _bufferAllocations[buffer.Handle] = allocation;
            memory = allocation.Memory;

            Result bindResult = Api!.BindBufferMemory(device, buffer, memory, allocation.Offset);
            if (bindResult != Result.Success)
            {
                _bufferAllocations.TryRemove(buffer.Handle, out _);
                FreeMemoryAllocation(allocation);
                memory = default;
                throw new Exception($"Failed to bind device memory for Vulkan buffer group '{group.Key}'. Result={bindResult}.");
            }
        }
        catch
        {
            if (buffer.Handle != 0)
            {
                DestroyBufferRaw(buffer, memory);
                buffer = default;
            }
            else if (memory.Handle != 0)
            {
                FreeUntrackedBufferMemory(memory, "AllocatePhysicalBuffer.Catch");
            }

            memory = default;

            throw;
        }
    }

    internal void DestroyPhysicalBuffer(ref Buffer buffer, ref DeviceMemory memory)
    {
        RetireBuffer(buffer, memory);
        buffer = default;
        memory = default;
    }

    internal void DestroyPhysicalBufferImmediate(ref Buffer buffer, ref DeviceMemory memory)
    {
        DestroyBufferRaw(buffer, memory);
        buffer = default;
        memory = default;
    }

    public bool TryGetPhysicalImage(string resourceName, out Image image)
        => ResourceAllocator.TryGetImage(resourceName, out image);

    public bool TryGetPhysicalBuffer(string resourceName, out Buffer buffer, out ulong size)
        => ResourceAllocator.TryGetBuffer(resourceName, out buffer, out size);

    private void EnsureFrameBufferRegistered(XRFrameBuffer frameBuffer)
    {
        var registry = RuntimeEngine.Rendering.State.CurrentResourceRegistry;
        if (registry is null)
            return;

        string? name = frameBuffer.Name;
        if (string.IsNullOrWhiteSpace(name))
            return;

        FrameBufferResourceDescriptor? descriptor = registry.FrameBufferRecords.TryGetValue(name, out RenderFrameBufferResource? record)
            ? record.Descriptor
            : null;
        registry.BindFrameBuffer(frameBuffer, descriptor);
    }

    private void EnsureFrameBufferAttachmentsRegistered(XRFrameBuffer frameBuffer)
    {
        var registry = RuntimeEngine.Rendering.State.CurrentResourceRegistry;
        if (registry is null)
            return;

        var targets = frameBuffer.Targets;
        if (targets is null)
            return;

        foreach (var (target, attachment, mipLevel, layerIndex) in targets)
        {
            if (target is XRTexture texture)
            {
                string? textureName = texture.Name;
                if (!string.IsNullOrWhiteSpace(textureName))
                {
                    TextureResourceDescriptor descriptor = registry.TextureRecords.TryGetValue(textureName, out RenderTextureResource? existingRecord)
                        ? existingRecord.Descriptor
                        : RenderResourceDescriptorFactory.FromTexture(texture);

                    registry.BindTexture(texture, EnrichTextureDescriptorForFrameBufferAttachment(descriptor, texture, attachment, mipLevel, layerIndex));
                }

                if (texture is XRTextureViewBase view)
                {
                    XRTexture viewedTexture = view.GetViewedTexture();
                    string? viewedTextureName = viewedTexture.Name;
                    if (!string.IsNullOrWhiteSpace(viewedTextureName))
                    {
                        TextureResourceDescriptor descriptor = registry.TextureRecords.TryGetValue(viewedTextureName, out RenderTextureResource? existingRecord)
                            ? existingRecord.Descriptor
                            : RenderResourceDescriptorFactory.FromTexture(viewedTexture);

                        int sourceMipLevel = mipLevel >= 0 ? SaturatingAddToInt32(view.MinLevel, (uint)mipLevel) : mipLevel;
                        int sourceLayerIndex = layerIndex >= 0 ? SaturatingAddToInt32(view.MinLayer, (uint)layerIndex) : layerIndex;
                        registry.BindTexture(viewedTexture, EnrichTextureDescriptorForFrameBufferAttachment(descriptor, viewedTexture, attachment, sourceMipLevel, sourceLayerIndex));
                    }
                }
            }
        }
    }

    private static int SaturatingAddToInt32(uint left, uint right)
    {
        ulong sum = (ulong)left + right;
        return sum > int.MaxValue ? int.MaxValue : (int)sum;
    }

    private static TextureResourceDescriptor EnrichTextureDescriptorForFrameBufferAttachment(
        TextureResourceDescriptor descriptor,
        XRTexture texture,
        EFrameBufferAttachment attachment,
        int mipLevel,
        int layerIndex)
    {
        RenderPipelineResourceUsage usage = descriptor.Usage | RenderPipelineResourceUsage.SampledTexture;
        usage |= attachment is EFrameBufferAttachment.DepthAttachment
            or EFrameBufferAttachment.DepthStencilAttachment
            or EFrameBufferAttachment.StencilAttachment
            ? RenderPipelineResourceUsage.DepthStencilAttachment
            : RenderPipelineResourceUsage.ColorAttachment;

        uint requiredMipLevels = mipLevel >= 0
            ? Math.Max(descriptor.MipPolicy.MipLevelCount, (uint)mipLevel + 1u)
            : Math.Max(descriptor.MipPolicy.MipLevelCount, 1u);

        uint requiredLayers = layerIndex >= 0
            ? Math.Max(descriptor.ArrayLayers, (uint)layerIndex + 1u)
            : descriptor.ArrayLayers;

        return descriptor with
        {
            Name = texture.Name ?? descriptor.Name,
            Usage = usage,
            MipPolicy = descriptor.MipPolicy with { MipLevelCount = requiredMipLevels },
            MipLevelCount = Math.Max(descriptor.MipLevelCount, requiredMipLevels),
            ArrayLayers = Math.Max(requiredLayers, 1u),
            LayerCount = Math.Max(descriptor.LayerCount, requiredLayers)
        };
    }

    internal void TrackTextureBinding(XRTexture texture)
    {
        if (texture is null)
            return;

        string? name = texture.Name;
        if (string.IsNullOrWhiteSpace(name))
            return;

        RenderResourceRegistry? registry = RuntimeEngine.Rendering.State.CurrentResourceRegistry;
        if (registry is null)
            return;

        if (!registry.TextureRecords.TryGetValue(name, out RenderTextureResource? record))
            return;

        if (ReferenceEquals(record.Instance, texture))
            return;

        registry.BindTexture(texture, record.Descriptor);
    }

    internal void TrackBufferBinding(XRDataBuffer buffer)
    {
        if (buffer is null)
            return;

        string name = string.IsNullOrWhiteSpace(buffer.AttributeName)
            ? buffer.Name ?? string.Empty
            : buffer.AttributeName;

        if (string.IsNullOrWhiteSpace(name))
            return;

        _trackedBuffersByName[name] = buffer;

        RenderResourceRegistry? registry = RuntimeEngine.Rendering.State.CurrentResourceRegistry;
        if (registry is null)
            return;

        if (!registry.BufferRecords.TryGetValue(name, out RenderBufferResource? record))
            return;

        if (ReferenceEquals(record.Instance, buffer))
            return;

        registry.BindBuffer(buffer, record.Descriptor);
    }

    internal bool TryResolveTrackedBuffer(string resourceName, out Buffer buffer, out ulong size)
    {
        if (ResourceAllocator.TryGetBuffer(resourceName, out buffer, out size))
            return true;

        if (_trackedBuffersByName.TryGetValue(resourceName, out XRDataBuffer? dataBuffer) &&
            GetOrCreateAPIRenderObject(dataBuffer, true) is VkDataBuffer vkBuffer)
        {
            vkBuffer.Generate();
            if (vkBuffer.BufferHandle is { } handle && handle.Handle != 0)
            {
                buffer = handle;
                size = Math.Max(dataBuffer.Length, 1u);
                return true;
            }
        }

        buffer = default;
        size = 0;
        return false;
    }
}
