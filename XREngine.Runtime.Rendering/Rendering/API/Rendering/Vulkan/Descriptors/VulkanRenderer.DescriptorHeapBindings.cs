using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal bool IsDescriptorHeapDrawBindingActive => ShouldBindDescriptorHeapState();

    internal DescriptorHeapProgramLayout? CreateDescriptorHeapProgramLayout(
        IReadOnlyList<DescriptorBindingInfo> bindings,
        string programName,
        out string reason)
    {
        reason = string.Empty;
        if (!ShouldBindDescriptorHeapState())
        {
            reason = "descriptor heap is not the active descriptor backend.";
            return null;
        }

        if (bindings.Count == 0)
        {
            reason = "program has no descriptor bindings.";
            return DescriptorHeapProgramLayout.Empty;
        }

        uint nextPushOffset = 0u;
        DescriptorHeapBindingLayout[] layouts = new DescriptorHeapBindingLayout[bindings.Count];
        DescriptorSetAndBindingMappingEXTNative[] mappings = new DescriptorSetAndBindingMappingEXTNative[bindings.Count];
        Dictionary<DescriptorHeapBindingKey, DescriptorHeapBindingLayout> lookup = new(bindings.Count);

        for (int i = 0; i < bindings.Count; i++)
        {
            DescriptorBindingInfo binding = bindings[i];
            if (!TryCreateDescriptorHeapBindingLayout(binding, ref nextPushOffset, out DescriptorHeapBindingLayout? layout, out reason) ||
                layout is null)
            {
                reason = $"program '{programName}' binding set={binding.Set} binding={binding.Binding} type={binding.DescriptorType}: {reason}";
                return null;
            }

            layouts[i] = layout;
            lookup[new DescriptorHeapBindingKey(binding.Set, binding.Binding)] = layout;
            mappings[i] = CreateDescriptorHeapMapping(layout);
        }

        uint pushByteCount = nextPushOffset;
        if (_descriptorHeapProperties.MaxPushDataSize > 0 &&
            pushByteCount > _descriptorHeapProperties.MaxPushDataSize)
        {
            reason = $"program '{programName}' descriptor heap push-data layout needs {pushByteCount} bytes, maxPushDataSize={_descriptorHeapProperties.MaxPushDataSize}.";
            return null;
        }

        Debug.Vulkan(
            "[Vulkan.DescriptorHeap.Mapping] program='{0}' bindings={1} pushBytes={2}.",
            programName,
            bindings.Count,
            pushByteCount);

        return new DescriptorHeapProgramLayout(layouts, mappings, lookup, pushByteCount);
    }

    private bool TryCreateDescriptorHeapBindingLayout(
        DescriptorBindingInfo binding,
        ref uint nextPushOffset,
        out DescriptorHeapBindingLayout? layout,
        out string reason)
    {
        layout = null;
        reason = string.Empty;

        bool hasResource = DescriptorHeapBindingHasResource(binding.DescriptorType);
        bool hasSampler = DescriptorHeapBindingHasSampler(binding.DescriptorType);
        if (!hasResource && !hasSampler)
        {
            reason = $"descriptor type {binding.DescriptorType} is not supported by descriptor heap binding.";
            return false;
        }

        uint descriptorCount = Math.Max(1u, VulkanBindlessMaterialDescriptors.ResolveDescriptorCount(binding));
        DescriptorType resourceDescriptorType = ResolveDescriptorHeapResourceDescriptorType(binding.DescriptorType);
        ulong resourceStride = hasResource ? ResolveDescriptorHeapDescriptorStride(resourceDescriptorType) : 0ul;
        ulong samplerStride = hasSampler ? ResolveDescriptorHeapDescriptorStride(DescriptorType.Sampler) : 0ul;

        uint resourcePushOffset = uint.MaxValue;
        uint samplerPushOffset = uint.MaxValue;

        if (hasResource)
        {
            resourcePushOffset = nextPushOffset;
            nextPushOffset += sizeof(uint);
        }

        if (hasSampler)
        {
            if (binding.DescriptorType == DescriptorType.Sampler)
            {
                resourcePushOffset = nextPushOffset;
                nextPushOffset += sizeof(uint);
            }
            else
            {
                samplerPushOffset = nextPushOffset;
                nextPushOffset += sizeof(uint);
            }
        }

        layout = new DescriptorHeapBindingLayout(
            new DescriptorHeapBindingKey(binding.Set, binding.Binding),
            binding.DescriptorType,
            resourceDescriptorType,
            descriptorCount,
            hasResource,
            hasSampler,
            resourcePushOffset,
            samplerPushOffset,
            checked((uint)Math.Min(resourceStride, uint.MaxValue)),
            checked((uint)Math.Min(samplerStride, uint.MaxValue)));
        return true;
    }

    private DescriptorSetAndBindingMappingEXTNative CreateDescriptorHeapMapping(DescriptorHeapBindingLayout layout)
    {
        DescriptorMappingSourcePushIndexEXTNative pushIndex = new()
        {
            HeapOffset = 0,
            PushOffset = layout.ResourcePushOffset == uint.MaxValue ? 0u : layout.ResourcePushOffset,
            HeapIndexStride = layout.ResourceStride,
            HeapArrayStride = layout.ResourceStride,
            EmbeddedSampler = null,
            UseCombinedImageSamplerIndex = Vk.False,
            SamplerHeapOffset = 0,
            SamplerPushOffset = layout.SamplerPushOffset == uint.MaxValue ? 0u : layout.SamplerPushOffset,
            SamplerHeapIndexStride = layout.SamplerStride,
            SamplerHeapArrayStride = layout.SamplerStride,
        };

        if (layout.DescriptorType == DescriptorType.Sampler)
        {
            pushIndex.HeapIndexStride = layout.SamplerStride;
            pushIndex.HeapArrayStride = layout.SamplerStride;
        }

        return new DescriptorSetAndBindingMappingEXTNative
        {
            SType = VulkanDescriptorHeapExt.DescriptorSetAndBindingMappingSType,
            PNext = null,
            DescriptorSet = layout.Key.Set,
            FirstBinding = layout.Key.Binding,
            BindingCount = 1,
            ResourceMask = VulkanSpirvResourceTypeFlagsEXT.All,
            Source = VulkanDescriptorMappingSourceEXT.HeapWithPushIndex,
            SourceData = new DescriptorMappingSourceDataEXTNative
            {
                PushIndex = pushIndex,
            },
        };
    }

    internal DescriptorHeapPushDataPayload CreateDescriptorHeapPushDataPayload(DescriptorHeapProgramLayout? layout)
        => layout is { PushDwordCount: > 0 }
            ? new DescriptorHeapPushDataPayload(new uint[layout.PushDwordCount])
            : DescriptorHeapPushDataPayload.Empty;

    internal bool TryWriteDescriptorHeapBinding(
        VkRenderProgram program,
        DescriptorBindingInfo binding,
        DescriptorHeapPushDataPayload payload,
        DescriptorBufferInfo* bufferInfos,
        DescriptorImageInfo* imageInfos,
        BufferView* texelBufferViews,
        uint descriptorCount,
        out string reason)
    {
        reason = string.Empty;
        if (!ShouldBindDescriptorHeapState())
        {
            reason = "descriptor heap is not active.";
            return false;
        }

        DescriptorHeapProgramLayout? layout = program.DescriptorHeapLayout;
        if (layout is null || !layout.TryGetBinding(binding.Set, binding.Binding, out DescriptorHeapBindingLayout bindingLayout))
        {
            reason = $"descriptor heap mapping is missing for set={binding.Set} binding={binding.Binding}.";
            return false;
        }

        descriptorCount = Math.Max(1u, descriptorCount);

        if (bindingLayout.HasResource)
        {
            if (!TryWriteDescriptorHeapResourceBinding(bindingLayout, bufferInfos, imageInfos, texelBufferViews, descriptorCount, out uint resourceIndex, out reason))
                return false;

            payload.SetDword(bindingLayout.ResourcePushOffset, resourceIndex);
        }

        if (bindingLayout.HasSampler)
        {
            if (!TryWriteDescriptorHeapSamplerBinding(bindingLayout, imageInfos, descriptorCount, out uint samplerIndex, out reason))
                return false;

            uint pushOffset = bindingLayout.DescriptorType == DescriptorType.Sampler
                ? bindingLayout.ResourcePushOffset
                : bindingLayout.SamplerPushOffset;
            payload.SetDword(pushOffset, samplerIndex);
        }

        return true;
    }

    internal bool TryPushDescriptorHeapProgramData(
        CommandBuffer commandBuffer,
        VkRenderProgram program,
        DescriptorHeapPushDataPayload? payload,
        out string reason)
    {
        reason = string.Empty;
        if (!ShouldBindDescriptorHeapState())
            return true;

        DescriptorHeapProgramLayout? layout = program.DescriptorHeapLayout;
        if (layout is null || layout.PushByteCount == 0)
            return true;

        if (payload is null || !payload.IsValidFor(layout))
        {
            reason = $"descriptor heap push payload is missing or has the wrong size for program '{program.Data?.Name ?? "UnnamedProgram"}'.";
            return false;
        }

        fixed (uint* dataPtr = payload.Dwords)
            return TryPushDescriptorHeapData(commandBuffer, 0, dataPtr, layout.PushByteCount, out reason);
    }

    internal uint DescriptorHeapSampledImageStride
        => checked((uint)ResolveDescriptorHeapDescriptorStride(DescriptorType.SampledImage));

    internal uint DescriptorHeapSamplerStride
        => checked((uint)ResolveDescriptorHeapDescriptorStride(DescriptorType.Sampler));

    internal bool TryWriteDescriptorHeapCombinedImageSamplerPayload(
        DescriptorImageInfo imageInfo,
        DescriptorHeapPushDataPayload payload,
        out string reason)
    {
        reason = string.Empty;
        if (!ShouldBindDescriptorHeapState())
            return true;

        if (payload.Dwords.Length < 2)
        {
            reason = "combined image sampler heap payload must contain two dwords.";
            return false;
        }

        DescriptorHeapBindingLayout layout = new(
            new DescriptorHeapBindingKey(0, 0),
            DescriptorType.CombinedImageSampler,
            DescriptorType.SampledImage,
            1u,
            HasResource: true,
            HasSampler: true,
            ResourcePushOffset: 0u,
            SamplerPushOffset: sizeof(uint),
            ResourceStride: DescriptorHeapSampledImageStride,
            SamplerStride: DescriptorHeapSamplerStride);

        if (!TryWriteDescriptorHeapResourceBinding(layout, null, &imageInfo, null, 1u, out uint resourceIndex, out reason))
            return false;

        if (!TryWriteDescriptorHeapSamplerBinding(layout, &imageInfo, 1u, out uint samplerIndex, out reason))
            return false;

        payload.SetDword(0u, resourceIndex);
        payload.SetDword(sizeof(uint), samplerIndex);
        return true;
    }

    private bool TryWriteDescriptorHeapResourceBinding(
        DescriptorHeapBindingLayout layout,
        DescriptorBufferInfo* bufferInfos,
        DescriptorImageInfo* imageInfos,
        BufferView* texelBufferViews,
        uint descriptorCount,
        out uint heapIndex,
        out string reason)
    {
        heapIndex = 0;
        reason = string.Empty;

        if (!TryAllocateDescriptorHeapResourceRange(layout.ResourceDescriptorType, descriptorCount, out ulong destinationOffset, out ulong destinationSize, out reason))
            return false;

        ResourceDescriptorInfoEXTNative* resources = stackalloc ResourceDescriptorInfoEXTNative[(int)descriptorCount];
        DeviceAddressRangeEXTNative* addressRanges = stackalloc DeviceAddressRangeEXTNative[(int)descriptorCount];
        ImageDescriptorInfoEXTNative* images = stackalloc ImageDescriptorInfoEXTNative[(int)descriptorCount];
        ImageViewCreateInfo* imageViews = stackalloc ImageViewCreateInfo[(int)descriptorCount];
        TexelBufferDescriptorInfoEXTNative* texelBuffers = stackalloc TexelBufferDescriptorInfoEXTNative[(int)descriptorCount];

        for (uint i = 0; i < descriptorCount; i++)
        {
            resources[i] = new ResourceDescriptorInfoEXTNative
            {
                SType = VulkanDescriptorHeapExt.ResourceDescriptorInfoSType,
                PNext = null,
                Type = layout.ResourceDescriptorType,
            };

            switch (layout.ResourceDescriptorType)
            {
                case DescriptorType.UniformBuffer:
                case DescriptorType.StorageBuffer:
                    if (!TryCreateDescriptorHeapAddressRange(bufferInfos[i], out addressRanges[i], out reason))
                        return false;
                    resources[i].Data.AddressRange = addressRanges + i;
                    break;

                case DescriptorType.SampledImage:
                case DescriptorType.StorageImage:
                case DescriptorType.InputAttachment:
                    if (!TryGetDescriptorHeapImageViewCreateInfo(imageInfos[i].ImageView, out imageViews[i]))
                    {
                        reason = $"image view 0x{imageInfos[i].ImageView.Handle:X} has no descriptor heap create-info metadata.";
                        return false;
                    }

                    images[i] = new ImageDescriptorInfoEXTNative
                    {
                        SType = VulkanDescriptorHeapExt.ImageDescriptorInfoSType,
                        PNext = null,
                        View = imageViews + i,
                        Layout = imageInfos[i].ImageLayout,
                    };
                    resources[i].Data.Image = images + i;
                    break;

                case DescriptorType.UniformTexelBuffer:
                case DescriptorType.StorageTexelBuffer:
                    if (!TryCreateDescriptorHeapTexelBufferInfo(texelBufferViews[i], out texelBuffers[i], out reason))
                        return false;
                    resources[i].Data.TexelBuffer = texelBuffers + i;
                    break;

                default:
                    reason = $"descriptor type {layout.ResourceDescriptorType} is not a supported resource heap descriptor.";
                    return false;
            }
        }

        if (!TryWriteDescriptorHeapResourceDescriptors(descriptorCount, resources, destinationOffset, destinationSize, out reason))
            return false;

        heapIndex = checked((uint)(destinationOffset / layout.ResourceStride));
        return true;
    }

    private bool TryWriteDescriptorHeapSamplerBinding(
        DescriptorHeapBindingLayout layout,
        DescriptorImageInfo* imageInfos,
        uint descriptorCount,
        out uint heapIndex,
        out string reason)
    {
        heapIndex = 0;
        reason = string.Empty;

        if (imageInfos is null)
        {
            reason = "sampler descriptor heap write has no image/sampler descriptor data.";
            return false;
        }

        if (!TryAllocateDescriptorHeapSamplerRange(descriptorCount, out ulong destinationOffset, out ulong destinationSize, out reason))
            return false;

        SamplerCreateInfo* samplers = stackalloc SamplerCreateInfo[(int)descriptorCount];
        for (uint i = 0; i < descriptorCount; i++)
        {
            Sampler sampler = imageInfos[i].Sampler;
            if (!TryGetDescriptorHeapSamplerCreateInfo(sampler, out samplers[i]))
            {
                reason = $"sampler 0x{sampler.Handle:X} has no descriptor heap create-info metadata.";
                return false;
            }
        }

        if (!TryWriteDescriptorHeapSamplerDescriptors(descriptorCount, samplers, destinationOffset, destinationSize, out reason))
            return false;

        heapIndex = checked((uint)(destinationOffset / layout.SamplerStride));
        return true;
    }

    private bool TryCreateDescriptorHeapAddressRange(
        DescriptorBufferInfo bufferInfo,
        out DeviceAddressRangeEXTNative range,
        out string reason)
    {
        range = default;
        reason = string.Empty;

        if (bufferInfo.Buffer.Handle == 0 || bufferInfo.Range == 0)
        {
            reason = "buffer descriptor has no buffer handle or range.";
            return false;
        }

        ulong baseAddress = GetBufferDeviceAddress(bufferInfo.Buffer);
        if (baseAddress == 0)
        {
            reason = $"buffer 0x{bufferInfo.Buffer.Handle:X} has no device address; descriptor heap buffer descriptors require shader-device-address usage.";
            return false;
        }

        range = new DeviceAddressRangeEXTNative
        {
            Address = checked(baseAddress + bufferInfo.Offset),
            Size = bufferInfo.Range,
        };
        return true;
    }

    private bool TryCreateDescriptorHeapTexelBufferInfo(
        BufferView bufferView,
        out TexelBufferDescriptorInfoEXTNative texelBuffer,
        out string reason)
    {
        texelBuffer = default;
        reason = string.Empty;

        if (!TryGetDescriptorHeapBufferViewCreateInfo(bufferView, out BufferViewCreateInfo createInfo))
        {
            reason = $"buffer view 0x{bufferView.Handle:X} has no descriptor heap create-info metadata.";
            return false;
        }

        ulong baseAddress = GetBufferDeviceAddress(createInfo.Buffer);
        if (baseAddress == 0)
        {
            reason = $"buffer view 0x{bufferView.Handle:X} references buffer 0x{createInfo.Buffer.Handle:X} with no device address.";
            return false;
        }

        texelBuffer = new TexelBufferDescriptorInfoEXTNative
        {
            SType = VulkanDescriptorHeapExt.TexelBufferDescriptorInfoSType,
            PNext = null,
            Format = createInfo.Format,
            AddressRange = new DeviceAddressRangeEXTNative
            {
                Address = checked(baseAddress + createInfo.Offset),
                Size = createInfo.Range,
            },
        };
        return true;
    }

    private bool TryAllocateDescriptorHeapResourceRange(
        DescriptorType descriptorType,
        uint descriptorCount,
        out ulong offset,
        out ulong size,
        out string reason)
        => TryAllocateDescriptorHeapRange(
            ref _descriptorHeapResourceHighWaterBytes,
            _descriptorHeapResourceStorage,
            ResolveDescriptorHeapDescriptorStride(descriptorType),
            descriptorCount,
            out offset,
            out size,
            out reason);

    private bool TryAllocateDescriptorHeapSamplerRange(
        uint descriptorCount,
        out ulong offset,
        out ulong size,
        out string reason)
        => TryAllocateDescriptorHeapRange(
            ref _descriptorHeapSamplerHighWaterBytes,
            _descriptorHeapSamplerStorage,
            ResolveDescriptorHeapDescriptorStride(DescriptorType.Sampler),
            descriptorCount,
            out offset,
            out size,
            out reason);

    private static bool TryAllocateDescriptorHeapRange(
        ref ulong cursor,
        DescriptorHeapStorage storage,
        ulong stride,
        uint descriptorCount,
        out ulong offset,
        out ulong size,
        out string reason)
    {
        offset = 0;
        size = 0;
        reason = string.Empty;

        if (!storage.IsReady)
        {
            reason = "descriptor heap storage is not ready.";
            return false;
        }

        stride = Math.Max(stride, 1ul);
        offset = AlignDescriptorHeapUp(cursor, stride);
        size = checked(stride * Math.Max(1u, descriptorCount));
        if (offset > storage.Size || size > storage.Size - offset)
        {
            reason = $"descriptor heap capacity exhausted (offset={offset}, size={size}, capacity={storage.Size}).";
            return false;
        }

        cursor = offset + size;
        return true;
    }

    private ulong ResolveDescriptorHeapDescriptorStride(DescriptorType descriptorType)
    {
        ulong size = ResolveDescriptorHeapDescriptorSize(descriptorType, ResolveDescriptorHeapFallbackSize(descriptorType));
        ulong alignment = ResolveDescriptorHeapDescriptorAlignment(descriptorType);
        return AlignDescriptorHeapUp(Math.Max(size, 1ul), Math.Max(alignment, 1ul));
    }

    private ulong ResolveDescriptorHeapFallbackSize(DescriptorType descriptorType)
        => descriptorType == DescriptorType.Sampler
            ? _descriptorHeapProperties.SamplerDescriptorSize
            : IsImageResourceDescriptor(descriptorType)
                ? _descriptorHeapProperties.ImageDescriptorSize
                : _descriptorHeapProperties.BufferDescriptorSize;

    private ulong ResolveDescriptorHeapDescriptorAlignment(DescriptorType descriptorType)
        => descriptorType == DescriptorType.Sampler
            ? _descriptorHeapProperties.SamplerDescriptorAlignment
            : IsImageResourceDescriptor(descriptorType)
                ? _descriptorHeapProperties.ImageDescriptorAlignment
                : _descriptorHeapProperties.BufferDescriptorAlignment;

    private static bool DescriptorHeapBindingHasResource(DescriptorType descriptorType)
        => descriptorType is DescriptorType.CombinedImageSampler
            or DescriptorType.SampledImage
            or DescriptorType.StorageImage
            or DescriptorType.InputAttachment
            or DescriptorType.UniformBuffer
            or DescriptorType.StorageBuffer
            or DescriptorType.UniformTexelBuffer
            or DescriptorType.StorageTexelBuffer;

    private static bool DescriptorHeapBindingHasSampler(DescriptorType descriptorType)
        => descriptorType is DescriptorType.CombinedImageSampler or DescriptorType.Sampler;

    private static bool IsImageResourceDescriptor(DescriptorType descriptorType)
        => descriptorType is DescriptorType.CombinedImageSampler
            or DescriptorType.SampledImage
            or DescriptorType.StorageImage
            or DescriptorType.InputAttachment;

    private static DescriptorType ResolveDescriptorHeapResourceDescriptorType(DescriptorType descriptorType)
        => descriptorType switch
        {
            DescriptorType.CombinedImageSampler => DescriptorType.SampledImage,
            DescriptorType.UniformBufferDynamic => DescriptorType.UniformBuffer,
            DescriptorType.StorageBufferDynamic => DescriptorType.StorageBuffer,
            _ => descriptorType,
        };

    internal sealed class DescriptorHeapProgramLayout(
        DescriptorHeapBindingLayout[] bindings,
        DescriptorSetAndBindingMappingEXTNative[] mappings,
        Dictionary<DescriptorHeapBindingKey, DescriptorHeapBindingLayout> lookup,
        uint pushByteCount)
    {
        public static DescriptorHeapProgramLayout Empty { get; } = new([], [], [], 0u);

        public DescriptorHeapBindingLayout[] Bindings { get; } = bindings;
        public DescriptorSetAndBindingMappingEXTNative[] Mappings { get; } = mappings;
        public uint PushByteCount { get; } = pushByteCount;
        public int PushDwordCount { get; } = checked((int)((pushByteCount + 3u) / 4u));

        public bool TryGetBinding(uint set, uint binding, out DescriptorHeapBindingLayout layout)
            => lookup.TryGetValue(new DescriptorHeapBindingKey(set, binding), out layout!);
    }

    internal readonly record struct DescriptorHeapBindingKey(uint Set, uint Binding);

    internal sealed record DescriptorHeapBindingLayout(
        DescriptorHeapBindingKey Key,
        DescriptorType DescriptorType,
        DescriptorType ResourceDescriptorType,
        uint DescriptorCount,
        bool HasResource,
        bool HasSampler,
        uint ResourcePushOffset,
        uint SamplerPushOffset,
        uint ResourceStride,
        uint SamplerStride);

    internal sealed class DescriptorHeapPushDataPayload(uint[] dwords)
    {
        public static DescriptorHeapPushDataPayload Empty { get; } = new([]);

        public uint[] Dwords { get; } = dwords;

        public void SetDword(uint byteOffset, uint value)
        {
            if (byteOffset == uint.MaxValue)
                return;

            Dwords[checked((int)(byteOffset / sizeof(uint)))] = value;
        }

        public bool IsValidFor(DescriptorHeapProgramLayout layout)
            => Dwords.Length >= layout.PushDwordCount;
    }
}
