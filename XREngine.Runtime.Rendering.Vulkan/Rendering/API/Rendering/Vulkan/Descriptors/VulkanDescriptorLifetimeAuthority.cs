using System.Buffers;
using System.Diagnostics;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns descriptor-set publication, mutation validation, and descriptor pool/set
/// retirement for one device generation.
/// </summary>
/// <remarks>
/// Descriptor wrappers depend on this authority instead of retaining the renderer
/// facade. Native writes remain serialized with the lifetime ledger so a resource
/// cannot retire between validation and Vulkan copying the descriptor payload.
/// The authority captures command-cache invalidation work under that lock, then
/// applies it only after the lock is released through the generation-local
/// command-operation boundary.
/// </remarks>
internal sealed unsafe class VulkanDescriptorLifetimeAuthority
{
    private const int MeshDescriptorPoolSlabAllocationCapacity = 64;
    private readonly VulkanResourceRuntime _resources;
    private readonly VulkanDescriptorManager _descriptors;
    private readonly VulkanLifetimeAuthority _lifetime;
    private VulkanDeviceContext? _deviceContext;
    private VulkanBackendObjectContext? _backendContext;
    private int _payloadChangeDiagnosticCount;

    internal VulkanDescriptorLifetimeAuthority(
        VulkanResourceRuntime resources,
        VulkanDescriptorManager descriptors,
        VulkanLifetimeAuthority lifetime)
    {
        _resources = resources;
        _descriptors = descriptors;
        _lifetime = lifetime;
    }

    internal void Configure(VulkanDeviceContext deviceContext)
    {
        ArgumentNullException.ThrowIfNull(deviceContext);
        if (_deviceContext is not null &&
            !ReferenceEquals(_deviceContext, deviceContext))
        {
            throw new InvalidOperationException(
                "The descriptor lifetime authority cannot be rebound to another device generation.");
        }

        _deviceContext = deviceContext;
    }

    internal void RecordTableGeneration()
        => _resources.RecordDescriptorTableGeneration();

    /// <summary>Assigns a validation-layer name to a descriptor set when debug utils are active.</summary>
    internal void SetDebugName(DescriptorSet descriptorSet, string name)
    {
        VulkanDeviceContext deviceContext = RequireDeviceContext();
        if (deviceContext.DebugUtils is null ||
            deviceContext.Device.Handle == 0 ||
            descriptorSet.Handle == 0 ||
            string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        nint namePointer = SilkMarshal.StringToPtr(name);
        try
        {
            DebugUtilsObjectNameInfoEXT nameInfo = new()
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = ObjectType.DescriptorSet,
                ObjectHandle = descriptorSet.Handle,
                PObjectName = (byte*)namePointer,
            };
            _ = deviceContext.DebugUtils.SetDebugUtilsObjectName(deviceContext.Device, in nameInfo);
        }
        finally
        {
            SilkMarshal.Free(namePointer);
        }
    }

    internal void PublishBackendObjectContext(VulkanBackendObjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        VulkanBackendObjectContext? current = Interlocked.CompareExchange(
            ref _backendContext,
            context,
            comparand: null);
        if (current is not null && !ReferenceEquals(current, context))
            throw new InvalidOperationException(
                "The descriptor authority cannot be rebound to another backend context.");
    }

    internal void RegisterDescriptorSet(
        DescriptorPool pool,
        DescriptorSet descriptorSet,
        bool usesUpdateAfterBind,
        string owner,
        uint setIndex = 0,
        IReadOnlyList<DescriptorBindingInfo>? reflectedBindings = null)
    {
        if (descriptorSet.Handle == 0)
            return;

        VulkanResourceLifetimeTracker tracker = _lifetime.Tracker;
        tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.DescriptorPool, pool.Handle),
            $"{owner}.Pool",
            externallyOwned: false);
        tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.DescriptorSet, descriptorSet.Handle),
            owner,
            externallyOwned: false);
        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            if (!tracker.DescriptorSetLifetimes.TryGetValue(
                    descriptorSet.Handle,
                    out VulkanDescriptorSetLifetimeRecord? state))
            {
                state = new VulkanDescriptorSetLifetimeRecord();
                tracker.DescriptorSetLifetimes.Add(descriptorSet.Handle, state);
            }

            UpdatePoolIndexNoLock(tracker, descriptorSet.Handle, state.Pool.Handle, pool.Handle);
            state.Pool = pool;
            state.UsesUpdateAfterBind = usesUpdateAfterBind;
            state.HasReflection = reflectedBindings is not null;
            state.Owner = owner;
            state.NativePublicationState =
                EVulkanDescriptorNativePublicationState.Known;
            state.Payloads.Clear();
            state.ReflectedImageBindings.Clear();
            if (reflectedBindings is not null)
            {
                for (int index = 0; index < reflectedBindings.Count; index++)
                {
                    DescriptorBindingInfo binding = reflectedBindings[index];
                    if (binding.Set == setIndex && IsLifetimeTrackedImageDescriptorType(binding.DescriptorType))
                        state.ReflectedImageBindings.Add(binding.Binding);
                }
            }

            state.Generation++;
            state.ImagePayloadGeneration++;
            VulkanDescriptorManager.PublishSnapshotNoLock(_lifetime, descriptorSet.Handle, state);
        }
    }

    internal void RegisterDescriptorSets(
        DescriptorPool pool,
        ReadOnlySpan<DescriptorSet> descriptorSets,
        bool usesUpdateAfterBind,
        string owner,
        IReadOnlyList<DescriptorBindingInfo>? reflectedBindings = null)
    {
        for (int index = 0; index < descriptorSets.Length; index++)
            RegisterDescriptorSet(
                pool,
                descriptorSets[index],
                usesUpdateAfterBind,
                owner,
                unchecked((uint)index),
                reflectedBindings);
    }

    internal void UpdateDescriptorSets(
        uint descriptorWriteCount,
        WriteDescriptorSet* descriptorWrites)
    {
        VulkanDeviceContext device = RequireOperationalDevice();
        ApplyDescriptorWrites(
            device,
            descriptorWriteCount,
            descriptorWrites,
            prevalidate: false,
            out _);
    }

    internal bool TryUpdateDescriptorSets(
        uint descriptorWriteCount,
        WriteDescriptorSet* descriptorWrites,
        out string failureReason)
    {
        if (_deviceContext is not { IsOperational: true } device)
        {
            failureReason = $"Cannot update Vulkan descriptors while device state is {_deviceContext?.State}.";
            return false;
        }

        return ApplyDescriptorWrites(
            device,
            descriptorWriteCount,
            descriptorWrites,
            prevalidate: true,
            out failureReason);
    }

    private bool ApplyDescriptorWrites(
        VulkanDeviceContext device,
        uint descriptorWriteCount,
        WriteDescriptorSet* descriptorWrites,
        bool prevalidate,
        out string failureReason)
    {
        ulong[]? dependentCommandBuffers;
        int dependentCommandBufferCount;
        bool invalidatesRecordedCommandBuffers;
        VulkanDescriptorUpdateInvalidation firstInvalidation;
        VulkanResourceLifetimeTracker tracker = _lifetime.Tracker;
        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            if (!TryPrevalidateWritesNoLock(
                    descriptorWriteCount,
                    descriptorWrites,
                    out failureReason))
            {
                if (prevalidate)
                    return false;
                throw new InvalidOperationException(failureReason);
            }

            // Vulkan descriptor updates can invalidate command buffers even when
            // the application republishes the same bytes. Suppress exact no-op
            // batches before entering the driver so reusable command artifacts
            // remain executable.
            if (WritesMatchPublishedPayloadNoLock(
                    tracker,
                    descriptorWriteCount,
                    descriptorWrites))
            {
                failureReason = string.Empty;
                return true;
            }

            invalidatesRecordedCommandBuffers = TryCaptureUpdateInvalidationsNoLock(
                descriptorWriteCount,
                descriptorWrites,
                out dependentCommandBuffers,
                out dependentCommandBufferCount,
                out firstInvalidation);
            try
            {
                device.Api.UpdateDescriptorSets(
                    device.Device,
                    descriptorWriteCount,
                    descriptorWrites,
                    0,
                    null);
            }
            catch
            {
                MarkDescriptorWritesNativeStateUnknownNoLock(
                    descriptorWriteCount,
                    descriptorWrites,
                    "vkUpdateDescriptorSets did not return normally");
                if (dependentCommandBuffers is not null)
                    ArrayPool<ulong>.Shared.Return(dependentCommandBuffers);
                throw;
            }

            try
            {
                // Native state commits first. Semantic publication follows
                // under the same lifetime lock, so retirement cannot race the
                // Vulkan copy. A failed semantic commit quarantines the set.
                ValidateAndRecordWritesNoLock(
                    descriptorWriteCount,
                    descriptorWrites);
            }
            catch
            {
                MarkDescriptorWritesNativeStateUnknownNoLock(
                    descriptorWriteCount,
                    descriptorWrites,
                    "vkUpdateDescriptorSets succeeded but tracker commit failed");
                if (dependentCommandBuffers is not null)
                    ArrayPool<ulong>.Shared.Return(dependentCommandBuffers);
                throw;
            }
        }

        PublishContentUpdate(
            invalidatesRecordedCommandBuffers,
            dependentCommandBuffers,
            dependentCommandBufferCount,
            firstInvalidation,
            "vkUpdateDescriptorSets");
        failureReason = string.Empty;
        return true;
    }

    internal bool TryUpdateDescriptorSetWithTemplate(
        DescriptorSet descriptorSet,
        DescriptorSetLayout descriptorSetLayout,
        PipelineBindPoint bindPoint,
        PipelineLayout pipelineLayout,
        uint setIndex,
        ReadOnlySpan<WriteDescriptorSet> writes)
    {
        if (_deviceContext is not { IsOperational: true } device ||
            descriptorSet.Handle == 0 ||
            descriptorSetLayout.Handle == 0 ||
            writes.Length == 0 ||
            ContainsImageWrites(writes))
        {
            return false;
        }

        DescriptorUpdateTemplateEntry[] entries = new DescriptorUpdateTemplateEntry[writes.Length];
        DescriptorUpdateTemplateSignature[] signature = new DescriptorUpdateTemplateSignature[writes.Length];
        nuint totalSize = 0;
        for (int index = 0; index < writes.Length; index++)
        {
            WriteDescriptorSet write = writes[index];
            if (write.DstSet.Handle != descriptorSet.Handle || write.DescriptorCount == 0)
                return false;

            nuint elementSize = GetTemplateElementSize(write.DescriptorType);
            if (elementSize == 0 || !HasTemplateSource(write))
                return false;
            nuint offset = AlignUp(totalSize, Math.Min(elementSize, (nuint)16));
            entries[index] = new DescriptorUpdateTemplateEntry
            {
                DstBinding = write.DstBinding,
                DstArrayElement = write.DstArrayElement,
                DescriptorCount = write.DescriptorCount,
                DescriptorType = write.DescriptorType,
                Offset = offset,
                Stride = elementSize,
            };
            signature[index] = new DescriptorUpdateTemplateSignature(
                descriptorSetLayout.Handle,
                pipelineLayout.Handle,
                unchecked((int)bindPoint),
                setIndex,
                write.DstBinding,
                write.DstArrayElement,
                write.DescriptorCount,
                write.DescriptorType,
                offset,
                elementSize);
            totalSize = offset + elementSize * write.DescriptorCount;
        }

        if (totalSize == 0 || totalSize > int.MaxValue)
            return false;

        byte[] data = ArrayPool<byte>.Shared.Rent(unchecked((int)totalSize));
        try
        {
            fixed (byte* dataPointer = data)
            {
                for (int index = 0; index < writes.Length; index++)
                    CopyTemplateData(writes[index], dataPointer + entries[index].Offset);
                if (!TryGetOrCreateUpdateTemplate(
                        device,
                        descriptorSetLayout,
                        bindPoint,
                        pipelineLayout,
                        setIndex,
                        entries,
                        signature,
                        out DescriptorUpdateTemplate updateTemplate))
                {
                    return false;
                }

                ulong[]? dependentCommandBuffers;
                int dependentCommandBufferCount;
                bool invalidatesRecordedCommandBuffers;
                VulkanDescriptorUpdateInvalidation firstInvalidation;
                fixed (WriteDescriptorSet* writePointer = writes)
                {
                    using (VulkanFrameLockScope.Enter(
                               _lifetime.Tracker.SyncRoot,
                               EVulkanFrameWaitReason.ResourceLifetimeLock))
                    {
                        if (!TryPrevalidateWritesNoLock(
                                unchecked((uint)writes.Length),
                                writePointer,
                                out _))
                        {
                            return false;
                        }

                        if (WritesMatchPublishedPayloadNoLock(
                                _lifetime.Tracker,
                                unchecked((uint)writes.Length),
                                writePointer))
                        {
                            return true;
                        }

                        invalidatesRecordedCommandBuffers = TryCaptureUpdateInvalidationsNoLock(
                            unchecked((uint)writes.Length),
                            writePointer,
                            out dependentCommandBuffers,
                            out dependentCommandBufferCount,
                            out firstInvalidation);
                        try
                        {
                            device.Api.UpdateDescriptorSetWithTemplate(
                                device.Device,
                                descriptorSet,
                                updateTemplate,
                                dataPointer);
                        }
                        catch
                        {
                            MarkDescriptorWritesNativeStateUnknownNoLock(
                                unchecked((uint)writes.Length),
                                writePointer,
                                "vkUpdateDescriptorSetWithTemplate did not return normally");
                            if (dependentCommandBuffers is not null)
                                ArrayPool<ulong>.Shared.Return(dependentCommandBuffers);
                            throw;
                        }

                        try
                        {
                            ValidateAndRecordWritesNoLock(
                                unchecked((uint)writes.Length),
                                writePointer);
                        }
                        catch
                        {
                            MarkDescriptorWritesNativeStateUnknownNoLock(
                                unchecked((uint)writes.Length),
                                writePointer,
                                "vkUpdateDescriptorSetWithTemplate succeeded but tracker commit failed");
                            if (dependentCommandBuffers is not null)
                                ArrayPool<ulong>.Shared.Return(dependentCommandBuffers);
                            throw;
                        }
                    }
                }

                PublishContentUpdate(
                    invalidatesRecordedCommandBuffers,
                    dependentCommandBuffers,
                    dependentCommandBufferCount,
                    firstInvalidation,
                    "vkUpdateDescriptorSetWithTemplate");
                return true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    internal void DestroyUpdateTemplateCache()
    {
        VulkanDeviceContext? device = _deviceContext;
        using (VulkanFrameLockScope.Enter(
                   _descriptors._descriptorUpdateTemplateCacheLock,
                   EVulkanFrameWaitReason.DescriptorPublicationLock))
        {
            if (device is not null && device.Device.Handle != 0)
            {
                foreach (List<CachedDescriptorUpdateTemplate> bucket in
                         _descriptors._descriptorUpdateTemplateCache.Values)
                {
                    for (int index = 0; index < bucket.Count; index++)
                    {
                        DescriptorUpdateTemplate template = bucket[index].Template;
                        if (template.Handle != 0)
                            device.Api.DestroyDescriptorUpdateTemplate(device.Device, template, null);
                    }
                }
            }

            _descriptors._descriptorUpdateTemplateCache.Clear();
        }
    }

    internal bool IsDescriptorHeapActive
    {
        get
        {
            VulkanDescriptorHeapState heap = _descriptors.Heap;
            return heap.StorageReady &&
                   heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap &&
                   heap.NativeApiAvailable &&
                   heap.NativeFunctions is not null &&
                   heap.SamplerStorage.IsReady &&
                   heap.ResourceStorage.IsReady;
        }
    }

    internal DescriptorHeapProgramLayout? CreateDescriptorHeapProgramLayout(
        IReadOnlyList<DescriptorBindingInfo> bindings,
        string programName,
        out string reason)
    {
        reason = string.Empty;
        if (!IsDescriptorHeapActive)
        {
            reason = "descriptor heap is not the active descriptor backend.";
            return null;
        }
        if (bindings.Count == 0)
            return DescriptorHeapProgramLayout.Empty;

        uint nextPushOffset = 0;
        DescriptorHeapBindingLayout[] layouts = new DescriptorHeapBindingLayout[bindings.Count];
        DescriptorSetAndBindingMappingEXTNative[] mappings = new DescriptorSetAndBindingMappingEXTNative[bindings.Count];
        Dictionary<DescriptorHeapBindingKey, DescriptorHeapBindingLayout> lookup = new(bindings.Count);
        for (int index = 0; index < bindings.Count; index++)
        {
            DescriptorBindingInfo binding = bindings[index];
            if (!TryCreateHeapBindingLayout(binding, ref nextPushOffset, out DescriptorHeapBindingLayout? layout, out reason) ||
                layout is null)
            {
                reason = $"program '{programName}' binding set={binding.Set} binding={binding.Binding} type={binding.DescriptorType}: {reason}";
                return null;
            }
            layouts[index] = layout;
            lookup[new DescriptorHeapBindingKey(binding.Set, binding.Binding)] = layout;
            mappings[index] = CreateHeapMapping(layout);
        }

        if (_descriptors.Heap.Properties.MaxPushDataSize > 0 &&
            nextPushOffset > _descriptors.Heap.Properties.MaxPushDataSize)
        {
            reason = $"program '{programName}' descriptor heap push-data layout needs {nextPushOffset} bytes, maxPushDataSize={_descriptors.Heap.Properties.MaxPushDataSize}.";
            return null;
        }
        Debug.Vulkan(
            "[Vulkan.DescriptorHeap.Mapping] program='{0}' bindings={1} pushBytes={2}.",
            programName,
            bindings.Count,
            nextPushOffset);
        return new DescriptorHeapProgramLayout(layouts, mappings, lookup, nextPushOffset);
    }

    private bool TryCreateHeapBindingLayout(
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
        DescriptorType resourceType = ResolveHeapResourceDescriptorType(binding.DescriptorType);
        ulong resourceStride = hasResource ? ResolveHeapDescriptorStride(resourceType) : 0;
        ulong samplerStride = hasSampler ? ResolveHeapDescriptorStride(DescriptorType.Sampler) : 0;
        uint resourceOffset = uint.MaxValue;
        uint samplerOffset = uint.MaxValue;
        if (hasResource)
        {
            resourceOffset = nextPushOffset;
            nextPushOffset += sizeof(uint);
        }
        if (hasSampler)
        {
            if (binding.DescriptorType == DescriptorType.Sampler)
            {
                resourceOffset = nextPushOffset;
                nextPushOffset += sizeof(uint);
            }
            else
            {
                samplerOffset = nextPushOffset;
                nextPushOffset += sizeof(uint);
            }
        }
        layout = new DescriptorHeapBindingLayout(
            new DescriptorHeapBindingKey(binding.Set, binding.Binding),
            binding.DescriptorType,
            resourceType,
            descriptorCount,
            hasResource,
            hasSampler,
            resourceOffset,
            samplerOffset,
            checked((uint)Math.Min(resourceStride, uint.MaxValue)),
            checked((uint)Math.Min(samplerStride, uint.MaxValue)));
        return true;
    }

    private static DescriptorSetAndBindingMappingEXTNative CreateHeapMapping(
        DescriptorHeapBindingLayout layout)
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
            SourceData = new DescriptorMappingSourceDataEXTNative { PushIndex = pushIndex },
        };
    }

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
        if (!IsDescriptorHeapActive)
        {
            reason = "descriptor heap is not active.";
            return false;
        }

        DescriptorHeapProgramLayout? layout = program.DescriptorHeapLayout;
        if (layout is null ||
            !layout.TryGetBinding(binding.Set, binding.Binding, out DescriptorHeapBindingLayout bindingLayout))
        {
            reason = $"descriptor heap mapping is missing for set={binding.Set} binding={binding.Binding}.";
            return false;
        }

        descriptorCount = Math.Max(1u, descriptorCount);
        if (bindingLayout.HasResource)
        {
            if (!TryWriteHeapResourceBinding(
                    bindingLayout,
                    bufferInfos,
                    imageInfos,
                    texelBufferViews,
                    descriptorCount,
                    out uint resourceIndex,
                    out reason))
            {
                return false;
            }
            payload.SetDword(bindingLayout.ResourcePushOffset, resourceIndex);
        }

        if (bindingLayout.HasSampler)
        {
            if (!TryWriteHeapSamplerBinding(
                    bindingLayout,
                    imageInfos,
                    descriptorCount,
                    out uint samplerIndex,
                    out reason))
            {
                return false;
            }
            uint pushOffset = bindingLayout.DescriptorType == DescriptorType.Sampler
                ? bindingLayout.ResourcePushOffset
                : bindingLayout.SamplerPushOffset;
            payload.SetDword(pushOffset, samplerIndex);
        }

        return true;
    }

    internal bool TryWriteCombinedImageSamplerHeapPayload(
        DescriptorImageInfo imageInfo,
        DescriptorHeapPushDataPayload payload,
        out string reason)
    {
        reason = string.Empty;
        if (!IsDescriptorHeapActive)
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
            ResourceStride: checked((uint)ResolveHeapDescriptorStride(DescriptorType.SampledImage)),
            SamplerStride: checked((uint)ResolveHeapDescriptorStride(DescriptorType.Sampler)));
        if (!TryWriteHeapResourceBinding(layout, null, &imageInfo, null, 1u, out uint resourceIndex, out reason) ||
            !TryWriteHeapSamplerBinding(layout, &imageInfo, 1u, out uint samplerIndex, out reason))
        {
            return false;
        }
        payload.SetDword(0u, resourceIndex);
        payload.SetDword(sizeof(uint), samplerIndex);
        return true;
    }

    private bool TryWriteHeapResourceBinding(
        DescriptorHeapBindingLayout layout,
        DescriptorBufferInfo* bufferInfos,
        DescriptorImageInfo* imageInfos,
        BufferView* texelBufferViews,
        uint descriptorCount,
        out uint heapIndex,
        out string reason)
    {
        heapIndex = 0;
        if (!TryAllocateHeapRange(
                sampler: false,
                layout.ResourceDescriptorType,
                descriptorCount,
                out ulong destinationOffset,
                out ulong destinationSize,
                out reason))
        {
            return false;
        }

        int count = checked((int)descriptorCount);
        ResourceDescriptorInfoEXTNative[] resourcesArray = new ResourceDescriptorInfoEXTNative[count];
        DeviceAddressRangeEXTNative[] rangesArray = new DeviceAddressRangeEXTNative[count];
        ImageDescriptorInfoEXTNative[] imagesArray = new ImageDescriptorInfoEXTNative[count];
        ImageViewCreateInfo[] imageViewsArray = new ImageViewCreateInfo[count];
        TexelBufferDescriptorInfoEXTNative[] texelBuffersArray = new TexelBufferDescriptorInfoEXTNative[count];
        VulkanBackendObjectContext context = RequireBackendContext();
        fixed (ResourceDescriptorInfoEXTNative* resources = resourcesArray)
        fixed (DeviceAddressRangeEXTNative* ranges = rangesArray)
        fixed (ImageDescriptorInfoEXTNative* images = imagesArray)
        fixed (ImageViewCreateInfo* imageViews = imageViewsArray)
        fixed (TexelBufferDescriptorInfoEXTNative* texelBuffers = texelBuffersArray)
        {
        for (uint index = 0; index < descriptorCount; index++)
        {
            resources[index] = new ResourceDescriptorInfoEXTNative
            {
                SType = VulkanDescriptorHeapExt.ResourceDescriptorInfoSType,
                PNext = null,
                Type = layout.ResourceDescriptorType,
            };
            switch (layout.ResourceDescriptorType)
            {
                case DescriptorType.UniformBuffer:
                case DescriptorType.StorageBuffer:
                    if (bufferInfos is null ||
                        !TryCreateAddressRange(context, bufferInfos[index], out ranges[index], out reason))
                    {
                        return false;
                    }
                    resources[index].Data.AddressRange = ranges + index;
                    break;
                case DescriptorType.SampledImage:
                case DescriptorType.StorageImage:
                case DescriptorType.InputAttachment:
                    if (imageInfos is null ||
                        !_resources.Images.TryGetDescriptorHeapCreateInfo(
                            imageInfos[index].ImageView,
                            out imageViews[index]))
                    {
                        reason = imageInfos is null
                            ? "image descriptor heap write has no image data."
                            : $"image view 0x{imageInfos[index].ImageView.Handle:X} has no descriptor heap create-info metadata.";
                        return false;
                    }
                    images[index] = new ImageDescriptorInfoEXTNative
                    {
                        SType = VulkanDescriptorHeapExt.ImageDescriptorInfoSType,
                        PNext = null,
                        View = imageViews + index,
                        Layout = imageInfos[index].ImageLayout,
                    };
                    resources[index].Data.Image = images + index;
                    break;
                case DescriptorType.UniformTexelBuffer:
                case DescriptorType.StorageTexelBuffer:
                    if (texelBufferViews is null ||
                        !TryCreateTexelBufferInfo(
                            context,
                            texelBufferViews[index],
                            out texelBuffers[index],
                            out reason))
                    {
                        return false;
                    }
                    resources[index].Data.TexelBuffer = texelBuffers + index;
                    break;
                default:
                    reason = $"descriptor type {layout.ResourceDescriptorType} is not a supported resource heap descriptor.";
                    return false;
            }
        }

        if (!TryWriteResourceDescriptors(
                descriptorCount,
                resources,
                destinationOffset,
                destinationSize,
                out reason))
        {
            return false;
        }
            heapIndex = checked((uint)(destinationOffset / layout.ResourceStride));
            return true;
        }
    }

    private bool TryWriteHeapSamplerBinding(
        DescriptorHeapBindingLayout layout,
        DescriptorImageInfo* imageInfos,
        uint descriptorCount,
        out uint heapIndex,
        out string reason)
    {
        heapIndex = 0;
        if (imageInfos is null)
        {
            reason = "sampler descriptor heap write has no image/sampler descriptor data.";
            return false;
        }
        if (!TryAllocateHeapRange(
                sampler: true,
                DescriptorType.Sampler,
                descriptorCount,
                out ulong destinationOffset,
                out ulong destinationSize,
                out reason))
        {
            return false;
        }

        SamplerCreateInfo[] samplersArray = new SamplerCreateInfo[checked((int)descriptorCount)];
        fixed (SamplerCreateInfo* samplers = samplersArray)
        {
        for (uint index = 0; index < descriptorCount; index++)
        {
            Sampler sampler = imageInfos[index].Sampler;
            if (!_descriptors.TryGetSamplerCreateInfo(sampler, out samplers[index]))
            {
                reason = $"sampler 0x{sampler.Handle:X} has no descriptor heap create-info metadata.";
                return false;
            }
        }
        if (!TryWriteSamplerDescriptors(
                descriptorCount,
                samplers,
                destinationOffset,
                destinationSize,
                out reason))
        {
            return false;
        }
            heapIndex = checked((uint)(destinationOffset / layout.SamplerStride));
            return true;
        }
    }

    private bool TryAllocateHeapRange(
        bool sampler,
        DescriptorType descriptorType,
        uint descriptorCount,
        out ulong offset,
        out ulong size,
        out string reason)
    {
        VulkanDescriptorHeapState heap = _descriptors.Heap;
        VulkanDescriptorHeapStorage storage = sampler ? heap.SamplerStorage : heap.ResourceStorage;
        ulong stride = ResolveHeapDescriptorStride(descriptorType);
        offset = 0;
        size = 0;
        reason = string.Empty;
        if (!storage.IsReady)
        {
            reason = "descriptor heap storage is not ready.";
            return false;
        }

        stride = Math.Max(stride, 1ul);
        ref ulong cursor = ref (sampler
            ? ref heap.SamplerHighWaterBytes
            : ref heap.ResourceHighWaterBytes);
        offset = AlignHeapUp(cursor, stride);
        size = checked(stride * Math.Max(1u, descriptorCount));
        if (offset > storage.Size || size > storage.Size - offset)
        {
            reason = $"descriptor heap capacity exhausted (offset={offset}, size={size}, capacity={storage.Size}).";
            heap.AllocationFailureCount++;
            Debug.VulkanWarningEvery(
                "Vulkan.DescriptorHeap.CapacityExhausted",
                TimeSpan.FromSeconds(2),
                "[Vulkan.DescriptorHeap.AllocationFailure] offset={0} bytes={1} capacity={2} failures={3}.",
                offset,
                size,
                storage.Size,
                heap.AllocationFailureCount);
            return false;
        }
        cursor = offset + size;
        return true;
    }

    private bool TryWriteSamplerDescriptors(
        uint count,
        SamplerCreateInfo* samplers,
        ulong offset,
        ulong size,
        out string reason)
    {
        VulkanDescriptorHeapState heap = _descriptors.Heap;
        if (heap.NativeFunctions is null || samplers is null || count == 0)
        {
            reason = "descriptor heap sampler write has no API, samplers, or count.";
            return false;
        }
        VulkanDeviceContext device = RequireOperationalDevice();
        VulkanBackendObjectContext context = RequireBackendContext();
        VulkanMappedMemorySlice mappedSlice = heap.SamplerStorage.MappedMemorySlice;
        if (!context.Resources.Buffers.TryAcquireWrite(context, in mappedSlice, out VulkanMappedMemoryWriteLease lease))
        {
            reason = "descriptor heap sampler mapping lease could not be acquired.";
            return false;
        }
        Result result;
        using (lease)
        {
            fixed (byte* address = lease.Bytes)
            {
                HostAddressRangeEXTNative destination = new() { Address = address + checked((nint)offset), Size = checked((nuint)size) };
                result = heap.NativeFunctions.WriteSamplerDescriptors(device.Device, count, samplers, &destination);
            }
        }
        if (result != Result.Success)
        {
            reason = $"vkWriteSamplerDescriptorsEXT failed ({result}).";
            return false;
        }
        heap.SamplerWriteCount += count;
        heap.FrameWrites += count;
        MarkHeapDirty(heap.SamplerStorage, offset, size, ref heap.SamplerDirtyStart, ref heap.SamplerDirtyEnd);
        RecordTableGeneration();
        heap.SamplerHighWaterBytes = Math.Max(heap.SamplerHighWaterBytes, offset + size);
        reason = string.Empty;
        return true;
    }

    private bool TryWriteResourceDescriptors(
        uint count,
        ResourceDescriptorInfoEXTNative* resources,
        ulong offset,
        ulong size,
        out string reason)
    {
        VulkanDescriptorHeapState heap = _descriptors.Heap;
        if (heap.NativeFunctions is null || resources is null || count == 0)
        {
            reason = "descriptor heap resource write has no API, resources, or count.";
            return false;
        }
        VulkanDeviceContext device = RequireOperationalDevice();
        VulkanBackendObjectContext context = RequireBackendContext();
        VulkanMappedMemorySlice mappedSlice = heap.ResourceStorage.MappedMemorySlice;
        if (!context.Resources.Buffers.TryAcquireWrite(context, in mappedSlice, out VulkanMappedMemoryWriteLease lease))
        {
            reason = "descriptor heap resource mapping lease could not be acquired.";
            return false;
        }
        Result result;
        using (lease)
        {
            fixed (byte* address = lease.Bytes)
            {
                HostAddressRangeEXTNative destination = new() { Address = address + checked((nint)offset), Size = checked((nuint)size) };
                result = heap.NativeFunctions.WriteResourceDescriptors(device.Device, count, resources, &destination);
            }
        }
        if (result != Result.Success)
        {
            reason = $"vkWriteResourceDescriptorsEXT failed ({result}).";
            return false;
        }
        heap.ResourceWriteCount += count;
        heap.FrameWrites += count;
        MarkHeapDirty(heap.ResourceStorage, offset, size, ref heap.ResourceDirtyStart, ref heap.ResourceDirtyEnd);
        reason = string.Empty;
        RecordTableGeneration();
        heap.ResourceHighWaterBytes = Math.Max(heap.ResourceHighWaterBytes, offset + size);
        return true;
    }

    private bool TryGetOrCreateUpdateTemplate(
        VulkanDeviceContext device,
        DescriptorSetLayout descriptorSetLayout,
        PipelineBindPoint bindPoint,
        PipelineLayout pipelineLayout,
        uint setIndex,
        DescriptorUpdateTemplateEntry[] entries,
        DescriptorUpdateTemplateSignature[] signature,
        out DescriptorUpdateTemplate updateTemplate)
    {
        updateTemplate = default;
        ulong hash = ComputeTemplateHash(signature);
        using (VulkanFrameLockScope.Enter(
                   _descriptors._descriptorUpdateTemplateCacheLock,
                   EVulkanFrameWaitReason.DescriptorPublicationLock))
        {
            if (_descriptors._descriptorUpdateTemplateCache.TryGetValue(
                    hash,
                    out List<CachedDescriptorUpdateTemplate>? bucket))
            {
                for (int index = 0; index < bucket.Count; index++)
                {
                    CachedDescriptorUpdateTemplate cached = bucket[index];
                    if (!TemplateSignaturesEqual(cached.Signature, signature))
                        continue;
                    updateTemplate = cached.Template;
                    return true;
                }
            }

            fixed (DescriptorUpdateTemplateEntry* entriesPointer = entries)
            {
                DescriptorUpdateTemplateCreateInfo createInfo = new()
                {
                    SType = StructureType.DescriptorUpdateTemplateCreateInfo,
                    DescriptorUpdateEntryCount = unchecked((uint)entries.Length),
                    PDescriptorUpdateEntries = entriesPointer,
                    TemplateType = DescriptorUpdateTemplateType.DescriptorSet,
                    DescriptorSetLayout = descriptorSetLayout,
                    PipelineBindPoint = bindPoint,
                    PipelineLayout = pipelineLayout,
                    Set = setIndex,
                };
                if (device.Api.CreateDescriptorUpdateTemplate(
                        device.Device,
                        &createInfo,
                        null,
                        out updateTemplate) != Result.Success)
                {
                    return false;
                }
            }

            CachedDescriptorUpdateTemplate created = new()
            {
                Template = updateTemplate,
                Signature = signature,
                Hash = hash,
            };
            if (bucket is null)
                _descriptors._descriptorUpdateTemplateCache.Add(hash, bucket = []);
            bucket.Add(created);
            return true;
        }
    }

    internal void RetireDescriptorSet(
        DescriptorPool descriptorPool,
        DescriptorSet descriptorSet)
    {
        if (descriptorPool.Handle == 0 || descriptorSet.Handle == 0)
            return;

        VulkanRetirementTicket ticket = CaptureRetirementTicket(
            new VulkanResourceLifetimeKey(ObjectType.DescriptorSet, descriptorSet.Handle),
            nameof(RetireDescriptorSet));
        int frameSlot = _resources.FramebufferRetirementFrameSlot;
        using (VulkanFrameLockScope.Enter(
                   _lifetime.Retirement.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            if (_lifetime.Retirement.AllDescriptorPoolHandles.Contains(descriptorPool.Handle))
                return;

            VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(
                frameSlot,
                descriptorSet.Handle,
                new RetiredDescriptorSet(descriptorPool, descriptorSet, ticket),
                _lifetime.Retirement.DescriptorSets,
                _lifetime.Retirement.DescriptorSetHandles,
                _lifetime.Retirement.AllDescriptorSetHandles);
        }
    }

    internal void RetireDescriptorPool(DescriptorPool descriptorPool)
    {
        if (descriptorPool.Handle == 0)
            return;

        using (VulkanFrameLockScope.Enter(
                   _lifetime.Retirement.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
            if (!_lifetime.Retirement.AllDescriptorPoolHandles.Add(descriptorPool.Handle))
                return;

        VulkanRetirementTicket ticket;
        try
        {
            ticket = CaptureDescriptorPoolRetirementTicket(
                descriptorPool,
                nameof(RetireDescriptorPool));
        }
        catch
        {
            using (VulkanFrameLockScope.Enter(
                       _lifetime.Retirement.SyncRoot,
                       EVulkanFrameWaitReason.ResourceLifetimeLock))
                _lifetime.Retirement.AllDescriptorPoolHandles.Remove(descriptorPool.Handle);
            throw;
        }

        int frameSlot = _resources.FramebufferRetirementFrameSlot;
        using (VulkanFrameLockScope.Enter(
                   _lifetime.Retirement.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            RemoveRetiredDescriptorSetsForPoolNoLock(descriptorPool.Handle);
            _lifetime.Retirement.DescriptorPoolHandles[frameSlot].Add(descriptorPool.Handle);
            _lifetime.Retirement.DescriptorPools[frameSlot].Add(
                new RetiredDescriptorPool(descriptorPool, ticket));
        }
    }

    internal bool TryAcquireMeshDescriptorPoolSlab(
        DescriptorPoolSize[] perAllocationPoolSizes,
        int setsPerAllocation,
        bool updateAfterBind,
        out MeshDescriptorPoolSlabLease? lease)
    {
        lease = null;
        if (setsPerAllocation <= 0 || perAllocationPoolSizes.Length == 0)
            return false;

        MeshDescriptorPoolSlabKey key = new(
            ComputePoolSizeFingerprint(perAllocationPoolSizes),
            setsPerAllocation,
            updateAfterBind);
        using (VulkanFrameLockScope.Enter(
                   _descriptors.MeshDescriptorPoolSlabLock,
                   EVulkanFrameWaitReason.DescriptorArena))
        {
            if (_descriptors.MeshDescriptorPoolSlabs.TryGetValue(
                    key,
                    out List<MeshDescriptorPoolSlab>? slabs))
            {
                for (int index = 0; index < slabs.Count; index++)
                {
                    MeshDescriptorPoolSlab existing = slabs[index];
                    if (existing.IssuedAllocationCount >= MeshDescriptorPoolSlabAllocationCapacity)
                        continue;
                    existing.IssuedAllocationCount++;
                    existing.LiveAllocationCount++;
                    lease = new MeshDescriptorPoolSlabLease(existing);
                    return true;
                }
            }
            else
            {
                _descriptors.MeshDescriptorPoolSlabs.Add(key, slabs = []);
            }

            DescriptorPoolSize[] slabPoolSizes = new DescriptorPoolSize[perAllocationPoolSizes.Length];
            for (int index = 0; index < perAllocationPoolSizes.Length; index++)
            {
                DescriptorPoolSize size = perAllocationPoolSizes[index];
                size.DescriptorCount = checked(
                    size.DescriptorCount * MeshDescriptorPoolSlabAllocationCapacity);
                slabPoolSizes[index] = size;
            }

            DescriptorPool pool;
            fixed (DescriptorPoolSize* poolSizes = slabPoolSizes)
            {
                DescriptorPoolCreateInfo createInfo = new()
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit |
                        (updateAfterBind ? DescriptorPoolCreateFlags.UpdateAfterBindBit : 0),
                    PoolSizeCount = unchecked((uint)slabPoolSizes.Length),
                    PPoolSizes = poolSizes,
                    MaxSets = checked((uint)(setsPerAllocation * MeshDescriptorPoolSlabAllocationCapacity)),
                };
                VulkanDeviceContext device = RequireOperationalDevice();
                if (device.Api.CreateDescriptorPool(device.Device, ref createInfo, null, out pool) != Result.Success)
                    return false;
            }

            _lifetime.Tracker.RegisterResource(
                new VulkanResourceLifetimeKey(ObjectType.DescriptorPool, pool.Handle),
                "MeshDescriptorPoolSlab",
                externallyOwned: false);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanDescriptorPoolCreate();
            RuntimeEngine.Rendering.Stats.Vulkan.AdjustVulkanMeshDescriptorOwnership(
                allocationVariants: 0,
                pools: 1,
                allocatedSets: 0,
                reservedSets: 0);
            MeshDescriptorPoolSlab slab = new()
            {
                Key = key,
                Pool = pool,
                IssuedAllocationCount = 1,
                LiveAllocationCount = 1,
            };
            slabs.Add(slab);
            lease = new MeshDescriptorPoolSlabLease(slab);
            return true;
        }
    }

    internal void ReleaseMeshDescriptorPoolSlab(
        MeshDescriptorPoolSlabLease? lease,
        DescriptorSet[][] descriptorSets,
        uint locallyOwnedSetMask)
    {
        if (lease is null || lease.Released)
            return;

        DescriptorPool pool = lease.Pool;
        bool retireWholePool = false;
        using (VulkanFrameLockScope.Enter(
                   _descriptors.MeshDescriptorPoolSlabLock,
                   EVulkanFrameWaitReason.DescriptorArena))
        {
            if (lease.Released)
                return;
            lease.Released = true;
            MeshDescriptorPoolSlab slab = lease.Slab;
            slab.LiveAllocationCount--;
            if (slab.LiveAllocationCount < 0)
                throw new InvalidOperationException("Mesh descriptor pool slab lease underflow.");
            if (slab.LiveAllocationCount == 0)
            {
                if (_descriptors.MeshDescriptorPoolSlabs.TryGetValue(
                        slab.Key,
                        out List<MeshDescriptorPoolSlab>? slabs))
                {
                    slabs.Remove(slab);
                    if (slabs.Count == 0)
                        _descriptors.MeshDescriptorPoolSlabs.Remove(slab.Key);
                }
                retireWholePool = true;
            }
        }

        if (retireWholePool)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.AdjustVulkanMeshDescriptorOwnership(
                allocationVariants: 0,
                pools: -1,
                allocatedSets: 0,
                reservedSets: 0);
            RetireDescriptorPool(pool);
            return;
        }

        for (int frameIndex = 0; frameIndex < descriptorSets.Length; frameIndex++)
        {
            DescriptorSet[] sets = descriptorSets[frameIndex];
            for (int setIndex = 0; setIndex < sets.Length; setIndex++)
            {
                if ((locallyOwnedSetMask & (1u << setIndex)) == 0 || sets[setIndex].Handle == 0)
                    continue;
                RetireDescriptorSet(pool, sets[setIndex]);
            }
        }
    }

    private VulkanRetirementTicket CaptureDescriptorPoolRetirementTicket(
        DescriptorPool pool,
        string owner)
    {
        VulkanResourceLifetimeKey poolKey = new(ObjectType.DescriptorPool, pool.Handle);
        VulkanRetirementTicket ticket = CaptureRetirementTicket(poolKey, owner);
        VulkanResourceLifetimeTracker tracker = _lifetime.Tracker;
        ulong[] ownedSets;
        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
            ownedSets = tracker.DescriptorSetsByPool.TryGetValue(pool.Handle, out HashSet<ulong>? sets)
                ? [.. sets]
                : [];

        for (int index = 0; index < ownedSets.Length; index++)
        {
            VulkanResourceLifetimeKey setKey = new(ObjectType.DescriptorSet, ownedSets[index]);
            VulkanRetirementTicket setTicket = CaptureRetirementTicket(
                setKey,
                $"{owner}.DescriptorSet");
            ticket = ticket.Merge(setTicket);
        }

        return ticket;
    }

    private VulkanRetirementTicket CaptureRetirementTicket(
        VulkanResourceLifetimeKey key,
        string owner)
    {
        if (!key.IsValid)
            return VulkanRetirementTicket.None;

        // Descriptor lifetime does not retain command ownership. Resolve the
        // generation-local command port only for this retirement operation,
        // after fencing new recordings and before mutating the ledger.
        VulkanResourceLifetimeTracker tracker = _lifetime.Tracker;
        tracker.FenceResourceRecordingAdmission(key, owner);
        _lifetime.PublishTrackingDependenciesBeforeRetirement(key);

        ulong[] dependentCommandBuffers = [];
        VulkanRetirementTicket ticket;
        ulong generation;
        string resourceOwner;
        int invalidatedDescriptorSetCount = 0;
        using (VulkanFrameLockScope.Enter(
                   tracker.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            VulkanResourceLifetimeRecord resource = tracker.GetOrRegisterResourceNoLock(key, owner);
            generation = resource.Generation;
            resourceOwner = resource.Owner;
            if ((resource.State & (EVulkanResourceLifetimeState.Destroyed |
                                   EVulkanResourceLifetimeState.PendingRetirement)) != 0)
            {
                return resource.RetirementTicket;
            }

            _resources.UpdateResourceCompletionStateNoLock(resource);
            ticket = new VulkanRetirementTicket(
                resource.Pins.LastGraphicsSequence,
                resource.Pins.LastTransferSequence,
                resource.Pins.LastOtherSequence,
                Stopwatch.GetTimestamp(),
                resource.Generation,
                (resource.State & EVulkanResourceLifetimeState.External) != 0,
                VulkanRetirementPinSet.Single(key, resource.Generation));
            resource.RetirementSerial = unchecked((ulong)Interlocked.Increment(ref tracker.RetirementSerial));
            resource.State |= EVulkanResourceLifetimeState.PendingRetirement;
            resource.RetirementTicket = ticket;
            tracker.PublishedResourceGenerations[key] = 0;
            invalidatedDescriptorSetCount = _descriptors.InvalidateResourceReferencesNoLock(_lifetime, key);
            dependentCommandBuffers = CaptureCurrentGenerationDependentsNoLock(tracker, key, generation);
        }

        if (dependentCommandBuffers.Length != 0)
        {
            VulkanExactInvalidationResult result =
                _resources.SynchronousCommands.CommandRuntime.InvalidateCachedCommandBuffers(
                dependentCommandBuffers,
                $"retiring {key} generation {generation}");
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanExactResourceInvalidation(
                result.ExactVariantsDirtied,
                result.ExactCommandChainsDirtied,
                result.UnrelatedVariantsPreserved,
                result.GlobalFallbackInvalidations);
            Debug.VulkanEvery(
                $"Vulkan.ResourceLifetime.RetirementInvalidation.{key.Type}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.ResourceLifetime] Exact retirement invalidation resource={0} generation={1} owner={2} dependentCommandBuffers={3} variantsDirtied={4} chainsDirtied={5} unrelatedVariantsPreserved={6} globalFallbacks={7}.",
                key,
                generation,
                resourceOwner,
                dependentCommandBuffers.Length,
                result.ExactVariantsDirtied,
                result.ExactCommandChainsDirtied,
                result.UnrelatedVariantsPreserved,
                result.GlobalFallbackInvalidations);
        }

        if (invalidatedDescriptorSetCount != 0)
        {
            Debug.VulkanEvery(
                $"Vulkan.ResourceLifetime.TargetedDescriptorInvalidation.{key.Type}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.ResourceLifetime] Targeted descriptor invalidation resource={0} generation={1} descriptorSets={2}.",
                key,
                generation,
                invalidatedDescriptorSetCount);
        }

        return ticket;
    }

    private static ulong[] CaptureCurrentGenerationDependentsNoLock(
        VulkanResourceLifetimeTracker tracker,
        VulkanResourceLifetimeKey key,
        ulong generation)
    {
        if (!tracker.ResourceCommandBufferDependencies.TryGetValue(key, out HashSet<ulong>? dependents) ||
            dependents.Count == 0)
        {
            return [];
        }

        ulong[] result = new ulong[dependents.Count];
        int count = 0;
        foreach (ulong commandBufferHandle in dependents)
        {
            if (!tracker.CommandBufferLifetimes.TryGetValue(
                    commandBufferHandle,
                    out VulkanCommandBufferLifetimeRecord? lifetime) ||
                !lifetime.Dependencies.TryGetValue(key, out ulong recordedGeneration) ||
                recordedGeneration != generation)
            {
                continue;
            }

            result[count++] = commandBufferHandle;
        }

        if (count != result.Length)
            Array.Resize(ref result, count);
        return result;
    }

    private void ValidateAndRecordWritesNoLock(
        uint writeCount,
        WriteDescriptorSet* writes)
    {
        if (writeCount == 0 || writes is null)
            return;

        VulkanResourceLifetimeTracker tracker = _lifetime.Tracker;
        HashSet<ulong> changedSets = tracker.ChangedDescriptorSetsScratch.Value!;
        changedSets.Clear();
        try
        {
            for (int writeIndex = 0; writeIndex < writeCount; writeIndex++)
            {
                WriteDescriptorSet write = writes[writeIndex];
                if (write.DstSet.Handle == 0)
                    continue;

                VulkanResourceLifetimeKey setKey = new(ObjectType.DescriptorSet, write.DstSet.Handle);
                if (!tracker.ResourceLifetimes.TryGetValue(
                        setKey,
                        out VulkanResourceLifetimeRecord? setResource) ||
                    !tracker.DescriptorSetLifetimes.TryGetValue(
                        write.DstSet.Handle,
                        out VulkanDescriptorSetLifetimeRecord? setState))
                {
                    throw new InvalidOperationException(
                        $"Descriptor set {setKey} was not registered before update.");
                }
                if ((setResource.State & (EVulkanResourceLifetimeState.PendingRetirement |
                                          EVulkanResourceLifetimeState.Destroyed)) != 0)
                {
                    throw new InvalidOperationException($"Cannot update retired Vulkan descriptor set {setKey}.");
                }

                if (setState.NativePublicationState !=
                    EVulkanDescriptorNativePublicationState.Known)
                {
                    throw new InvalidOperationException(
                        $"Descriptor set {setKey} has unknown native publication state and must be recreated.");
                }

                bool setUseCompleted = _resources.UpdateResourceCompletionStateNoLock(setResource);
                bool bindingSupportsUpdateAfterBind =
                    setState.UsesUpdateAfterBind && CanUseUpdateAfterBind(write.DescriptorType);
                if (!setUseCompleted && !bindingSupportsUpdateAfterBind)
                {
                    throw new InvalidOperationException(
                        $"Cannot update in-flight Vulkan descriptor set {setKey}; binding={write.DstBinding} type={write.DescriptorType} was not registered for update-after-bind.");
                }

                for (uint descriptorIndex = 0; descriptorIndex < write.DescriptorCount; descriptorIndex++)
                {
                    (uint Binding, uint Element) bindingKey =
                        (write.DstBinding, write.DstArrayElement + descriptorIndex);
                    VulkanDescriptorReferencePair references = ResolveReferences(write, descriptorIndex);
                    ValidateAndPropagateReferenceNoLock(setKey, setResource, references.First, setUseCompleted);
                    ValidateAndPropagateReferenceNoLock(setKey, setResource, references.Second, setUseCompleted);
                    if (!setState.References.TryGetValue(bindingKey, out VulkanDescriptorReferencePair previous) ||
                        previous != references)
                    {
                        setState.References[bindingKey] = references;
                        changedSets.Add(write.DstSet.Handle);
                    }

                    if (write.PImageInfo is not null && IsLifetimeTrackedImageDescriptorType(write.DescriptorType))
                    {
                        DescriptorImageInfo info = write.PImageInfo[descriptorIndex];
                        VulkanDescriptorImageReference image = new(info.ImageView, info.ImageLayout, write.DescriptorType);
                        if (!setState.ImageReferences.TryGetValue(bindingKey, out VulkanDescriptorImageReference previousImage) ||
                            previousImage != image)
                        {
                            setState.ImageReferences[bindingKey] = image;
                            setState.ImagePayloadGeneration++;
                            changedSets.Add(write.DstSet.Handle);
                        }
                    }
                    else if (setState.ImageReferences.Remove(bindingKey))
                    {
                        setState.ImagePayloadGeneration++;
                        changedSets.Add(write.DstSet.Handle);
                    }

                    if (TryCaptureDescriptorPayloadNoLock(
                            tracker,
                            write,
                            descriptorIndex,
                            out VulkanDescriptorPayload payload))
                    {
                        bool hadPreviousPayload = setState.Payloads.TryGetValue(
                            bindingKey,
                            out VulkanDescriptorPayload previousPayload);
                        if (!hadPreviousPayload || previousPayload != payload)
                        {
                            if (hadPreviousPayload &&
                                VulkanMeshRenderingConventions.DescriptorTraceEnabled &&
                                Interlocked.Increment(ref _payloadChangeDiagnosticCount) <= 128)
                            {
                                Debug.WriteAuxiliaryLog(
                                    "vulkan-descriptor-payload-changes.log",
                                    $"set=0x{write.DstSet.Handle:X} owner={setState.Owner} binding={bindingKey.Binding} element={bindingKey.Element} old={(hadPreviousPayload ? previousPayload : default)} new={payload}");
                            }

                            setState.Payloads[bindingKey] = payload;
                            changedSets.Add(write.DstSet.Handle);
                        }
                    }
                    else
                    {
                        setState.Payloads.Remove(bindingKey);
                        changedSets.Add(write.DstSet.Handle);
                    }
                }
            }

            foreach (ulong descriptorSetHandle in changedSets)
            {
                VulkanDescriptorSetLifetimeRecord state = tracker.DescriptorSetLifetimes[descriptorSetHandle];
                state.Generation++;
                VulkanDescriptorManager.PublishSnapshotNoLock(_lifetime, descriptorSetHandle, state);
            }
        }
        finally
        {
            changedSets.Clear();
        }
    }

    /// <summary>
    /// Quarantines every touched descriptor set when the native/semantic
    /// publication boundary cannot be proven. Existing pins are retained and
    /// proposed references are added conservatively; no later recording or
    /// submission may consume the set until its owner recreates it.
    /// </summary>
    private void MarkDescriptorWritesNativeStateUnknownNoLock(
        uint writeCount,
        WriteDescriptorSet* writes,
        string reason)
    {
        if (writeCount == 0 || writes is null)
            return;

        VulkanResourceLifetimeTracker tracker = _lifetime.Tracker;
        HashSet<ulong> touchedSets = tracker.ChangedDescriptorSetsScratch.Value!;
        touchedSets.Clear();
        try
        {
            for (int writeIndex = 0; writeIndex < writeCount; writeIndex++)
            {
                WriteDescriptorSet write = writes[writeIndex];
                ulong setHandle = write.DstSet.Handle;
                if (setHandle == 0 ||
                    !tracker.DescriptorSetLifetimes.TryGetValue(
                        setHandle,
                        out VulkanDescriptorSetLifetimeRecord? state))
                {
                    continue;
                }

                if (touchedSets.Add(setHandle))
                {
                    state.NativePublicationState =
                        EVulkanDescriptorNativePublicationState.Unknown;
                    state.Generation++;
                    state.ImagePayloadGeneration++;
                    // Absence is safer than leaving a previously Known snapshot
                    // visible to lock-free recording/submission readers.
                    tracker.PublishedDescriptorSets.TryRemove(setHandle, out _);
                }

                for (uint descriptorIndex = 0;
                     descriptorIndex < write.DescriptorCount;
                     descriptorIndex++)
                {
                    VulkanDescriptorReferencePair references =
                        ResolveReferences(write, descriptorIndex);
                    RetainUncertainDescriptorReferenceNoLock(
                        tracker,
                        state,
                        setHandle,
                        references.First);
                    RetainUncertainDescriptorReferenceNoLock(
                        tracker,
                        state,
                        setHandle,
                        references.Second);
                }
            }

            Debug.VulkanWarning(
                "[Vulkan.Descriptor] Quarantined {0} descriptor set(s) " +
                "with unknown native publication state: {1}",
                touchedSets.Count,
                reason);
        }
        finally
        {
            touchedSets.Clear();
        }
    }

    private static void RetainUncertainDescriptorReferenceNoLock(
        VulkanResourceLifetimeTracker tracker,
        VulkanDescriptorSetLifetimeRecord state,
        ulong descriptorSetHandle,
        VulkanResourceLifetimeKey key)
    {
        if (!key.IsValid)
            return;

        if (!tracker.DescriptorSetsByReferencedResource.TryGetValue(
                key,
                out HashSet<ulong>? descriptorSets))
        {
            tracker.DescriptorSetsByReferencedResource[key] =
                descriptorSets = [];
        }
        descriptorSets.Add(descriptorSetHandle);
        state.IndexedReferences.Add(key);

        if (tracker.ResourceLifetimes.TryGetValue(
                key,
                out VulkanResourceLifetimeRecord? resource) &&
            (resource.State & EVulkanResourceLifetimeState.Destroyed) == 0 &&
            (!state.PinnedReferences.TryGetValue(
                 key,
                 out ulong pinnedGeneration) ||
             pinnedGeneration != resource.Generation))
        {
            resource.Pins.AddDescriptorReference();
            state.PinnedReferences[key] = resource.Generation;
        }

        if (key.Type == ObjectType.ImageView &&
            tracker.ImageViewBackingImages.TryGetValue(
                key.Handle,
                out ulong imageHandle) &&
            imageHandle != 0)
        {
            RetainUncertainDescriptorReferenceNoLock(
                tracker,
                state,
                descriptorSetHandle,
                new VulkanResourceLifetimeKey(
                    ObjectType.Image,
                    imageHandle));
        }
        else if (key.Type == ObjectType.BufferView &&
                 tracker.BufferViewBackingBuffers.TryGetValue(
                     key.Handle,
                     out ulong bufferHandle) &&
                 bufferHandle != 0)
        {
            RetainUncertainDescriptorReferenceNoLock(
                tracker,
                state,
                descriptorSetHandle,
                new VulkanResourceLifetimeKey(
                    ObjectType.Buffer,
                    bufferHandle));
        }
    }

    private bool TryPrevalidateWritesNoLock(
        uint writeCount,
        WriteDescriptorSet* writes,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (writeCount == 0 || writes is null)
            return true;

        VulkanResourceLifetimeTracker tracker = _lifetime.Tracker;
        for (int writeIndex = 0; writeIndex < writeCount; writeIndex++)
        {
            WriteDescriptorSet write = writes[writeIndex];
            if (write.DstSet.Handle == 0)
                continue;

            VulkanResourceLifetimeKey setKey = new(ObjectType.DescriptorSet, write.DstSet.Handle);
            if (!tracker.ResourceLifetimes.TryGetValue(
                    setKey,
                    out VulkanResourceLifetimeRecord? setResource) ||
                !tracker.DescriptorSetLifetimes.TryGetValue(
                    write.DstSet.Handle,
                    out VulkanDescriptorSetLifetimeRecord? setState))
            {
                failureReason =
                    $"Descriptor set {setKey} was not registered before update.";
                return false;
            }
            if ((setResource.State & (EVulkanResourceLifetimeState.PendingRetirement |
                                      EVulkanResourceLifetimeState.Destroyed)) != 0)
            {
                failureReason = $"Cannot update retired Vulkan descriptor set {setKey}.";
                return false;
            }

            bool setUseCompleted = _resources.UpdateResourceCompletionStateNoLock(setResource);
            if (setState.NativePublicationState !=
                EVulkanDescriptorNativePublicationState.Known)
            {
                failureReason =
                    $"Descriptor set {setKey} has unknown native publication state and must be recreated.";
                return false;
            }

            bool usesUpdateAfterBind =
                setState.UsesUpdateAfterBind &&
                CanUseUpdateAfterBind(write.DescriptorType);
            if (!setUseCompleted && !usesUpdateAfterBind)
            {
                failureReason =
                    $"Cannot update in-flight Vulkan descriptor set {setKey}; binding={write.DstBinding} type={write.DescriptorType} was not registered for update-after-bind.";
                return false;
            }

            for (uint descriptorIndex = 0; descriptorIndex < write.DescriptorCount; descriptorIndex++)
            {
                VulkanDescriptorReferencePair references = ResolveReferences(write, descriptorIndex);
                if (!TryPrevalidateReferenceNoLock(tracker, setKey, references.First, out failureReason) ||
                    !TryPrevalidateReferenceNoLock(tracker, setKey, references.Second, out failureReason))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool WritesMatchPublishedPayloadNoLock(
        VulkanResourceLifetimeTracker tracker,
        uint writeCount,
        WriteDescriptorSet* writes)
    {
        if (writeCount == 0)
            return true;
        if (writes is null)
            return false;

        for (int writeIndex = 0; writeIndex < writeCount; writeIndex++)
        {
            WriteDescriptorSet write = writes[writeIndex];
            if (write.DstSet.Handle == 0 || write.DescriptorCount == 0 || write.PNext is not null)
                return false;
            if (!tracker.DescriptorSetLifetimes.TryGetValue(
                    write.DstSet.Handle,
                    out VulkanDescriptorSetLifetimeRecord? setState) ||
                setState.NativePublicationState !=
                    EVulkanDescriptorNativePublicationState.Known)
            {
                return false;
            }

            for (uint descriptorIndex = 0; descriptorIndex < write.DescriptorCount; descriptorIndex++)
            {
                (uint Binding, uint Element) bindingKey =
                    (write.DstBinding, write.DstArrayElement + descriptorIndex);
                if (!TryCaptureDescriptorPayloadNoLock(
                        tracker,
                        write,
                        descriptorIndex,
                        out VulkanDescriptorPayload payload) ||
                    !setState.Payloads.TryGetValue(bindingKey, out VulkanDescriptorPayload published) ||
                    published != payload)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryCaptureDescriptorPayloadNoLock(
        VulkanResourceLifetimeTracker tracker,
        in WriteDescriptorSet write,
        uint descriptorIndex,
        out VulkanDescriptorPayload payload)
    {
        payload = default;
        switch (write.DescriptorType)
        {
            case DescriptorType.Sampler when write.PImageInfo is not null:
            {
                Sampler sampler = write.PImageInfo[descriptorIndex].Sampler;
                payload = new VulkanDescriptorPayload(
                    write.DescriptorType,
                    sampler.Handle,
                    ResolvePayloadGenerationNoLock(tracker, ObjectType.Sampler, sampler.Handle),
                    0,
                    0,
                    0,
                    0,
                    default);
                return true;
            }
            case DescriptorType.CombinedImageSampler when write.PImageInfo is not null:
            {
                DescriptorImageInfo info = write.PImageInfo[descriptorIndex];
                payload = new VulkanDescriptorPayload(
                    write.DescriptorType,
                    info.ImageView.Handle,
                    ResolvePayloadGenerationNoLock(tracker, ObjectType.ImageView, info.ImageView.Handle),
                    info.Sampler.Handle,
                    ResolvePayloadGenerationNoLock(tracker, ObjectType.Sampler, info.Sampler.Handle),
                    0,
                    0,
                    info.ImageLayout);
                return true;
            }
            case DescriptorType.SampledImage or
                 DescriptorType.StorageImage or
                 DescriptorType.InputAttachment when write.PImageInfo is not null:
            {
                DescriptorImageInfo info = write.PImageInfo[descriptorIndex];
                payload = new VulkanDescriptorPayload(
                    write.DescriptorType,
                    info.ImageView.Handle,
                    ResolvePayloadGenerationNoLock(tracker, ObjectType.ImageView, info.ImageView.Handle),
                    0,
                    0,
                    0,
                    0,
                    info.ImageLayout);
                return true;
            }
            case DescriptorType.UniformBuffer or
                 DescriptorType.StorageBuffer or
                 DescriptorType.UniformBufferDynamic or
                 DescriptorType.StorageBufferDynamic when write.PBufferInfo is not null:
            {
                DescriptorBufferInfo info = write.PBufferInfo[descriptorIndex];
                payload = new VulkanDescriptorPayload(
                    write.DescriptorType,
                    info.Buffer.Handle,
                    ResolvePayloadGenerationNoLock(tracker, ObjectType.Buffer, info.Buffer.Handle),
                    0,
                    0,
                    info.Offset,
                    info.Range,
                    default);
                return true;
            }
            case DescriptorType.UniformTexelBuffer or
                 DescriptorType.StorageTexelBuffer when write.PTexelBufferView is not null:
            {
                BufferView view = write.PTexelBufferView[descriptorIndex];
                payload = new VulkanDescriptorPayload(
                    write.DescriptorType,
                    view.Handle,
                    ResolvePayloadGenerationNoLock(tracker, ObjectType.BufferView, view.Handle),
                    0,
                    0,
                    0,
                    0,
                    default);
                return true;
            }
            default:
                return false;
        }
    }

    private static ulong ResolvePayloadGenerationNoLock(
        VulkanResourceLifetimeTracker tracker,
        ObjectType objectType,
        ulong handle)
    {
        if (handle == 0)
            return 0;

        VulkanResourceLifetimeKey key = new(objectType, handle);
        return tracker.GetOrRegisterResourceNoLock(key, "DescriptorSet.Payload").Generation;
    }

    private static bool TryPrevalidateReferenceNoLock(
        VulkanResourceLifetimeTracker tracker,
        VulkanResourceLifetimeKey setKey,
        VulkanResourceLifetimeKey referenceKey,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (!referenceKey.IsValid)
            return true;

        VulkanResourceLifetimeRecord reference = tracker.GetOrRegisterResourceNoLock(
            referenceKey,
            "DescriptorSet.Reference.Prevalidate");
        if ((reference.State & (EVulkanResourceLifetimeState.PendingRetirement |
                                EVulkanResourceLifetimeState.Destroyed)) == 0)
        {
            return true;
        }

        failureReason =
            $"Cannot update descriptor set {setKey} with retired Vulkan resource {referenceKey} generation {reference.Generation}.";
        return false;
    }

    private void ValidateAndPropagateReferenceNoLock(
        VulkanResourceLifetimeKey setKey,
        VulkanResourceLifetimeRecord setResource,
        VulkanResourceLifetimeKey referenceKey,
        bool setUseCompleted)
    {
        if (!referenceKey.IsValid)
            return;

        VulkanResourceLifetimeRecord reference = _lifetime.Tracker.GetOrRegisterResourceNoLock(
            referenceKey,
            "DescriptorSet.Reference");
        if ((reference.State & (EVulkanResourceLifetimeState.PendingRetirement |
                                EVulkanResourceLifetimeState.Destroyed)) != 0)
        {
            throw new InvalidOperationException(
                $"Cannot update descriptor set {setKey} with retired Vulkan resource {referenceKey} generation {reference.Generation}.");
        }

        if (setUseCompleted)
            return;

        reference.Pins.MergeSubmitted(in setResource.Pins);
        reference.LastSubmissionSerial = Math.Max(reference.LastSubmissionSerial, setResource.LastSubmissionSerial);
        reference.LastFrameOpContextId = setResource.LastFrameOpContextId;
        reference.LastFrameOpKind = setResource.LastFrameOpKind;
        reference.State &= ~EVulkanResourceLifetimeState.Completed;
        reference.State |= EVulkanResourceLifetimeState.Submitted;
    }

    private bool TryCaptureUpdateInvalidationsNoLock(
        uint writeCount,
        WriteDescriptorSet* writes,
        out ulong[]? dependentCommandBuffers,
        out int dependentCommandBufferCount,
        out VulkanDescriptorUpdateInvalidation firstInvalidation)
    {
        dependentCommandBuffers = null;
        dependentCommandBufferCount = 0;
        firstInvalidation = default;
        if (writeCount == 0 || writes is null)
            return false;

        VulkanResourceLifetimeTracker tracker = _lifetime.Tracker;
        bool invalidatesRecordedCommandBuffers = false;
        int dependentCapacity = 0;
        for (int writeIndex = 0; writeIndex < writeCount; writeIndex++)
        {
            WriteDescriptorSet write = writes[writeIndex];
            if (!WriteInvalidatesRecordedCommandsNoLock(tracker, write))
                continue;

            invalidatesRecordedCommandBuffers = true;
            VulkanResourceLifetimeKey setKey = new(ObjectType.DescriptorSet, write.DstSet.Handle);
            if (!tracker.ResourceCommandBufferDependencies.TryGetValue(setKey, out HashSet<ulong>? dependents))
                continue;
            dependentCapacity = checked(dependentCapacity + dependents.Count);
            if (firstInvalidation.DescriptorSetHandle == 0 && dependents.Count != 0)
            {
                firstInvalidation = new VulkanDescriptorUpdateInvalidation(
                    write.DstSet.Handle,
                    write.DstBinding,
                    write.DstArrayElement,
                    write.DescriptorType,
                    write.DescriptorCount,
                    tracker.DescriptorSetLifetimes.TryGetValue(
                        write.DstSet.Handle,
                        out VulkanDescriptorSetLifetimeRecord? setState)
                            ? setState.Owner
                            : null);
            }
        }

        if (!invalidatesRecordedCommandBuffers || dependentCapacity == 0)
            return invalidatesRecordedCommandBuffers;

        dependentCommandBuffers = ArrayPool<ulong>.Shared.Rent(dependentCapacity);
        for (int writeIndex = 0; writeIndex < writeCount; writeIndex++)
        {
            WriteDescriptorSet write = writes[writeIndex];
            if (!WriteInvalidatesRecordedCommandsNoLock(tracker, write))
                continue;

            VulkanResourceLifetimeKey setKey = new(ObjectType.DescriptorSet, write.DstSet.Handle);
            if (!tracker.ResourceLifetimes.TryGetValue(setKey, out VulkanResourceLifetimeRecord? setResource) ||
                !tracker.ResourceCommandBufferDependencies.TryGetValue(setKey, out HashSet<ulong>? dependents))
            {
                continue;
            }

            foreach (ulong handle in dependents)
            {
                if (!tracker.CommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime) ||
                    !lifetime.Dependencies.TryGetValue(setKey, out ulong recordedGeneration) ||
                    recordedGeneration != setResource.Generation ||
                    dependentCommandBuffers.AsSpan(0, dependentCommandBufferCount).Contains(handle))
                {
                    continue;
                }

                dependentCommandBuffers[dependentCommandBufferCount++] = handle;
            }
        }

        return true;
    }

    private void PublishContentUpdate(
        bool invalidatesRecordedCommandBuffers,
        ulong[]? dependentCommandBuffers,
        int dependentCommandBufferCount,
        in VulkanDescriptorUpdateInvalidation firstInvalidation,
        string updateKind)
    {
        try
        {
            if (!invalidatesRecordedCommandBuffers)
                return;

            _descriptors.RecordDescriptorSetContentUpdate();
            if (dependentCommandBuffers is null || dependentCommandBufferCount == 0)
                return;

            if (_descriptors.RecordDescriptorUpdateInvalidationDiagnostic() <= 128)
            {
                Debug.WriteAuxiliaryLog(
                    "vulkan-descriptor-invalidations.log",
                    $"update={updateKind} set=0x{firstInvalidation.DescriptorSetHandle:X} owner={firstInvalidation.Owner ?? "<unknown>"} binding={firstInvalidation.Binding} array={firstInvalidation.ArrayElement} type={firstInvalidation.DescriptorType} count={firstInvalidation.DescriptorCount} dependentCommandBuffers={dependentCommandBufferCount}");
            }

            VulkanExactInvalidationResult result = _resources.SynchronousCommands.CommandRuntime.InvalidateCachedCommandBuffers(
                dependentCommandBuffers.AsSpan(0, dependentCommandBufferCount),
                $"{updateKind} changed a descriptor payload required by a recorded command buffer");
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanExactResourceInvalidation(
                result.ExactVariantsDirtied,
                result.ExactCommandChainsDirtied,
                result.UnrelatedVariantsPreserved,
                result.GlobalFallbackInvalidations);
        }
        catch (Exception exception)
        {
            // Native descriptor contents and lifetime tracking are already
            // committed. Diagnostics/cache invalidation failure cannot revoke
            // that ownership or let upload cleanup release referenced handles.
            try
            {
                Debug.VulkanWarning(
                    "[Vulkan] Post-commit descriptor invalidation failed after {0}: {1}",
                    updateKind,
                    exception.Message);
            }
            catch
            {
                // The native commit boundary is intentionally no-throw.
            }
        }
        finally
        {
            if (dependentCommandBuffers is not null)
            {
                try
                {
                    ArrayPool<ulong>.Shared.Return(dependentCommandBuffers);
                }
                catch
                {
                    // Pool diagnostics cannot invalidate a native commit.
                }
            }
        }
    }

    private bool CanUseUpdateAfterBind(DescriptorType type)
    {
        VulkanDeviceContext device = RequireDeviceContext();
        return type switch
        {
            DescriptorType.SampledImage or DescriptorType.CombinedImageSampler or DescriptorType.Sampler =>
                device.MutableCapabilities._supportsDescriptorBindingSampledImageUpdateAfterBind,
            DescriptorType.UniformBuffer =>
                device.MutableCapabilities._supportsDescriptorBindingUniformBufferUpdateAfterBind,
            DescriptorType.StorageBuffer =>
                device.MutableCapabilities._supportsDescriptorBindingStorageBufferUpdateAfterBind,
            DescriptorType.StorageImage => device.MutableCapabilities._supportsDescriptorBindingStorageImageUpdateAfterBind,
            _ => false,
        };
    }

    private bool WriteInvalidatesRecordedCommandsNoLock(
        VulkanResourceLifetimeTracker tracker,
        in WriteDescriptorSet write)
    {
        if (write.DstSet.Handle == 0 || write.DescriptorCount == 0)
            return false;
        if (IsLifetimeTrackedImageDescriptorType(write.DescriptorType))
            return true;
        return !tracker.DescriptorSetLifetimes.TryGetValue(
                   write.DstSet.Handle,
                   out VulkanDescriptorSetLifetimeRecord? state) ||
               !state.UsesUpdateAfterBind ||
               !CanUseUpdateAfterBind(write.DescriptorType);
    }

    private static VulkanDescriptorReferencePair ResolveReferences(
        in WriteDescriptorSet write,
        uint descriptorIndex)
        => write.DescriptorType switch
        {
            DescriptorType.Sampler or DescriptorType.CombinedImageSampler or
            DescriptorType.SampledImage or DescriptorType.StorageImage or DescriptorType.InputAttachment
                when write.PImageInfo is not null
                => new VulkanDescriptorReferencePair(
                    new VulkanResourceLifetimeKey(ObjectType.ImageView, write.PImageInfo[descriptorIndex].ImageView.Handle),
                    new VulkanResourceLifetimeKey(ObjectType.Sampler, write.PImageInfo[descriptorIndex].Sampler.Handle)),
            DescriptorType.UniformBuffer or DescriptorType.StorageBuffer or
            DescriptorType.UniformBufferDynamic or DescriptorType.StorageBufferDynamic
                when write.PBufferInfo is not null
                => new VulkanDescriptorReferencePair(
                    new VulkanResourceLifetimeKey(ObjectType.Buffer, write.PBufferInfo[descriptorIndex].Buffer.Handle),
                    default),
            DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer
                when write.PTexelBufferView is not null
                => new VulkanDescriptorReferencePair(
                    new VulkanResourceLifetimeKey(ObjectType.BufferView, write.PTexelBufferView[descriptorIndex].Handle),
                    default),
            _ => default,
        };

    private static bool IsLifetimeTrackedImageDescriptorType(DescriptorType type)
        => type is DescriptorType.CombinedImageSampler
            or DescriptorType.SampledImage
            or DescriptorType.StorageImage
            or DescriptorType.InputAttachment;

    private void UpdatePoolIndexNoLock(
        VulkanResourceLifetimeTracker tracker,
        ulong descriptorSetHandle,
        ulong previousPoolHandle,
        ulong poolHandle)
    {
        if (previousPoolHandle == poolHandle)
            return;
        if (previousPoolHandle != 0 &&
            tracker.DescriptorSetsByPool.TryGetValue(previousPoolHandle, out HashSet<ulong>? previousSets))
        {
            previousSets.Remove(descriptorSetHandle);
            if (previousSets.Count == 0)
                tracker.DescriptorSetsByPool.Remove(previousPoolHandle);
        }

        if (poolHandle == 0)
            return;
        if (!tracker.DescriptorSetsByPool.TryGetValue(poolHandle, out HashSet<ulong>? sets))
            tracker.DescriptorSetsByPool.Add(poolHandle, sets = []);
        sets.Add(descriptorSetHandle);
    }

    private void RemoveRetiredDescriptorSetsForPoolNoLock(ulong descriptorPoolHandle)
    {
        for (int frameSlot = 0; frameSlot < _lifetime.Retirement.DescriptorSets.Length; frameSlot++)
        {
            List<RetiredDescriptorSet> sets = _lifetime.Retirement.DescriptorSets[frameSlot];
            for (int index = sets.Count - 1; index >= 0; index--)
            {
                RetiredDescriptorSet entry = sets[index];
                if (entry.DescriptorPool.Handle != descriptorPoolHandle)
                    continue;
                sets.RemoveAt(index);
                _lifetime.Retirement.DescriptorSetHandles[frameSlot].Remove(entry.DescriptorSet.Handle);
                _lifetime.Retirement.AllDescriptorSetHandles.Remove(entry.DescriptorSet.Handle);
            }
        }
    }

    private VulkanDeviceContext RequireOperationalDevice()
        => _deviceContext is { IsOperational: true } device
            ? device
            : throw new InvalidOperationException(
                $"Cannot update Vulkan descriptors while device state is {_deviceContext?.State}.");

    private VulkanDeviceContext RequireDeviceContext()
        => _deviceContext ?? throw new InvalidOperationException(
            "The descriptor lifetime authority has not been configured with a device context.");

    private VulkanBackendObjectContext RequireBackendContext()
        => _backendContext ?? throw new InvalidOperationException(
            "The descriptor lifetime authority has not been published to a backend object context.");

    private static void MarkHeapDirty(
        VulkanDescriptorHeapStorage storage,
        ulong offset,
        ulong size,
        ref ulong dirtyStart,
        ref ulong dirtyEnd)
    {
        if (!storage.RequiresCopy)
            return;
        dirtyStart = Math.Min(dirtyStart, offset);
        dirtyEnd = Math.Max(dirtyEnd, checked(offset + size));
    }

    private bool TryCreateAddressRange(
        VulkanBackendObjectContext context,
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
        ulong address = _resources.Buffers.GetDeviceAddress(context, bufferInfo.Buffer);
        if (address == 0)
        {
            reason = $"buffer 0x{bufferInfo.Buffer.Handle:X} has no device address; descriptor heap buffer descriptors require shader-device-address usage.";
            return false;
        }
        range = new DeviceAddressRangeEXTNative
        {
            Address = checked(address + bufferInfo.Offset),
            Size = bufferInfo.Range,
        };
        return true;
    }

    private bool TryCreateTexelBufferInfo(
        VulkanBackendObjectContext context,
        BufferView bufferView,
        out TexelBufferDescriptorInfoEXTNative info,
        out string reason)
    {
        info = default;
        reason = string.Empty;
        if (!_descriptors.TryGetBufferViewCreateInfo(bufferView, out BufferViewCreateInfo createInfo))
        {
            reason = $"buffer view 0x{bufferView.Handle:X} has no descriptor heap create-info metadata.";
            return false;
        }
        ulong address = _resources.Buffers.GetDeviceAddress(context, createInfo.Buffer);
        if (address == 0)
        {
            reason = $"buffer view 0x{bufferView.Handle:X} references buffer 0x{createInfo.Buffer.Handle:X} with no device address.";
            return false;
        }
        info = new TexelBufferDescriptorInfoEXTNative
        {
            SType = VulkanDescriptorHeapExt.TexelBufferDescriptorInfoSType,
            PNext = null,
            Format = createInfo.Format,
            AddressRange = new DeviceAddressRangeEXTNative
            {
                Address = checked(address + createInfo.Offset),
                Size = createInfo.Range,
            },
        };
        return true;
    }

    internal ulong ResolveHeapDescriptorStride(DescriptorType type)
    {
        PhysicalDeviceDescriptorHeapPropertiesEXTNative properties = _descriptors.Heap.Properties;
        ulong fallbackSize = type == DescriptorType.Sampler
            ? properties.SamplerDescriptorSize
            : IsImageResourceDescriptor(type)
                ? properties.ImageDescriptorSize
                : properties.BufferDescriptorSize;
        ulong size = _descriptors.Heap.NativeFunctions?.TryGetDescriptorSize(
            RequireDeviceContext().PhysicalDevice,
            type,
            out ulong exactSize) == true && exactSize != 0
                ? exactSize
                : Math.Max(1ul, fallbackSize);
        ulong alignment = type == DescriptorType.Sampler
            ? properties.SamplerDescriptorAlignment
            : IsImageResourceDescriptor(type)
                ? properties.ImageDescriptorAlignment
                : properties.BufferDescriptorAlignment;
        return AlignHeapUp(Math.Max(size, 1ul), Math.Max(alignment, 1ul));
    }

    private static bool IsImageResourceDescriptor(DescriptorType type)
        => type is DescriptorType.CombinedImageSampler
            or DescriptorType.SampledImage
            or DescriptorType.StorageImage
            or DescriptorType.InputAttachment;

    private static bool DescriptorHeapBindingHasResource(DescriptorType type)
        => type is DescriptorType.CombinedImageSampler
            or DescriptorType.SampledImage
            or DescriptorType.StorageImage
            or DescriptorType.InputAttachment
            or DescriptorType.UniformBuffer
            or DescriptorType.StorageBuffer
            or DescriptorType.UniformTexelBuffer
            or DescriptorType.StorageTexelBuffer;

    private static bool DescriptorHeapBindingHasSampler(DescriptorType type)
        => type is DescriptorType.CombinedImageSampler or DescriptorType.Sampler;

    private static DescriptorType ResolveHeapResourceDescriptorType(DescriptorType type)
        => type switch
        {
            DescriptorType.CombinedImageSampler => DescriptorType.SampledImage,
            DescriptorType.UniformBufferDynamic => DescriptorType.UniformBuffer,
            DescriptorType.StorageBufferDynamic => DescriptorType.StorageBuffer,
            _ => type,
        };

    private static ulong AlignHeapUp(ulong value, ulong alignment)
        => alignment <= 1ul
            ? value
            : checked((value + alignment - 1ul) / alignment * alignment);

    private static ulong ComputePoolSizeFingerprint(DescriptorPoolSize[] poolSizes)
    {
        ulong hash = 1469598103934665603UL;
        for (int index = 0; index < poolSizes.Length; index++)
        {
            hash ^= unchecked((uint)poolSizes[index].Type);
            hash *= 1099511628211UL;
            hash ^= poolSizes[index].DescriptorCount;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static bool HasTemplateSource(in WriteDescriptorSet write)
        => write.DescriptorType switch
        {
            DescriptorType.UniformBuffer or DescriptorType.StorageBuffer or
            DescriptorType.UniformBufferDynamic or DescriptorType.StorageBufferDynamic
                => write.PBufferInfo is not null,
            DescriptorType.CombinedImageSampler or DescriptorType.Sampler or
            DescriptorType.SampledImage or DescriptorType.StorageImage or DescriptorType.InputAttachment
                => write.PImageInfo is not null,
            DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer
                => write.PTexelBufferView is not null,
            _ => false,
        };

    private static bool ContainsImageWrites(ReadOnlySpan<WriteDescriptorSet> writes)
    {
        for (int index = 0; index < writes.Length; index++)
            if (writes[index].DescriptorType is DescriptorType.CombinedImageSampler or
                DescriptorType.Sampler or DescriptorType.SampledImage or
                DescriptorType.StorageImage or DescriptorType.InputAttachment)
            {
                return true;
            }
        return false;
    }

    private static nuint GetTemplateElementSize(DescriptorType type)
        => type switch
        {
            DescriptorType.UniformBuffer or DescriptorType.StorageBuffer or
            DescriptorType.UniformBufferDynamic or DescriptorType.StorageBufferDynamic
                => (nuint)sizeof(DescriptorBufferInfo),
            DescriptorType.CombinedImageSampler or DescriptorType.Sampler or
            DescriptorType.SampledImage or DescriptorType.StorageImage or DescriptorType.InputAttachment
                => (nuint)sizeof(DescriptorImageInfo),
            DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer
                => (nuint)sizeof(BufferView),
            _ => 0,
        };

    private static void CopyTemplateData(in WriteDescriptorSet write, void* destination)
    {
        nuint bytes = GetTemplateElementSize(write.DescriptorType) * write.DescriptorCount;
        void* source = write.DescriptorType switch
        {
            DescriptorType.UniformBuffer or DescriptorType.StorageBuffer or
            DescriptorType.UniformBufferDynamic or DescriptorType.StorageBufferDynamic
                => write.PBufferInfo,
            DescriptorType.CombinedImageSampler or DescriptorType.Sampler or
            DescriptorType.SampledImage or DescriptorType.StorageImage or DescriptorType.InputAttachment
                => write.PImageInfo,
            DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer
                => write.PTexelBufferView,
            _ => null,
        };
        if (source is not null && bytes != 0)
            System.Buffer.MemoryCopy(source, destination, bytes, bytes);
    }

    private static nuint AlignUp(nuint value, nuint alignment)
    {
        if (alignment <= 1)
            return value;
        nuint mask = alignment - 1;
        return (value + mask) & ~mask;
    }

    private static ulong ComputeTemplateHash(ReadOnlySpan<DescriptorUpdateTemplateSignature> signature)
    {
        ulong hash = 1469598103934665603UL;
        static void Mix(ref ulong value, ulong part)
        {
            value ^= part;
            value *= 1099511628211UL;
        }

        for (int index = 0; index < signature.Length; index++)
        {
            DescriptorUpdateTemplateSignature part = signature[index];
            Mix(ref hash, part.DescriptorSetLayout);
            Mix(ref hash, part.PipelineLayout);
            Mix(ref hash, unchecked((ulong)part.BindPoint));
            Mix(ref hash, part.SetIndex);
            Mix(ref hash, part.DstBinding);
            Mix(ref hash, part.DstArrayElement);
            Mix(ref hash, part.DescriptorCount);
            Mix(ref hash, unchecked((ulong)part.DescriptorType));
            Mix(ref hash, unchecked((ulong)part.Offset));
            Mix(ref hash, unchecked((ulong)part.Stride));
        }
        return hash;
    }

    private static bool TemplateSignaturesEqual(
        DescriptorUpdateTemplateSignature[] left,
        DescriptorUpdateTemplateSignature[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (int index = 0; index < left.Length; index++)
            if (left[index] != right[index])
                return false;
        return true;
    }
}
