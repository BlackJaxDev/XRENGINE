using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const uint DescriptorHeapDefaultSamplerCapacity = 4096u;
    private const uint DescriptorHeapDefaultResourceCapacity = 16384u;

    private VulkanDescriptorHeapNativeApi? _descriptorHeapApi;
    private DescriptorHeapStorage _descriptorHeapSamplerStorage;
    private DescriptorHeapStorage _descriptorHeapResourceStorage;
    private EVulkanDescriptorBackend _activeDescriptorBackend = EVulkanDescriptorBackend.DescriptorSets;
    private string _descriptorBackendFallbackReason = "Vulkan logical device is not initialized.";
    private bool _descriptorHeapFeatureSupported;
    private bool _descriptorHeapCaptureReplaySupported;
    private bool _descriptorHeapShaderUntypedPointersAvailable;
    private bool _descriptorHeapNativeApiAvailable;
    private bool _descriptorHeapStorageReady;
    private PhysicalDeviceDescriptorHeapPropertiesEXTNative _descriptorHeapProperties;
    private ulong _descriptorHeapSamplerHighWaterBytes;
    private ulong _descriptorHeapResourceHighWaterBytes;
    private ulong _descriptorHeapSamplerWriteCount;
    private ulong _descriptorHeapResourceWriteCount;
    private ulong _descriptorHeapSamplerBindCount;
    private ulong _descriptorHeapResourceBindCount;

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

    private unsafe void QueryDescriptorHeapCapabilities(
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

        Api!.GetPhysicalDeviceFeatures2(_physicalDevice, &features2);
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

        Api.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
        descriptorHeapProperties = properties;
    }

    private void ResolveDescriptorBackendAfterDeviceCreate(
        EVulkanDescriptorBackend requestedBackend,
        bool descriptorIndexingEnabled,
        bool descriptorHeapExtensionAvailable,
        bool descriptorHeapDependenciesReady,
        bool descriptorHeapFeatureSupported,
        bool descriptorHeapNativeApiAvailable)
    {
        _activeDescriptorBackend = EVulkanDescriptorBackend.DescriptorSets;
        _descriptorBackendFallbackReason = string.Empty;

        bool descriptorHeapPreferred =
            requestedBackend == EVulkanDescriptorBackend.DescriptorHeap ||
            (!VulkanFeatureProfile.TryGetDescriptorBackendEnvOverride(out _) &&
             descriptorHeapExtensionAvailable &&
             descriptorHeapDependenciesReady &&
             descriptorHeapFeatureSupported &&
             descriptorHeapNativeApiAvailable);

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

    private bool TryInitializeDescriptorHeapNativeApi(out string reason)
    {
        reason = string.Empty;
        _descriptorHeapNativeApiAvailable = false;
        _descriptorHeapApi = null;

        VulkanDescriptorHeapNativeApi api = new();
        if (!api.TryLoad(Api!, instance, device, out reason))
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

        if (_memoryAllocator is null)
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
        if (_descriptorHeapApi?.TryGetDescriptorSize(_physicalDevice, descriptorType, out ulong exactSize) == true &&
            exactSize > 0)
        {
            return exactSize;
        }

        return Math.Max(1ul, fallbackSize);
    }

    private DescriptorHeapStorage CreateDescriptorHeapStorage(string name, ulong size)
    {
        BufferUsageFlags usage =
            VulkanDescriptorHeapExt.DescriptorHeapBufferUsage |
            BufferUsageFlags.ShaderDeviceAddressBit |
            BufferUsageFlags.TransferSrcBit |
            BufferUsageFlags.TransferDstBit;
        MemoryPropertyFlags properties = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        (Buffer buffer, DeviceMemory memory) = CreateDedicatedBufferRaw(size, usage, properties, enableDeviceAddress: true);

        if (!TryMapBufferMemory(buffer, memory, 0, size, out void* mapped))
        {
            DestroyBuffer(buffer, memory);
            throw new InvalidOperationException($"Failed to map {name} descriptor heap storage.");
        }

        ulong address = GetBufferDeviceAddress(buffer);
        if (address == 0)
        {
            UnmapBufferMemory(buffer, memory);
            DestroyBuffer(buffer, memory);
            throw new InvalidOperationException($"{name} descriptor heap storage has no device address.");
        }

        return new DescriptorHeapStorage(buffer, memory, mapped, size, address);
    }

    private void DestroyDescriptorHeapBackend()
    {
        DestroyDescriptorHeapStorage(ref _descriptorHeapSamplerStorage);
        DestroyDescriptorHeapStorage(ref _descriptorHeapResourceStorage);
        _descriptorHeapStorageReady = false;
        _descriptorHeapSamplerHighWaterBytes = 0;
        _descriptorHeapResourceHighWaterBytes = 0;
        _descriptorHeapSamplerWriteCount = 0;
        _descriptorHeapResourceWriteCount = 0;
        _descriptorHeapSamplerBindCount = 0;
        _descriptorHeapResourceBindCount = 0;
    }

    private void DestroyDescriptorHeapStorage(ref DescriptorHeapStorage storage)
    {
        if (!storage.IsReady)
        {
            storage = default;
            return;
        }

        UnmapBufferMemory(storage.Buffer, storage.Memory);
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

        ulong signature = unchecked((ulong)HashCode.Combine(
            _descriptorHeapSamplerStorage.DeviceAddress,
            _descriptorHeapSamplerStorage.Size,
            _descriptorHeapResourceStorage.DeviceAddress,
            _descriptorHeapResourceStorage.Size));

        bool shouldBind;
        ulong key = unchecked((ulong)commandBuffer.Handle);
        lock (_commandBindStateLock)
        {
            _commandBindStates.TryGetValue(key, out CommandBufferBindState state);
            shouldBind = state.DescriptorHeapSignature != signature;
            if (shouldBind)
            {
                state.DescriptorHeapSignature = signature;
                _commandBindStates[key] = state;
            }
        }

        if (!shouldBind)
            return true;

        BindHeapInfoEXTNative samplerBindInfo = CreateSamplerHeapBindInfo();
        BindHeapInfoEXTNative resourceBindInfo = CreateResourceHeapBindInfo();
        _descriptorHeapApi.CmdBindSamplerHeap(commandBuffer, &samplerBindInfo);
        _descriptorHeapApi.CmdBindResourceHeap(commandBuffer, &resourceBindInfo);
        InvalidateDescriptorSetBindingState(commandBuffer);
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

    private bool TryAppendDescriptorHeapInheritancePNext(
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

    internal bool TryWriteDescriptorHeapSamplerDescriptors(
        uint samplerCount,
        SamplerCreateInfo* samplers,
        ulong destinationOffsetBytes,
        ulong destinationSizeBytes,
        out string reason)
    {
        reason = string.Empty;
        if (!TryResolveDescriptorHeapWriteDestination(
                _descriptorHeapSamplerStorage,
                destinationOffsetBytes,
                destinationSizeBytes,
                out HostAddressRangeEXTNative destination,
                out reason))
        {
            return false;
        }

        if (_descriptorHeapApi is null || samplers is null || samplerCount == 0)
        {
            reason = "descriptor heap sampler write has no API, samplers, or count.";
            return false;
        }

        Result result = _descriptorHeapApi.WriteSamplerDescriptors(device, samplerCount, samplers, &destination);
        if (result != Result.Success)
        {
            reason = $"vkWriteSamplerDescriptorsEXT failed ({result}).";
            return false;
        }

        _descriptorHeapSamplerWriteCount += samplerCount;
        _descriptorHeapSamplerHighWaterBytes = Math.Max(_descriptorHeapSamplerHighWaterBytes, destinationOffsetBytes + destinationSizeBytes);
        return true;
    }

    internal bool TryWriteDescriptorHeapResourceDescriptors(
        uint resourceCount,
        ResourceDescriptorInfoEXTNative* resources,
        ulong destinationOffsetBytes,
        ulong destinationSizeBytes,
        out string reason)
    {
        reason = string.Empty;
        if (!TryResolveDescriptorHeapWriteDestination(
                _descriptorHeapResourceStorage,
                destinationOffsetBytes,
                destinationSizeBytes,
                out HostAddressRangeEXTNative destination,
                out reason))
        {
            return false;
        }

        if (_descriptorHeapApi is null || resources is null || resourceCount == 0)
        {
            reason = "descriptor heap resource write has no API, resources, or count.";
            return false;
        }

        Result result = _descriptorHeapApi.WriteResourceDescriptors(device, resourceCount, resources, &destination);
        if (result != Result.Success)
        {
            reason = $"vkWriteResourceDescriptorsEXT failed ({result}).";
            return false;
        }

        _descriptorHeapResourceWriteCount += resourceCount;
        _descriptorHeapResourceHighWaterBytes = Math.Max(_descriptorHeapResourceHighWaterBytes, destinationOffsetBytes + destinationSizeBytes);
        return true;
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
        InvalidateDescriptorSetBindingState(commandBuffer);
        return true;
    }

    private static bool TryResolveDescriptorHeapWriteDestination(
        DescriptorHeapStorage storage,
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

    private readonly struct DescriptorHeapStorage(
        Buffer buffer,
        DeviceMemory memory,
        void* mapped,
        ulong size,
        ulong deviceAddress)
    {
        public Buffer Buffer { get; } = buffer;
        public DeviceMemory Memory { get; } = memory;
        public void* Mapped { get; } = mapped;
        public ulong Size { get; } = size;
        public ulong DeviceAddress { get; } = deviceAddress;
        public bool IsReady => Buffer.Handle != 0 && Memory.Handle != 0 && Mapped != null && Size > 0 && DeviceAddress != 0;
    }

    private sealed class VulkanDescriptorHeapNativeApi
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void CmdBindHeapDelegate(CommandBuffer commandBuffer, BindHeapInfoEXTNative* bindInfo);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void CmdPushDataDelegate(CommandBuffer commandBuffer, PushDataInfoEXTNative* pushDataInfo);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate ulong GetPhysicalDeviceDescriptorSizeDelegate(PhysicalDevice physicalDevice, DescriptorType descriptorType);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate Result WriteSamplerDescriptorsDelegate(Device device, uint samplerCount, SamplerCreateInfo* samplers, HostAddressRangeEXTNative* descriptors);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate Result WriteResourceDescriptorsDelegate(Device device, uint resourceCount, ResourceDescriptorInfoEXTNative* resources, HostAddressRangeEXTNative* descriptors);

        private CmdBindHeapDelegate? _cmdBindSamplerHeap;
        private CmdBindHeapDelegate? _cmdBindResourceHeap;
        private CmdPushDataDelegate? _cmdPushData;
        private GetPhysicalDeviceDescriptorSizeDelegate? _getPhysicalDeviceDescriptorSize;
        private WriteSamplerDescriptorsDelegate? _writeSamplerDescriptors;
        private WriteResourceDescriptorsDelegate? _writeResourceDescriptors;

        public bool TryLoad(Vk api, Instance instance, Device device, out string reason)
        {
            reason = string.Empty;
            _cmdBindSamplerHeap = LoadDeviceDelegate<CmdBindHeapDelegate>(api, device, "vkCmdBindSamplerHeapEXT");
            _cmdBindResourceHeap = LoadDeviceDelegate<CmdBindHeapDelegate>(api, device, "vkCmdBindResourceHeapEXT");
            _cmdPushData = LoadDeviceDelegate<CmdPushDataDelegate>(api, device, "vkCmdPushDataEXT");
            _writeSamplerDescriptors = LoadDeviceDelegate<WriteSamplerDescriptorsDelegate>(api, device, "vkWriteSamplerDescriptorsEXT");
            _writeResourceDescriptors = LoadDeviceDelegate<WriteResourceDescriptorsDelegate>(api, device, "vkWriteResourceDescriptorsEXT");
            _getPhysicalDeviceDescriptorSize = LoadInstanceDelegate<GetPhysicalDeviceDescriptorSizeDelegate>(
                api,
                instance,
                "vkGetPhysicalDeviceDescriptorSizeEXT");

            if (_cmdBindSamplerHeap is null ||
                _cmdBindResourceHeap is null ||
                _cmdPushData is null ||
                _writeSamplerDescriptors is null ||
                _writeResourceDescriptors is null)
            {
                reason =
                    $"missing entry points: bindSampler={_cmdBindSamplerHeap is not null}, bindResource={_cmdBindResourceHeap is not null}, pushData={_cmdPushData is not null}, writeSampler={_writeSamplerDescriptors is not null}, writeResource={_writeResourceDescriptors is not null}.";
                return false;
            }

            return true;
        }

        public void CmdBindSamplerHeap(CommandBuffer commandBuffer, BindHeapInfoEXTNative* bindInfo)
            => _cmdBindSamplerHeap!(commandBuffer, bindInfo);

        public void CmdBindResourceHeap(CommandBuffer commandBuffer, BindHeapInfoEXTNative* bindInfo)
            => _cmdBindResourceHeap!(commandBuffer, bindInfo);

        public void CmdPushData(CommandBuffer commandBuffer, PushDataInfoEXTNative* pushDataInfo)
            => _cmdPushData!(commandBuffer, pushDataInfo);

        public bool TryGetDescriptorSize(PhysicalDevice physicalDevice, DescriptorType descriptorType, out ulong size)
        {
            size = 0;
            if (_getPhysicalDeviceDescriptorSize is null)
                return false;

            size = _getPhysicalDeviceDescriptorSize(physicalDevice, descriptorType);
            return size > 0;
        }

        public Result WriteSamplerDescriptors(Device device, uint samplerCount, SamplerCreateInfo* samplers, HostAddressRangeEXTNative* descriptors)
            => _writeSamplerDescriptors!(device, samplerCount, samplers, descriptors);

        public Result WriteResourceDescriptors(Device device, uint resourceCount, ResourceDescriptorInfoEXTNative* resources, HostAddressRangeEXTNative* descriptors)
            => _writeResourceDescriptors!(device, resourceCount, resources, descriptors);

        private static TDelegate? LoadDeviceDelegate<TDelegate>(Vk api, Device device, string name)
            where TDelegate : Delegate
        {
            nint proc = (nint)api.GetDeviceProcAddr(device, name);
            return proc == 0
                ? null
                : Marshal.GetDelegateForFunctionPointer<TDelegate>(proc);
        }

        private static TDelegate? LoadInstanceDelegate<TDelegate>(Vk api, Instance instance, string name)
            where TDelegate : Delegate
        {
            nint proc = (nint)api.GetInstanceProcAddr(instance, name);
            return proc == 0
                ? null
                : Marshal.GetDelegateForFunctionPointer<TDelegate>(proc);
        }
    }
}
