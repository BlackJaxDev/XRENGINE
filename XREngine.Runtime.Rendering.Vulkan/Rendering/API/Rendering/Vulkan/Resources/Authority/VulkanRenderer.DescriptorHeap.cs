using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanDescriptorManager
{
    private const uint DescriptorHeapDefaultSamplerCapacity = 4096u;
    private const uint DescriptorHeapDefaultResourceCapacity = 16384u;

    internal ref VulkanDescriptorHeapNativeFunctions? _descriptorHeapApi => ref ResourceRuntime.Descriptors.Heap.NativeFunctions;
    internal ref VulkanDescriptorHeapStorage _descriptorHeapSamplerStorage => ref ResourceRuntime.Descriptors.Heap.SamplerStorage;
    internal ref VulkanDescriptorHeapStorage _descriptorHeapResourceStorage => ref ResourceRuntime.Descriptors.Heap.ResourceStorage;
    internal ref EVulkanDescriptorBackend _activeDescriptorBackend => ref ResourceRuntime.Descriptors.Heap.ActiveBackend;
    internal ref string _descriptorBackendFallbackReason => ref ResourceRuntime.Descriptors.Heap.FallbackReason;
    internal ref bool _descriptorHeapFeatureSupported => ref ResourceRuntime.Descriptors.Heap.FeatureSupported;
    internal ref bool _descriptorHeapCaptureReplaySupported => ref ResourceRuntime.Descriptors.Heap.CaptureReplaySupported;
    internal ref bool _descriptorHeapShaderUntypedPointersAvailable => ref ResourceRuntime.Descriptors.Heap.ShaderUntypedPointersAvailable;
    internal ref bool _descriptorHeapNativeApiAvailable => ref ResourceRuntime.Descriptors.Heap.NativeApiAvailable;
    internal ref bool _descriptorHeapStorageReady => ref ResourceRuntime.Descriptors.Heap.StorageReady;
    internal ref PhysicalDeviceDescriptorHeapPropertiesEXTNative _descriptorHeapProperties => ref ResourceRuntime.Descriptors.Heap.Properties;
    internal ref ulong _descriptorHeapSamplerHighWaterBytes => ref ResourceRuntime.Descriptors.Heap.SamplerHighWaterBytes;
    internal ref ulong _descriptorHeapResourceHighWaterBytes => ref ResourceRuntime.Descriptors.Heap.ResourceHighWaterBytes;
    internal ref ulong _descriptorHeapSamplerWriteCount => ref ResourceRuntime.Descriptors.Heap.SamplerWriteCount;
    internal ref ulong _descriptorHeapResourceWriteCount => ref ResourceRuntime.Descriptors.Heap.ResourceWriteCount;
    internal ref ulong _descriptorHeapSamplerBindCount => ref ResourceRuntime.Descriptors.Heap.SamplerBindCount;
    internal ref ulong _descriptorHeapResourceBindCount => ref ResourceRuntime.Descriptors.Heap.ResourceBindCount;
    internal ref ulong _descriptorHeapCopyCount => ref ResourceRuntime.Descriptors.Heap.CopyCount;
    internal ref ulong _descriptorHeapCopyBytes => ref ResourceRuntime.Descriptors.Heap.CopyBytes;
    internal ref ulong _descriptorHeapAllocationFailureCount => ref ResourceRuntime.Descriptors.Heap.AllocationFailureCount;
    internal ref ulong _descriptorHeapSamplerDirtyStart => ref ResourceRuntime.Descriptors.Heap.SamplerDirtyStart;
    internal ref ulong _descriptorHeapSamplerDirtyEnd => ref ResourceRuntime.Descriptors.Heap.SamplerDirtyEnd;
    internal ref ulong _descriptorHeapResourceDirtyStart => ref ResourceRuntime.Descriptors.Heap.ResourceDirtyStart;
    internal ref ulong _descriptorHeapResourceDirtyEnd => ref ResourceRuntime.Descriptors.Heap.ResourceDirtyEnd;
    internal ref ulong _descriptorHeapFrameNumber => ref ResourceRuntime.Descriptors.Heap.FrameNumber;
    internal ref ulong _descriptorHeapFrameWrites => ref ResourceRuntime.Descriptors.Heap.FrameWrites;
    internal ref ulong _descriptorHeapFrameCopies => ref ResourceRuntime.Descriptors.Heap.FrameCopies;
    internal ref ulong _descriptorHeapLastFrameWrites => ref ResourceRuntime.Descriptors.Heap.LastFrameWrites;
    internal ref ulong _descriptorHeapLastFrameCopies => ref ResourceRuntime.Descriptors.Heap.LastFrameCopies;

    public EVulkanDescriptorBackend ActiveDescriptorBackend => _activeDescriptorBackend;
    public string DescriptorBackendFallbackReason => _descriptorBackendFallbackReason;
    public bool DescriptorHeapStorageReady => _descriptorHeapStorageReady;
    public ulong DescriptorHeapSamplerBytesUsed => _descriptorHeapSamplerHighWaterBytes;
    public ulong DescriptorHeapResourceBytesUsed => _descriptorHeapResourceHighWaterBytes;
    public ulong DescriptorHeapSamplerCapacityBytes => _descriptorHeapSamplerStorage.Size;
    public ulong DescriptorHeapResourceCapacityBytes => _descriptorHeapResourceStorage.Size;
    public ulong DescriptorHeapSamplerWrites => _descriptorHeapSamplerWriteCount;
    public ulong DescriptorHeapResourceWrites => _descriptorHeapResourceWriteCount;
    public ulong DescriptorHeapSamplerBinds => _descriptorHeapSamplerBindCount;
    public ulong DescriptorHeapResourceBinds => _descriptorHeapResourceBindCount;
    public ulong DescriptorHeapCopies => _descriptorHeapCopyCount;
    public ulong DescriptorHeapCopyBytes => _descriptorHeapCopyBytes;
    public ulong DescriptorHeapAllocationFailures => _descriptorHeapAllocationFailureCount;
    public ulong DescriptorHeapLastFrameWrites => _descriptorHeapLastFrameWrites;
    public ulong DescriptorHeapLastFrameCopies => _descriptorHeapLastFrameCopies;
    public bool DescriptorHeapUsesStagedGpuCopies =>
        _descriptorHeapSamplerStorage.RequiresCopy || _descriptorHeapResourceStorage.RequiresCopy;

    internal unsafe void QueryDescriptorHeapCapabilities(
        bool descriptorHeapExtensionAvailable,
        bool shaderUntypedPointersAvailable,
        out bool descriptorHeapFeatureSupported,
        out bool descriptorHeapCaptureReplaySupported,
        out PhysicalDeviceDescriptorHeapPropertiesEXTNative descriptorHeapProperties)
    {
        descriptorHeapFeatureSupported = false;
        descriptorHeapCaptureReplaySupported = false;
        descriptorHeapProperties = default;
        _descriptorHeapShaderUntypedPointersAvailable = shaderUntypedPointersAvailable;

        if (!descriptorHeapExtensionAvailable)
            return;

        PhysicalDeviceDescriptorHeapFeaturesEXTNative descriptorHeapFeatures = new()
        {
            SType = VulkanDescriptorHeapExt.PhysicalDeviceDescriptorHeapFeaturesSType,
            PNext = null,
        };

        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &descriptorHeapFeatures,
        };

        Api.GetPhysicalDeviceFeatures2(DeviceContext.PhysicalDevice, &features2);
        descriptorHeapFeatureSupported = descriptorHeapFeatures.DescriptorHeap;
        descriptorHeapCaptureReplaySupported = descriptorHeapFeatures.DescriptorHeapCaptureReplay;

        PhysicalDeviceDescriptorHeapPropertiesEXTNative properties = new()
        {
            SType = VulkanDescriptorHeapExt.PhysicalDeviceDescriptorHeapPropertiesSType,
            PNext = null,
        };

        PhysicalDeviceProperties2 properties2 = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &properties,
        };

        Api.GetPhysicalDeviceProperties2(DeviceContext.PhysicalDevice, &properties2);
        descriptorHeapProperties = properties;
    }

    internal void ResolveDescriptorBackendAfterDeviceCreate(
        EVulkanDescriptorBackend requestedBackend,
        bool descriptorIndexingEnabled,
        bool descriptorHeapExtensionAvailable,
        bool descriptorHeapDependenciesReady,
        bool descriptorHeapFeatureSupported,
        bool descriptorHeapNativeApiAvailable)
    {
        _activeDescriptorBackend = EVulkanDescriptorBackend.DescriptorSets;
        _descriptorBackendFallbackReason = string.Empty;

        bool descriptorHeapPreferred = requestedBackend == EVulkanDescriptorBackend.DescriptorHeap;

        if (descriptorHeapPreferred)
        {
            if (!descriptorHeapExtensionAvailable)
                _descriptorBackendFallbackReason = "VK_EXT_descriptor_heap is not exposed by the selected physical device.";
            else if (!descriptorHeapDependenciesReady)
                _descriptorBackendFallbackReason = "VK_EXT_descriptor_heap dependencies are incomplete.";
            else if (!descriptorHeapFeatureSupported)
                _descriptorBackendFallbackReason = "VK_EXT_descriptor_heap feature bit is false.";
            else if (!descriptorHeapNativeApiAvailable)
                _descriptorBackendFallbackReason = "VK_EXT_descriptor_heap native entry points are unavailable.";
            else if (!TryInitializeDescriptorHeapStorage(out _descriptorBackendFallbackReason))
                _descriptorBackendFallbackReason = $"Descriptor heap storage initialization failed: {_descriptorBackendFallbackReason}";
            else
            {
                _activeDescriptorBackend = EVulkanDescriptorBackend.DescriptorHeap;
                _descriptorBackendFallbackReason = "Descriptor heap is the active descriptor backend.";
                Debug.Vulkan(
                    "[Vulkan.DescriptorHeap.Active] heapStorageReady=True activeDescriptorBackend={0}.",
                    _activeDescriptorBackend);
                return;
            }
        }

        if (descriptorIndexingEnabled && requestedBackend != EVulkanDescriptorBackend.DescriptorSets)
        {
            _activeDescriptorBackend = EVulkanDescriptorBackend.DescriptorIndexing;
            if (string.IsNullOrWhiteSpace(_descriptorBackendFallbackReason))
                _descriptorBackendFallbackReason = "Descriptor indexing is the active backend.";
            return;
        }

        _activeDescriptorBackend = EVulkanDescriptorBackend.DescriptorSets;
        if (string.IsNullOrWhiteSpace(_descriptorBackendFallbackReason))
            _descriptorBackendFallbackReason = descriptorIndexingEnabled
                ? "Descriptor sets were explicitly requested."
                : "Descriptor indexing is unavailable; descriptor sets are the fallback backend.";
    }

    internal bool TryInitializeDescriptorHeapNativeApi(out string reason)
    {
        reason = string.Empty;
        _descriptorHeapNativeApiAvailable = false;
        _descriptorHeapApi = null;

        VulkanDescriptorHeapNativeFunctions api = new();
        if (!api.TryLoad(Api, DeviceContext.Instance, DeviceContext.Device, out reason))
            return false;

        _descriptorHeapApi = api;
        _descriptorHeapNativeApiAvailable = true;
        Debug.Vulkan("[Vulkan.DescriptorHeap.Capability] native entry points loaded.");
        return true;
    }

    private bool TryInitializeDescriptorHeapStorage(out string reason)
    {
        reason = string.Empty;
        if (_descriptorHeapStorageReady)
            return true;

        if (_descriptorHeapApi is null)
        {
            reason = "native descriptor heap API is not loaded.";
            return false;
        }

        if (ResourceRuntime.Allocations.Buffers.MemoryAllocator is null)
        {
            reason = "Vulkan memory allocator is not initialized yet.";
            return false;
        }

        try
        {
            ulong samplerDescriptorSize = ResolveDescriptorHeapDescriptorSize(DescriptorType.Sampler, _descriptorHeapProperties.SamplerDescriptorSize);
            ulong imageDescriptorSize = ResolveDescriptorHeapDescriptorSize(DescriptorType.SampledImage, _descriptorHeapProperties.ImageDescriptorSize);
            ulong bufferDescriptorSize = ResolveDescriptorHeapDescriptorSize(DescriptorType.StorageBuffer, _descriptorHeapProperties.BufferDescriptorSize);
            ulong resourceDescriptorSize = Math.Max(imageDescriptorSize, bufferDescriptorSize);

            ulong samplerReserved = Math.Max(
                _descriptorHeapProperties.MinSamplerHeapReservedRange,
                _descriptorHeapProperties.MinSamplerHeapReservedRangeWithEmbedded);
            ulong resourceReserved = _descriptorHeapProperties.MinResourceHeapReservedRange;

            ulong samplerSize = ResolveDescriptorHeapAllocationSize(
                samplerReserved,
                samplerDescriptorSize,
                DescriptorHeapDefaultSamplerCapacity,
                _descriptorHeapProperties.SamplerHeapAlignment,
                _descriptorHeapProperties.MaxSamplerHeapSize);
            ulong resourceSize = ResolveDescriptorHeapAllocationSize(
                resourceReserved,
                resourceDescriptorSize,
                DescriptorHeapDefaultResourceCapacity,
                _descriptorHeapProperties.ResourceHeapAlignment,
                _descriptorHeapProperties.MaxResourceHeapSize);

            _descriptorHeapSamplerStorage = CreateDescriptorHeapStorage("Sampler", samplerSize);
            _descriptorHeapResourceStorage = CreateDescriptorHeapStorage("Resource", resourceSize);
            _descriptorHeapSamplerHighWaterBytes = samplerReserved;
            _descriptorHeapResourceHighWaterBytes = resourceReserved;
            _descriptorHeapStorageReady =
                _descriptorHeapSamplerStorage.IsReady &&
                _descriptorHeapResourceStorage.IsReady;

            Debug.Vulkan(
                "[Vulkan.DescriptorHeap.Allocation] samplerSize={0} samplerAddress=0x{1:X} samplerReserved={2} samplerDescriptorSize={3} resourceSize={4} resourceAddress=0x{5:X} resourceReserved={6} imageDescriptorSize={7} bufferDescriptorSize={8} maxPushDataSize={9}.",
                _descriptorHeapSamplerStorage.Size,
                _descriptorHeapSamplerStorage.DeviceAddress,
                samplerReserved,
                samplerDescriptorSize,
                _descriptorHeapResourceStorage.Size,
                _descriptorHeapResourceStorage.DeviceAddress,
                resourceReserved,
                imageDescriptorSize,
                bufferDescriptorSize,
                _descriptorHeapProperties.MaxPushDataSize);
            return _descriptorHeapStorageReady;
        }
        catch (Exception ex)
        {
            DestroyDescriptorHeapBackend();
            reason = ex.Message;
            return false;
        }
    }

    private ulong ResolveDescriptorHeapDescriptorSize(DescriptorType descriptorType, ulong fallbackSize)
    {
        if (_descriptorHeapApi?.TryGetDescriptorSize(DeviceContext.PhysicalDevice, descriptorType, out ulong exactSize) == true &&
            exactSize > 0)
        {
            return exactSize;
        }

        return Math.Max(1ul, fallbackSize);
    }

    private VulkanDescriptorHeapStorage CreateDescriptorHeapStorage(string name, ulong size)
    {
        BufferUsageFlags usage =
            VulkanDescriptorHeapExt.DescriptorHeapBufferUsage |
            BufferUsageFlags.ShaderDeviceAddressBit |
            BufferUsageFlags.TransferSrcBit |
            BufferUsageFlags.TransferDstBit;
        Buffer buffer = default;
        DeviceMemory memory = default;
        Buffer stagingBuffer = default;
        DeviceMemory stagingMemory = default;
        void* mapped = null;
        bool requiresCopy = false;

        try
        {
            (buffer, memory) = CreateDedicatedBufferRaw(
                size,
                usage,
                MemoryPropertyFlags.DeviceLocalBit,
                enableDeviceAddress: true);

            if (!TryMapBufferMemory(buffer, memory, 0, size, out mapped))
            {
                requiresCopy = true;
                (stagingBuffer, stagingMemory) = CreateDedicatedBufferRaw(
                    size,
                    BufferUsageFlags.TransferSrcBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
                if (!TryMapBufferMemory(stagingBuffer, stagingMemory, 0, size, out mapped))
                    throw new InvalidOperationException($"Failed to map {name} descriptor heap staging storage.");
            }
        }
        catch
        {
            if (mapped is not null)
                UnmapBufferMemory(requiresCopy ? stagingBuffer : buffer, requiresCopy ? stagingMemory : memory);
            if (stagingBuffer.Handle != 0)
                DestroyBuffer(stagingBuffer, stagingMemory);
            if (buffer.Handle != 0)
                DestroyBuffer(buffer, memory);
            throw;
        }

        ulong address = GetBufferDeviceAddress(buffer);
        if (address == 0)
        {
            UnmapBufferMemory(requiresCopy ? stagingBuffer : buffer, requiresCopy ? stagingMemory : memory);
            if (stagingBuffer.Handle != 0)
                DestroyBuffer(stagingBuffer, stagingMemory);
            DestroyBuffer(buffer, memory);
            throw new InvalidOperationException($"{name} descriptor heap storage has no device address.");
        }

        FrameTelemetry.RegisterDeviceAddressRange(buffer, address, size, $"DescriptorHeap.{name}");
        ResourceRuntime.DescriptorLifetime.RecordTableGeneration();
        Debug.Vulkan(
            "[Vulkan.DescriptorHeap.Residency] heap={0} placement={1} size={2}.",
            name,
            requiresCopy ? "DeviceLocalWithStaging" : "HostVisibleDeviceLocal",
            size);
        return new VulkanDescriptorHeapStorage(
            buffer,
            memory,
            mapped,
            size,
            address,
            stagingBuffer,
            stagingMemory,
            requiresCopy);
    }

    internal void DestroyDescriptorHeapBackend()
    {
        DestroyDescriptorHeapStorage(ref _descriptorHeapSamplerStorage);
        DestroyDescriptorHeapStorage(ref _descriptorHeapResourceStorage);
        _descriptorHeapApi?.ReleaseDelegates();
        _descriptorHeapApi = null;
        _descriptorHeapStorageReady = false;
        _descriptorHeapSamplerHighWaterBytes = 0;
        _descriptorHeapResourceHighWaterBytes = 0;
        _descriptorHeapSamplerWriteCount = 0;
        _descriptorHeapResourceWriteCount = 0;
        _descriptorHeapSamplerBindCount = 0;
        _descriptorHeapResourceBindCount = 0;
        _descriptorHeapCopyCount = 0;
        _descriptorHeapCopyBytes = 0;
        _descriptorHeapAllocationFailureCount = 0;
        _descriptorHeapSamplerDirtyStart = ulong.MaxValue;
        _descriptorHeapSamplerDirtyEnd = 0;
        _descriptorHeapResourceDirtyStart = ulong.MaxValue;
        _descriptorHeapResourceDirtyEnd = 0;
    }

    private void DestroyDescriptorHeapStorage(ref VulkanDescriptorHeapStorage storage)
    {
        if (!storage.IsReady)
        {
            storage = default;
            return;
        }

        Buffer mappedBuffer = storage.RequiresCopy ? storage.StagingBuffer : storage.Buffer;
        DeviceMemory mappedMemory = storage.RequiresCopy ? storage.StagingMemory : storage.Memory;
        UnmapBufferMemory(mappedBuffer, mappedMemory);
        if (storage.StagingBuffer.Handle != 0)
            DestroyBuffer(storage.StagingBuffer, storage.StagingMemory);
        DestroyBuffer(storage.Buffer, storage.Memory);
        storage = default;
    }

    internal bool TryBindDescriptorHeapsTracked(CommandBuffer commandBuffer)
    {
        if (!ShouldBindDescriptorHeapState())
            return false;

        if (_descriptorHeapApi is null ||
            !_descriptorHeapSamplerStorage.IsReady ||
            !_descriptorHeapResourceStorage.IsReady)
        {
            return false;
        }

        FlushDescriptorHeapStagingCopies(commandBuffer);

        CommandRuntime.TrackVulkanCommandBufferResource(
            commandBuffer,
            ObjectType.Buffer,
            _descriptorHeapSamplerStorage.Buffer.Handle,
            "DescriptorHeap.SamplerStorage");
        CommandRuntime.TrackVulkanCommandBufferResource(
            commandBuffer,
            ObjectType.Buffer,
            _descriptorHeapResourceStorage.Buffer.Handle,
            "DescriptorHeap.ResourceStorage");

        ulong signature = unchecked((ulong)HashCode.Combine(
            _descriptorHeapSamplerStorage.DeviceAddress,
            _descriptorHeapSamplerStorage.Size,
            _descriptorHeapResourceStorage.DeviceAddress,
            _descriptorHeapResourceStorage.Size));

        bool shouldBind;
        ulong key = unchecked((ulong)commandBuffer.Handle);
        lock (CommandRuntime.CommandBuffers.BindStateGate)
        {
            CommandRuntime.CommandBuffers.BindStates.TryGetValue(key, out CommandBufferBindState state);
            shouldBind = state.DescriptorHeapSignature != signature;
            if (shouldBind)
            {
                state.DescriptorHeapSignature = signature;
                CommandRuntime.CommandBuffers.BindStates[key] = state;
            }
        }

        if (!shouldBind)
            return true;

        BindHeapInfoEXTNative samplerBindInfo = CreateSamplerHeapBindInfo();
        BindHeapInfoEXTNative resourceBindInfo = CreateResourceHeapBindInfo();
        _descriptorHeapApi.CmdBindSamplerHeap(commandBuffer, &samplerBindInfo);
        _descriptorHeapApi.CmdBindResourceHeap(commandBuffer, &resourceBindInfo);
        CommandRuntime.InvalidateDescriptorBindings(commandBuffer);
        _descriptorHeapSamplerBindCount++;
        _descriptorHeapResourceBindCount++;
        return true;
    }

    private bool ShouldBindDescriptorHeapState()
        => _descriptorHeapStorageReady &&
           _activeDescriptorBackend == EVulkanDescriptorBackend.DescriptorHeap &&
           _descriptorHeapNativeApiAvailable &&
           _descriptorHeapApi is not null &&
           _descriptorHeapSamplerStorage.IsReady &&
           _descriptorHeapResourceStorage.IsReady;

    private BindHeapInfoEXTNative CreateSamplerHeapBindInfo()
        => new()
        {
            SType = VulkanDescriptorHeapExt.BindHeapInfoSType,
            PNext = null,
            HeapRange = new DeviceAddressRangeEXTNative
            {
                Address = _descriptorHeapSamplerStorage.DeviceAddress,
                Size = _descriptorHeapSamplerStorage.Size,
            },
            ReservedRangeOffset = 0,
            ReservedRangeSize = Math.Max(
                _descriptorHeapProperties.MinSamplerHeapReservedRange,
                _descriptorHeapProperties.MinSamplerHeapReservedRangeWithEmbedded),
        };

    private BindHeapInfoEXTNative CreateResourceHeapBindInfo()
        => new()
        {
            SType = VulkanDescriptorHeapExt.BindHeapInfoSType,
            PNext = null,
            HeapRange = new DeviceAddressRangeEXTNative
            {
                Address = _descriptorHeapResourceStorage.DeviceAddress,
                Size = _descriptorHeapResourceStorage.Size,
            },
            ReservedRangeOffset = 0,
            ReservedRangeSize = _descriptorHeapProperties.MinResourceHeapReservedRange,
        };

    internal bool TryAppendDescriptorHeapInheritancePNext(
        ref CommandBufferInheritanceInfo inheritanceInfo,
        CommandBufferInheritanceDescriptorHeapInfoEXTNative* heapInfo,
        BindHeapInfoEXTNative* samplerHeapInfo,
        BindHeapInfoEXTNative* resourceHeapInfo)
    {
        if (!ShouldBindDescriptorHeapState() ||
            heapInfo is null ||
            samplerHeapInfo is null ||
            resourceHeapInfo is null)
        {
            return false;
        }

        *samplerHeapInfo = CreateSamplerHeapBindInfo();
        *resourceHeapInfo = CreateResourceHeapBindInfo();
        *heapInfo = new CommandBufferInheritanceDescriptorHeapInfoEXTNative
        {
            SType = VulkanDescriptorHeapExt.CommandBufferInheritanceDescriptorHeapInfoSType,
            PNext = inheritanceInfo.PNext,
            SamplerHeapBindInfo = samplerHeapInfo,
            ResourceHeapBindInfo = resourceHeapInfo,
        };
        inheritanceInfo.PNext = heapInfo;
        return true;
    }

    private void FlushDescriptorHeapStagingCopies(CommandBuffer commandBuffer)
    {
        FlushDescriptorHeapStagingCopy(
            commandBuffer,
            _descriptorHeapSamplerStorage,
            ref _descriptorHeapSamplerDirtyStart,
            ref _descriptorHeapSamplerDirtyEnd,
            (AccessFlags2)VulkanDescriptorHeapExt.SamplerHeapReadAccess2,
            "Sampler");
        FlushDescriptorHeapStagingCopy(
            commandBuffer,
            _descriptorHeapResourceStorage,
            ref _descriptorHeapResourceDirtyStart,
            ref _descriptorHeapResourceDirtyEnd,
            (AccessFlags2)VulkanDescriptorHeapExt.ResourceHeapReadAccess2,
            "Resource");
    }

    private void FlushDescriptorHeapStagingCopy(
        CommandBuffer commandBuffer,
        VulkanDescriptorHeapStorage storage,
        ref ulong dirtyStart,
        ref ulong dirtyEnd,
        AccessFlags2 heapReadAccess,
        string heapName)
    {
        if (!storage.RequiresCopy || dirtyStart == ulong.MaxValue || dirtyEnd <= dirtyStart)
            return;

        ulong copyStart = AlignDescriptorHeapDown(dirtyStart, sizeof(uint));
        ulong copyEnd = AlignDescriptorHeapUp(dirtyEnd, sizeof(uint));
        ulong copySize = copyEnd - copyStart;
        BufferCopy copy = new()
        {
            SrcOffset = copyStart,
            DstOffset = copyStart,
            Size = copySize,
        };
        CommandRuntime.CmdCopyBufferTracked(commandBuffer, storage.StagingBuffer, storage.Buffer, 1, &copy);

        BufferMemoryBarrier2 barrier = new()
        {
            SType = StructureType.BufferMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.TransferBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = heapReadAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = storage.Buffer,
            Offset = copyStart,
            Size = copySize,
        };
        DependencyInfo dependency = new()
        {
            SType = StructureType.DependencyInfo,
            BufferMemoryBarrierCount = 1,
            PBufferMemoryBarriers = &barrier,
        };
        CommandRuntime.PipelineBarrier2Tracked(commandBuffer, &dependency);

        dirtyStart = ulong.MaxValue;
        dirtyEnd = 0;
        _descriptorHeapCopyCount++;
        _descriptorHeapFrameCopies++;
        _descriptorHeapCopyBytes += copySize;
        if (VulkanCommandRuntime.FrameDiagnosticsTraceEnabled)
        {
            Debug.VulkanEvery(
                $"Vulkan.DescriptorHeap.Copy.{heapName}",
                TimeSpan.FromSeconds(2),
                "[Vulkan.DescriptorHeap.Copy] heap={0} offset={1} bytes={2} frameCopies={3} totalCopies={4}.",
                heapName,
                copyStart,
                copySize,
                _descriptorHeapFrameCopies,
                _descriptorHeapCopyCount);
        }
    }

    internal bool TryPushDescriptorHeapData(CommandBuffer commandBuffer, uint offset, void* data, uint byteCount, out string reason)
    {
        reason = string.Empty;
        if (_descriptorHeapApi is null)
        {
            reason = "descriptor heap native API is not loaded.";
            return false;
        }

        if (data is null || byteCount == 0)
        {
            reason = "descriptor heap push-data payload is empty.";
            return false;
        }

        if (_descriptorHeapProperties.MaxPushDataSize > 0 &&
            offset + byteCount > _descriptorHeapProperties.MaxPushDataSize)
        {
            reason = $"descriptor heap push-data range exceeds maxPushDataSize (offset={offset}, bytes={byteCount}, max={_descriptorHeapProperties.MaxPushDataSize}).";
            return false;
        }

        if (!TryBindDescriptorHeapsTracked(commandBuffer))
        {
            reason = "descriptor heap state could not be rebound before push data.";
            return false;
        }

        PushDataInfoEXTNative pushData = new()
        {
            SType = VulkanDescriptorHeapExt.PushDataInfoSType,
            PNext = null,
            Offset = offset,
            Data = new HostAddressRangeConstEXTNative
            {
                Address = data,
                Size = byteCount,
            },
        };
        _descriptorHeapApi.CmdPushData(commandBuffer, &pushData);
        CommandRuntime.InvalidateDescriptorBindings(commandBuffer);
        return true;
    }

    private static bool TryResolveDescriptorHeapWriteDestination(
        VulkanDescriptorHeapStorage storage,
        ulong offsetBytes,
        ulong sizeBytes,
        out HostAddressRangeEXTNative destination,
        out string reason)
    {
        destination = default;
        reason = string.Empty;

        if (!storage.IsReady)
        {
            reason = "descriptor heap storage is not ready.";
            return false;
        }

        if (sizeBytes == 0 || offsetBytes > storage.Size || sizeBytes > storage.Size - offsetBytes)
        {
            reason = $"descriptor heap write range is out of bounds (offset={offsetBytes}, size={sizeBytes}, capacity={storage.Size}).";
            return false;
        }

        destination = new HostAddressRangeEXTNative
        {
            Address = (byte*)storage.Mapped + offsetBytes,
            Size = checked((nuint)sizeBytes),
        };
        return true;
    }

    private static ulong ResolveDescriptorHeapAllocationSize(
        ulong reservedBytes,
        ulong descriptorSize,
        uint descriptorCapacity,
        ulong alignment,
        ulong maxHeapSize)
    {
        ulong requested = checked(reservedBytes + descriptorSize * Math.Max(1u, descriptorCapacity));
        ulong aligned = AlignDescriptorHeapUp(Math.Max(requested, 1ul), Math.Max(alignment, 1ul));
        if (maxHeapSize > 0 && aligned > maxHeapSize)
            aligned = AlignDescriptorHeapDown(maxHeapSize, Math.Max(alignment, 1ul));
        return Math.Max(aligned, Math.Max(reservedBytes + descriptorSize, 1ul));
    }

    private static ulong AlignDescriptorHeapUp(ulong value, ulong alignment)
        => alignment <= 1ul ? value : checked((value + alignment - 1ul) / alignment * alignment);

    private static ulong AlignDescriptorHeapDown(ulong value, ulong alignment)
        => alignment <= 1ul ? value : value / alignment * alignment;

}
