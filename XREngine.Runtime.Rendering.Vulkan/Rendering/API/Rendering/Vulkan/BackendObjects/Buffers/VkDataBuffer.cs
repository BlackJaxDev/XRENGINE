using XREngine.Extensions;
using Silk.NET.Vulkan;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan
{
        /// <summary>
        /// Vulkan data buffer with best practices: staging, synchronization, descriptor integration, lifetime, mapping, error handling, and multi-frame support.
        /// </summary>
    internal unsafe partial class VkDataBuffer(
        VulkanBackendObjectContext backendContext,
        XRDataBuffer buffer) : VkObject<XRDataBuffer>(backendContext, buffer), IApiDataBuffer
        {
            private const ulong IndirectCopyDeviceAddressThresholdBytes = 256UL * 1024UL;
            private const ulong DeviceLocalStaticUploadMinimumBytes = 64UL * 1024UL;

            // --- Resource handles ---
            private Buffer? _vkBuffer; // Device-local or host-visible buffer
            private DeviceMemory? _vkMemory;
            private ulong _bufferSize = 0;

            /// <summary>
            /// Tracks the currently allocated GPU memory size for this buffer in bytes.
            /// </summary>
            private long _allocatedVRAMBytes = 0;

            // For dynamic/multi-frame: per-frame buffers (optional, not fully implemented)
            // private VulkanBuffer[]? _perFrameBuffers;
            // private VulkanDeviceMemory[]? _perFrameMemories;

            // For persistent mapping
            private void* _persistentMappedPtr = null;

            // For resource lifetime management
            private BufferUsageFlags _lastUsageFlags;
            private MemoryPropertyFlags _lastMemProps;
            private bool _lastDeviceAddressEnabled;
            private ulong _uploadedByteCount;
            private bool _hasPendingUpload;
            private bool _lastUploadUsedCompressedGpuPath;
            private string _lastUploadRoute = "None";
            private string _lastBindingName = string.Empty;
            private bool _requiresStorageBufferUsage;
            private readonly Dictionary<XRRenderProgram, uint> _resolvedProgramBindings = [];
            private readonly object _queuedUploadSync = new();
            private bool _queuedRenderThreadUpload;
            private bool _queuedUploadIsFull;
            private bool _queuedSubUpload;
            private uint _queuedSubUploadStart;
            private uint _queuedSubUploadEnd;
            private string? _renderThreadUploadJobLabel;

            // --- Event wiring ---
            protected override void UnlinkData()
            {
                Data.PushDataRequested -= PushData;
                Data.PushSubDataRequested -= PushSubData;
                Data.FlushRequested -= Flush;
                Data.FlushRangeRequested -= FlushRange;
                Data.SetBlockNameRequested -= SetUniformBlockName;
                Data.SetBlockIndexRequested -= SetBlockIndex;
                Data.BindRequested -= Bind;
                Data.UnbindRequested -= Unbind;
                Data.MapBufferDataRequested -= MapBufferData;
                Data.UnmapBufferDataRequested -= UnmapBufferData;
                Data.BindSSBORequested -= BindSSBO;
            }
            protected override void LinkData()
            {
                Data.PushDataRequested += PushData;
                Data.PushSubDataRequested += PushSubData;
                Data.FlushRequested += Flush;
                Data.FlushRangeRequested += FlushRange;
                Data.SetBlockNameRequested += SetUniformBlockName;
                Data.SetBlockIndexRequested += SetBlockIndex;
                Data.BindRequested += Bind;
                Data.UnbindRequested += Unbind;
                Data.MapBufferDataRequested += MapBufferData;
                Data.UnmapBufferDataRequested += UnmapBufferData;
                Data.BindSSBORequested += BindSSBO;
            }

            public override VkObjectType Type => VkObjectType.Buffer;

            /// <summary>
            /// Exposes the backing Vulkan buffer handle for binding in render commands.
            /// </summary>
            public Buffer? BufferHandle => _vkBuffer;

            /// <summary>
            /// Exposes the backing Vulkan memory handle. Primarily useful for debugging.
            /// </summary>
            public DeviceMemory? MemoryHandle => _vkMemory;
            public ulong AllocatedByteSize => _bufferSize;
            internal BufferUsageFlags LastUsageFlags => _lastUsageFlags;
            public ulong DeviceAddress { get; private set; }
            public ulong UploadedByteCount => _uploadedByteCount;
            public bool HasPendingUpload => _hasPendingUpload;
            public bool IsReadyForRendering => IsGenerated && !_hasPendingUpload && _uploadedByteCount >= (ulong)Data.Length && _bufferSize >= (ulong)Data.Length;
            public string LastUploadRoute => _lastUploadRoute;
            public string LastBindingName => _lastBindingName;
            public ulong BackendAllocatedByteSize => _bufferSize;
            public ulong BackendUploadedByteCount => _uploadedByteCount;
            public bool BackendHasPendingUpload => _hasPendingUpload;
            public bool BackendIsReadyForGpuUse => IsReadyForRendering;
            public bool BackendIsPersistentlyMapped => _persistentMappedPtr != null;
            public XRBufferResolvedRoute BackendResolvedRoute => ResolveBackendRoute();

            public bool TryGetGpuAddress(out ulong address, out string downgradeReason)
            {
                address = DeviceAddress;
                if (address != 0ul)
                {
                    downgradeReason = string.Empty;
                    return true;
                }

                downgradeReason = BackendContext.Resources.Buffers.ResolveDeviceAddressStatus(BackendContext, Data, DeviceAddress);
                Data.ReportDeviceAddressDowngrade(downgradeReason);
                return false;
            }

            internal void EnsureReadyForRendering()
                => TryEnsureReadyForRendering(allowSynchronousUpload: true);

            internal bool TryEnsureReadyForRendering(bool allowSynchronousUpload)
            {
                bool canUploadNow = CanUploadFromRenderReadinessCheck(allowSynchronousUpload);
                if (!IsActive)
                {
                    if (!canUploadNow)
                    {
                        if (allowSynchronousUpload)
                            TraceDeferredRenderThreadUpload("TryEnsureReady.Generate");
                        return IsReadyForRendering;
                    }

                    Generate();
                    return IsReadyForRendering;
                }

                if (IsReadyForRendering)
                    return true;

                if (!canUploadNow)
                {
                    if (allowSynchronousUpload)
                        TraceDeferredRenderThreadUpload("TryEnsureReady.PushData");
                    return false;
                }

                PushData();
                return IsReadyForRendering;
            }

            private XRBufferResolvedRoute ResolveBackendRoute()
            {
                if (_lastUploadRoute.Contains("Readback", StringComparison.OrdinalIgnoreCase))
                    return XRBufferResolvedRoute.Readback;
                if (_lastUploadRoute.Contains("DeviceLocal", StringComparison.OrdinalIgnoreCase))
                    return XRBufferResolvedRoute.DeviceLocal;
                if (_lastUploadRoute.Contains("Staging", StringComparison.OrdinalIgnoreCase))
                    return XRBufferResolvedRoute.StagingUpload;
                if (_persistentMappedPtr != null)
                    return XRBufferResolvedRoute.PersistentMappedRing;
                if ((_lastMemProps & MemoryPropertyFlags.HostVisibleBit) != 0)
                    return XRBufferResolvedRoute.HostVisible;

                return XRBufferPolicyResolver.ResolveVulkan(
                    Data.DefaultMemoryPolicy,
                    supportsPersistentRing: true,
                    supportsDeviceLocal: true);
            }

            private void ReportBackendState()
                => Data.ReportBackendUploadState(
                    BackendAllocatedByteSize,
                    BackendUploadedByteCount,
                    BackendHasPendingUpload,
                    BackendResolvedRoute,
                    BackendIsReadyForGpuUse);

            public bool TryGetDeviceAddress(out ulong address)
            {
                address = DeviceAddress;
                return address != 0ul;
            }

            public override string GetDescribingName()
            {
                string? name = Data.Name;
                if (!string.IsNullOrWhiteSpace(name))
                    return name;

                string? attributeName = Data.AttributeName;
                if (!string.IsNullOrWhiteSpace(attributeName))
                    return attributeName;

                return base.GetDescribingName();
            }

            protected internal override void PostGenerated()
            {
                if (!RuntimeEngine.IsRenderThread)
                {
                    TraceDeferredRenderThreadUpload("PostGenerated");
                    return;
                }

                if (Data.Resizable)
                    PushData();
                else
                    AllocateImmutable();

                if (Data.ShouldMap)
                    MapBufferData();
            }

            /// <summary>
            /// Pushes data to the GPU. Uses staging buffer for device-local, host-visible for dynamic.
            /// </summary>
            public void PushData()
            {
                if (SkipUploadBecauseDeviceLost("PushData"))
                    return;
                if (HasBlockingActiveMapping())
                    return;
                if (!RuntimeEngine.IsRenderThread)
                {
                    EnqueueRenderThreadUpload(fullUpload: true, offset: 0, length: Data.Length, "PushData");
                    return;
                }

                // Determine usage and memory flags
                BufferUsageFlags usage = ResolveVkUsageFlags(Data.Target, Data.Usage);
                MemoryPropertyFlags memProps = ResolveMemoryProperties(Data, Data.Length);
                bool enableDeviceAddress =
                    BackendContext.Resources.Buffers.ShouldEnableDeviceAddress(BackendContext, Data) ||
                    BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap;
                if (enableDeviceAddress)
                    usage |= BufferUsageFlags.ShaderDeviceAddressBit;

                ulong requiredByteSize = Data.Length;
                bool useCapacityBackedAllocation = Data.Resizable && !Data.HasGpuCompressedPayload;
                bool needsRecreate =
                    _vkBuffer is null ||
                    _vkMemory is null ||
                    requiredByteSize > _bufferSize ||
                    _lastUsageFlags != usage ||
                    _lastMemProps != memProps ||
                    _lastDeviceAddressEnabled != enableDeviceAddress ||
                    (_immutableStorageSet &&
                     (Data.StorageFlags & EBufferMapStorageFlags.DynamicStorage) == 0);

                if (needsRecreate)
                {
                    bool replacesExistingBacking = _vkBuffer.HasValue || _vkMemory.HasValue;
                    ulong requestedAllocationBytes = useCapacityBackedAllocation
                        ? ResolveResizableBufferCapacity(_bufferSize, requiredByteSize)
                        : Math.Max(requiredByteSize, 1UL);
                    if (!CanAllocateBufferVram(requestedAllocationBytes))
                        return;

                    _hasPendingUpload = true;
                    ReportBackendState();

                    // Retire old buffer handles for deferred cleanup ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â the command buffer
                    // currently being recorded may still reference them, so we must not
                    // destroy them synchronously.  Do NOT call Destroy() here because
                    // that also resets _bindingId, which would make IsActive return false
                    // and trigger redundant Generate() cycles on every draw call.
                    if (_vkBuffer.HasValue || _vkMemory.HasValue)
                    {
                        ReleasePersistentMappingBeforeResourceRetire();
                        if (_vkBuffer.HasValue && _vkMemory.HasValue)
                            BackendContext.Resources.Buffers.Retire(_vkBuffer.Value, _vkMemory.Value, "VkDataBuffer.PushData");
                        else
                        {
                            // Partial state ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â still retire to avoid use-after-free.
                            if (_vkBuffer.HasValue)
                                BackendContext.Resources.Buffers.Retire(_vkBuffer.Value, default, "VkDataBuffer.PushData.PartialState");
                            if (_vkMemory.HasValue)
                                BackendContext.Resources.Buffers.Retire(default, _vkMemory.Value, "VkDataBuffer.PushData.PartialState");
                        }
                        _vkBuffer = null;
                        _vkMemory = null;
                        _uploadedByteCount = 0ul;
                        DeviceAddress = 0ul;
                    }
                    if (_allocatedVRAMBytes > 0)
                    {
                        RuntimeEngine.Rendering.Stats.Vram.RemoveBufferAllocation(_allocatedVRAMBytes);
                        _allocatedVRAMBytes = 0;
                    }

                    _bufferSize = requestedAllocationBytes;
                    bool uploadedContent = requiredByteSize == 0;
                    _lastUsageFlags = usage;
                    _lastMemProps = memProps;
                    _lastDeviceAddressEnabled = enableDeviceAddress;
                    _lastUploadUsedCompressedGpuPath = false;

                    // --- Staging buffer pattern for device-local ---
                    if (ShouldUseDeviceLocal(Data, _bufferSize))
                    {
                        bool canUseGpuDecompression = CanUseGpuDecompressionUpload();
                        bool preferIndirectCopy = ShouldUseDeviceAddressForIndirectCopy(_bufferSize);
                        bool createDeviceAddress = preferIndirectCopy || enableDeviceAddress || canUseGpuDecompression;
                        _lastUploadRoute = "DeviceLocalFrameDataArena";

                        // Create device-local buffer first.
                        BufferUsageFlags deviceUsage = usage | BufferUsageFlags.TransferDstBit;
                        if (createDeviceAddress)
                            deviceUsage |= BufferUsageFlags.ShaderDeviceAddressBit;

                        var (deviceBuffer, deviceMemory) = BackendContext.Resources.Buffers.Create(
                            BackendContext,
                            _bufferSize,
                            deviceUsage,
                            MemoryPropertyFlags.DeviceLocalBit,
                            null,
                            createDeviceAddress);
                        _vkBuffer = deviceBuffer;
                        _vkMemory = deviceMemory;

                        if (TryUploadGpuCompressedPayload(deviceBuffer))
                        {
                            // GPU-side decompression upload succeeded; no staging copy required.
                            _lastUploadUsedCompressedGpuPath = true;
                            _lastUploadRoute = "DeviceLocalGpuDecompression";
                            uploadedContent = true;
                        }
                        // Ordinary CPU data uses the persistent frame-slot arena. GPU-compressed
                        // payloads retain their device-address staging requirement above.
                        else if (requiredByteSize > 0 && TryGetUploadSlice(0, (uint)requiredByteSize, out VoidPtr sourceSlice))
                        {
                            uploadedContent = UploadDeviceLocalRangeFromFrameDataArena(
                                sourceSlice,
                                checked((uint)requiredByteSize),
                                deviceBuffer,
                                destinationOffset: 0,
                                "VkDataBuffer.PushData");
                        }
                        else if (Data.HasGpuCompressedPayload)
                        {
                            Debug.VulkanWarningEvery(
                                $"VkDataBuffer.CompressedFallback.{GetDescribingName()}",
                                TimeSpan.FromSeconds(5),
                                "[VkDataBuffer] '{0}' could not use GPU decompression; falling back to empty device-local allocation until CPU data is available. codec={1} decodedBytes={2} supportsDecompression={3} supportsBda={4}.",
                                GetDescribingName(),
                                Data.GpuCompressionCodec,
                                Data.GpuCompressedDecodedLength,
                                BackendContext.Supports(EVulkanDeviceCapability.NvMemoryDecompression),
                                BackendContext.Supports(EVulkanDeviceCapability.BufferDeviceAddress));
                            _lastUploadRoute = "DeviceLocalCompressedFallbackMissingCpuData";
                        }
                    }
                    else
                    {
                        // Host-visible buffer for dynamic/stream
                        _lastUploadRoute = ResolveHostVisibleUploadRoute(memProps);
                        VoidPtr initialData = _bufferSize == requiredByteSize && Data.TryGetAddress(out var address)
                            ? address
                            : VoidPtr.Zero;
                        (_vkBuffer, _vkMemory) = BackendContext.Resources.Buffers.Create(
                            BackendContext,
                            _bufferSize,
                            usage,
                            memProps,
                            initialData,
                            enableDeviceAddress);
                        if (requiredByteSize > 0 && initialData == VoidPtr.Zero)
                            PushSubData(0, checked((uint)requiredByteSize));
                        uploadedContent = requiredByteSize == 0 || initialData != VoidPtr.Zero || _uploadedByteCount >= requiredByteSize;
                    }

                    RefreshDeviceAddress();
                    _uploadedByteCount = uploadedContent ? requiredByteSize : 0ul;
                    _hasPendingUpload = false;
                    ReportBackendState();

                    // Track VRAM allocation only when the actual backing allocation is device-local.
                    TrackCurrentBufferVramAllocation();
                }
                else
                {
                    // Reuse the existing allocation and upload fresh data even when size/usage are unchanged.
                    PushSubData(0, Data.Length);
                    if (ShouldDisposeAfterUpload())
                        Data.Dispose();
                    return;
                }

                BackendContext.Resources.PlannerPublications.TrackBufferBinding(Data);
                RecordUploadDiagnostics((long)_bufferSize, recreate: needsRecreate, fullUpload: true);
                ReportBackendState();

                if (ShouldDisposeAfterUpload())
                    Data.Dispose();
            }

            internal static ulong ResolveResizableBufferCapacity(ulong currentCapacity, ulong requiredBytes)
            {
                const ulong MinimumCapacity = 256UL;
                ulong capacity = Math.Max(currentCapacity, MinimumCapacity);
                ulong required = Math.Max(requiredBytes, 1UL);
                while (capacity < required)
                {
                    ulong next = capacity <= ulong.MaxValue / 2UL
                        ? capacity * 2UL
                        : ulong.MaxValue;
                    if (next <= capacity)
                        return required;
                    capacity = next;
                }

                return capacity;
            }

            private void TrackCurrentBufferVramAllocation()
            {
                if (_allocatedVRAMBytes > 0)
                {
                    RuntimeEngine.Rendering.Stats.Vram.RemoveBufferAllocation(_allocatedVRAMBytes);
                    _allocatedVRAMBytes = 0;
                }

                if (!_vkBuffer.HasValue ||
                    !BackendContext.Resources.Buffers.TryGetAllocation(_vkBuffer.Value, out VulkanMemoryAllocation allocation) ||
                    !VulkanBufferResourceService.IsDeviceLocalVramAllocation(allocation.Properties))
                {
                    return;
                }

                _allocatedVRAMBytes = ClampToTrackedVramBytes(allocation.Size);
                RuntimeEngine.Rendering.Stats.Vram.AddBufferAllocation(_allocatedVRAMBytes);
            }

            private static long ClampToTrackedVramBytes(ulong bytes)
                => bytes > (ulong)long.MaxValue ? long.MaxValue : (long)bytes;

            /// <summary>
            /// Pushes a subrange of data to the GPU. Uses staging if device-local.
            /// </summary>
            public void PushSubData(int offset, uint length)
            {
                if (SkipUploadBecauseDeviceLost("PushSubData"))
                    return;
                if (HasBlockingActiveMapping())
                    return;
                if (offset < 0)
                {
                    TracePushSubData(offset, length, "negative-offset-ignored");
                    return;
                }
                if (length == 0)
                    return;

                uint totalLength = Data.Length;
                if (!RuntimeEngine.IsRenderThread)
                {
                    EnqueueRenderThreadUpload(fullUpload: length >= totalLength && offset == 0, offset, length, "PushSubData");
                    return;
                }

                if ((uint)offset >= totalLength)
                {
                    Debug.VulkanWarningEvery(
                        $"VkDataBuffer.PushSubData.OffsetPastLength.{GetDescribingName()}",
                        TimeSpan.FromSeconds(5),
                        "[VkDataBuffer] PushSubData skipped for '{0}': offset {1} exceeds buffer length {2}.",
                        GetDescribingName(),
                        offset,
                        totalLength);
                    TracePushSubData(offset, length, "offset-past-data-ignored");
                    return;
                }

                uint clampedLength = Math.Min(length, totalLength - (uint)offset);
                if (clampedLength != length)
                    TracePushSubData(offset, length, $"clamp-client {length}->{clampedLength}");
                if (clampedLength == 0)
                    return;

                if (_vkBuffer == null || _vkMemory == null || (ulong)totalLength > _bufferSize)
                {
                    TracePushSubData(offset, clampedLength, "grow-full-upload");
                    PushData();
                    return;
                }

                if (_vkBuffer is null || _vkMemory is null)
                    return;

                ulong gpuAvailable = _bufferSize > (ulong)offset
                    ? _bufferSize - (ulong)offset
                    : 0UL;
                if (gpuAvailable == 0UL)
                {
                    TracePushSubData(offset, clampedLength, "offset-past-allocation-ignored");
                    return;
                }

                if ((ulong)clampedLength > gpuAvailable)
                {
                    uint originalLength = clampedLength;
                    clampedLength = (uint)Math.Min(gpuAvailable, uint.MaxValue);
                    Debug.VulkanWarningEvery(
                        $"VkDataBuffer.PushSubData.ClampGpu.{GetDescribingName()}",
                        TimeSpan.FromSeconds(5),
                        "[VkDataBuffer] PushSubData clamped for '{0}': requested {1}+{2}, allocated {3}.",
                        GetDescribingName(),
                        offset,
                        originalLength,
                        _bufferSize);
                    TracePushSubData(offset, originalLength, $"clamp-gpu {originalLength}->{clampedLength}");
                }
                if (clampedLength == 0)
                    return;

                if (_immutableStorageSet &&
                    (Data.StorageFlags & EBufferMapStorageFlags.DynamicStorage) == 0)
                {
                    TracePushSubData(offset, clampedLength, "immutable-no-dynstore-full-upload");
                    PushData();
                    return;
                }

                // For device-local, use staging buffer for subdata
                if (IsUsingDeviceLocalBacking())
                {
                    if (Data.HasGpuCompressedPayload)
                    {
                        // Partial sub-updates are not meaningful for compressed payload uploads.
                        // Re-run full upload path so decompression/copy logic remains consistent.
                        PushData();
                        return;
                    }

                    if (!TryGetUploadSlice(offset, clampedLength, out VoidPtr sourceSlice))
                        return;

                    _lastUploadRoute = "DeviceLocalSubDataFrameDataArena";
                    _ = UploadDeviceLocalRangeFromFrameDataArena(
                        sourceSlice,
                        clampedLength,
                        _vkBuffer.Value,
                        checked((ulong)offset),
                        "VkDataBuffer.PushSubData");
                }
                else
                {
                    if (!TryGetUploadSlice(offset, clampedLength, out VoidPtr sourceSlice))
                        return;

                    // Host-visible: map, copy, unmap
                    _lastUploadRoute = ResolveHostVisibleSubDataUploadRoute(_lastMemProps);
                    BackendContext.Resources.Buffers.Update(BackendContext, _vkBuffer.Value, _vkMemory.Value, (ulong)offset, (ulong)clampedLength, sourceSlice.Pointer);
                }

                ulong uploadedEnd = (ulong)offset + clampedLength;
                if (uploadedEnd > _uploadedByteCount)
                    _uploadedByteCount = uploadedEnd;
                _hasPendingUpload = false;
                BackendContext.Resources.PlannerPublications.TrackBufferBinding(Data);
                RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.BufferUploadBytes, clampedLength);
                TracePushSubData(offset, clampedLength, "done");
                ReportBackendState();
            }

            private string RenderThreadUploadJobLabel
                => _renderThreadUploadJobLabel ??= $"VkDataBuffer.Upload:{GetDescribingName()}";

            private void EnqueueRenderThreadUpload(bool fullUpload, int offset, uint length, string reason)
            {
                bool shouldQueue = false;
                lock (_queuedUploadSync)
                {
                    _hasPendingUpload = true;

                    if (fullUpload)
                    {
                        _queuedUploadIsFull = true;
                        _queuedSubUpload = false;
                        _queuedSubUploadStart = 0u;
                        _queuedSubUploadEnd = 0u;
                    }
                    else if (!_queuedUploadIsFull)
                    {
                        uint start = (uint)Math.Max(offset, 0);
                        ulong requestedEnd = (ulong)start + length;
                        uint end = requestedEnd > uint.MaxValue ? uint.MaxValue : (uint)requestedEnd;

                        if (!_queuedSubUpload)
                        {
                            _queuedSubUpload = true;
                            _queuedSubUploadStart = start;
                            _queuedSubUploadEnd = end;
                        }
                        else
                        {
                            if (start < _queuedSubUploadStart)
                                _queuedSubUploadStart = start;
                            if (end > _queuedSubUploadEnd)
                                _queuedSubUploadEnd = end;
                        }
                    }

                    if (!_queuedRenderThreadUpload)
                    {
                        _queuedRenderThreadUpload = true;
                        shouldQueue = true;
                    }
                }

                TraceQueuedUpload(reason);
                if (!shouldQueue)
                    return;

                if (!RuntimeEngine.InvokeOnMainThread(DrainQueuedRenderThreadUpload, RenderThreadUploadJobLabel))
                    DrainQueuedRenderThreadUpload();
            }

            private void DrainQueuedRenderThreadUpload()
            {
                bool fullUpload;
                bool subUpload;
                uint subStart;
                uint subEnd;

                lock (_queuedUploadSync)
                {
                    fullUpload = _queuedUploadIsFull;
                    subUpload = _queuedSubUpload;
                    subStart = _queuedSubUploadStart;
                    subEnd = _queuedSubUploadEnd;

                    _queuedRenderThreadUpload = false;
                    _queuedUploadIsFull = false;
                    _queuedSubUpload = false;
                    _queuedSubUploadStart = 0u;
                    _queuedSubUploadEnd = 0u;
                }

                if (fullUpload)
                {
                    PushData();
                    return;
                }

                if (subUpload && subEnd > subStart)
                {
                    PushSubData(checked((int)subStart), subEnd - subStart);
                    return;
                }

                _hasPendingUpload = false;
                ReportBackendState();
            }

            private bool TryUploadGpuCompressedPayload(Buffer deviceBuffer)
            {
                if (!CanUseGpuDecompressionUpload() || Data.GpuCompressedSource is null)
                    return false;

                ulong decodedLength = _bufferSize;
                ulong expectedDecodedLength = Data.GpuCompressedDecodedLength;
                if (decodedLength == 0 || expectedDecodedLength == 0 || expectedDecodedLength != decodedLength)
                    return false;

                DataSource compressedSource = Data.GpuCompressedSource;
                ulong compressedLength = compressedSource.Length;
                if (compressedLength == 0)
                    return false;

                BufferUsageFlags stagingUsage = BufferUsageFlags.TransferSrcBit | BufferUsageFlags.ShaderDeviceAddressBit;
                MemoryPropertyFlags stagingProps = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;

                var (compressedBuffer, compressedMemory) = BackendContext.Resources.Buffers.Create(
                    BackendContext,
                    compressedLength,
                    stagingUsage,
                    stagingProps,
                    compressedSource.Address,
                    enableDeviceAddress: true);

                try
                {
                    return BackendContext.Resources.SynchronousCommands.TryDecompressBufferGDeflate(
                        compressedBuffer,
                        sourceOffset: 0,
                        compressedSize: compressedLength,
                        destination: deviceBuffer,
                        destinationOffset: 0,
                        decompressedSize: decodedLength,
                        owner: "VkDataBuffer.GpuDecompression");
                }
                finally
                {
                    BackendContext.Resources.Buffers.Destroy(BackendContext, compressedBuffer, compressedMemory, "VkDataBuffer.GpuDecompression.Staging");
                }
            }

            private bool CanUseGpuDecompressionUpload()
                => Data.HasGpuCompressedPayload &&
                   Data.GpuCompressionCodec == XRDataBuffer.EBufferCompressionCodec.GDeflate &&
                   BackendContext.Supports(EVulkanDeviceCapability.NvMemoryDecompression) &&
                   BackendContext.Supports(EVulkanDeviceCapability.BufferDeviceAddress);

            private bool ShouldUseDeviceAddressForIndirectCopy(ulong byteCount)
                => byteCount >= IndirectCopyDeviceAddressThresholdBytes &&
                   BackendContext.Resources.Buffers.CanUseNvIndirectCopyUploads(BackendContext);
            public void PushSubData() => PushSubData(0, Data.Length);

            private void RefreshDeviceAddress()
            {
                DeviceAddress = 0ul;
                if (!_lastDeviceAddressEnabled || !BackendContext.Supports(EVulkanDeviceCapability.BufferDeviceAddress) || _vkBuffer is not { } buffer)
                    return;

                DeviceAddress = BackendContext.Resources.Buffers.GetDeviceAddress(BackendContext, buffer);
            }

            /// <summary>
            /// Flushes mapped memory range. Only needed for non-coherent memory.
            /// </summary>
            public void Flush()
            {
                if (RuntimeEngine.InvokeOnMainThread(Flush, "VkDataBuffer.Flush"))
                    return;
                if (!CanFlushMappedMemory(out ulong length))
                    return;
                // Only needed for non-coherent memory
                if ((_lastMemProps & MemoryPropertyFlags.HostCoherentBit) == 0)
                    BackendContext.Resources.Buffers.Flush(BackendContext, _vkBuffer!.Value, _vkMemory!.Value, GetMappedMemoryOffset(0), length);
            }
            public void FlushRange(int offset, uint length)
            {
                if (RuntimeEngine.InvokeOnMainThread(() => FlushRange(offset, length), "VkDataBuffer.FlushRange"))
                    return;
                if (!NormalizeMappedRange(offset, length, out ulong memoryOffset, out ulong mappedLength))
                    return;
                if ((_lastMemProps & MemoryPropertyFlags.HostCoherentBit) == 0)
                    BackendContext.Resources.Buffers.Flush(BackendContext, _vkBuffer!.Value, _vkMemory!.Value, memoryOffset, mappedLength);
            }

            // --- Persistent mapping for dynamic buffers ---
            private DataSource? _gpuSideSource = null;
            public DataSource? GPUSideSource
            {
                get => _gpuSideSource;
                set => SetField(ref _gpuSideSource, value);
            }
            private bool _immutableStorageSet = false;
            public bool ImmutableStorageSet
            {
                get => _immutableStorageSet;
                set => SetField(ref _immutableStorageSet, value);
            }

            /// <summary>
            /// Maps buffer memory for CPU access. For dynamic/host-visible buffers, supports persistent mapping.
            /// </summary>
            public void MapBufferData()
            {
                if (!BackendContext.IsDeviceOperational)
                    return;
                if (Data.ActivelyMapping.Count > 0)
                {
                    Debug.VulkanWarning($"Buffer {GetDescribingName()} is already mapped.");
                    return;
                }
                if (Data.Resizable)
                    EnsureStorageAllocatedForGpuUse();
                if (RuntimeEngine.InvokeOnMainThread(MapBufferData, "VkDataBuffer.MapBufferData"))
                    return;
                MapToClientSide();
            }
            public void MapToClientSide()
            {
                if (!BackendContext.IsDeviceOperational)
                    return;
                if (_vkBuffer == null || _vkMemory == null)
                    EnsureStorageAllocatedForGpuUse();
                if (_vkBuffer == null || _vkMemory == null)
                    return;
                if ((_lastMemProps & MemoryPropertyFlags.HostVisibleBit) == 0)
                {
                    Debug.VulkanWarningEvery(
                        $"VkDataBuffer.Map.DeviceLocal.{GetDescribingName()}",
                        TimeSpan.FromSeconds(5),
                        "[VkDataBuffer] '{0}' cannot be mapped directly because it is device-local. Use a readback buffer path for CPU reads.",
                        GetDescribingName());
                    return;
                }
                WarnUnsupportedMappingFlags();
                GPUSideSource?.Dispose();
                // Persistent mapping for dynamic buffers
                if (_persistentMappedPtr == null)
                    _persistentMappedPtr = MapCurrentBufferOrThrow(0, Math.Max(_bufferSize, 1UL));
                if (_persistentMappedPtr == null)
                    return;
                GPUSideSource = new DataSource(_persistentMappedPtr, (uint)_bufferSize);
                RecordMappedReadbackBytes(_bufferSize);
                if (!Data.ActivelyMapping.Contains(this))
                    Data.ActivelyMapping.Add(this);
            }
            public void MapToClientSide(int offset, uint length)
            {
                if (!BackendContext.IsDeviceOperational)
                    return;
                if (_vkBuffer == null || _vkMemory == null)
                    EnsureStorageAllocatedForGpuUse();
                if (_vkBuffer == null || _vkMemory == null)
                    return;
                if ((_lastMemProps & MemoryPropertyFlags.HostVisibleBit) == 0)
                {
                    Debug.VulkanWarningEvery(
                        $"VkDataBuffer.MapRange.DeviceLocal.{GetDescribingName()}",
                        TimeSpan.FromSeconds(5),
                        "[VkDataBuffer] '{0}' cannot be mapped directly because it is device-local. Use a readback buffer path for CPU reads.",
                        GetDescribingName());
                    return;
                }
                if (!NormalizeMappedRange(offset, length, out ulong memoryOffset, out ulong mappedLength))
                    return;
                WarnUnsupportedMappingFlags();
                GPUSideSource?.Dispose();
                if (_persistentMappedPtr == null)
                    _persistentMappedPtr = MapCurrentBufferOrThrow((ulong)offset, mappedLength);
                if (_persistentMappedPtr == null)
                    return;
                GPUSideSource = new DataSource(_persistentMappedPtr, (uint)mappedLength);
                RecordMappedReadbackBytes(mappedLength);
                if (!Data.ActivelyMapping.Contains(this))
                    Data.ActivelyMapping.Add(this);
            }

            private ulong GetMappedMemoryOffset(ulong bufferOffset)
                => _vkBuffer.HasValue
                    ? BackendContext.Resources.Buffers.GetAllocationOffset(_vkBuffer.Value) + bufferOffset
                    : bufferOffset;

            private void* MapCurrentBufferOrThrow(ulong offset, ulong length)
            {
                if (!_vkBuffer.HasValue || !_vkMemory.HasValue ||
                    !BackendContext.Resources.Buffers.TryMap(
                        BackendContext,
                        _vkBuffer.Value,
                        _vkMemory.Value,
                        offset,
                        length,
                        out void* mapped))
                {
                    throw new InvalidOperationException("Failed to map Vulkan buffer memory.");
                }

                return mapped;
            }

            private void UnmapCurrentBuffer()
            {
                if (_vkBuffer.HasValue && _vkMemory.HasValue)
                    BackendContext.Resources.Buffers.Unmap(BackendContext, _vkBuffer.Value, _vkMemory.Value);
            }

            public uint GetLength()
            {
                var existingSource = Data.ClientSideSource;
                return existingSource is not null ? existingSource.Length : Data.Length;
            }

            /// <summary>
            /// Allocates immutable storage (device-local, staging upload).
            /// </summary>
            public void AllocateImmutable()
            {
                PushData();
                ImmutableStorageSet = true;
            }

            /// <summary>
            /// Unmaps buffer memory. For persistent mapping, only unmap if mapped.
            /// </summary>
            public void UnmapBufferData()
            {
                if (!Data.ActivelyMapping.Contains(this))
                    return;
                if (RuntimeEngine.InvokeOnMainThread(UnmapBufferData, "VkDataBuffer.UnmapBufferData"))
                    return;
                if (_persistentMappedPtr != null)
                {
                    if ((Data.RangeFlags &
                         (EBufferMapRangeFlags.Read |
                          EBufferMapRangeFlags.InvalidateRange |
                          EBufferMapRangeFlags.InvalidateBuffer)) != 0)
                    {
                        BackendContext.Resources.Buffers.Invalidate(BackendContext, _vkMemory!.Value, GetMappedMemoryOffset(0), _bufferSize);
                    }

                    UnmapCurrentBuffer();
                    _persistentMappedPtr = null;
                }
                Data.ActivelyMapping.Remove(this);
                GPUSideSource?.Dispose();
                GPUSideSource = null;
            }

            private void ReleasePersistentMappingBeforeResourceRetire()
            {
                if (_persistentMappedPtr != null)
                {
                    if (_vkBuffer.HasValue && _vkMemory.HasValue)
                    {
                        if ((Data.RangeFlags &
                             (EBufferMapRangeFlags.Read |
                              EBufferMapRangeFlags.InvalidateRange |
                              EBufferMapRangeFlags.InvalidateBuffer)) != 0)
                        {
                            BackendContext.Resources.Buffers.Invalidate(BackendContext, _vkMemory!.Value, GetMappedMemoryOffset(0), _bufferSize);
                        }

                        UnmapCurrentBuffer();
                    }

                    _persistentMappedPtr = null;
                }

                while (Data.ActivelyMapping.Remove(this))
                {
                }

                GPUSideSource?.Dispose();
                GPUSideSource = null;
            }

            /// <summary>
            /// Hooks for descriptor set integration (uniform/storage buffer binding).
            /// </summary>
            public void SetUniformBlockName(XRRenderProgram program, string blockName)
            {
                if (program is null || string.IsNullOrWhiteSpace(blockName))
                    return;

                _lastBindingName = blockName;
                if (program.TryResolveShaderStorageBufferBinding(blockName, out uint binding) ||
                    program.TryResolveUniformBlockBinding(blockName, out binding))
                {
                    SetBlockIndex(binding);
                    _resolvedProgramBindings[program] = binding;
                    return;
                }

                Debug.VulkanWarningEvery(
                    $"VkDataBuffer.UnresolvedBlockName.{blockName}",
                    TimeSpan.FromSeconds(5),
                    "[VkDataBuffer] Could not resolve block '{0}' for buffer '{1}' in program '{2}'.",
                    blockName,
                    GetDescribingName(),
                    program.Name ?? "<unnamed>");
            }
            public void SetBlockIndex(uint blockIndex)
            {
                if (blockIndex == uint.MaxValue)
                    return;

                Data.BindingIndexOverride = blockIndex;
            }

            public void BindSSBO(XRRenderProgram program, uint? bindingIndexOverride = null)
            {
                if (program is null)
                    return;

                EnsureStorageAllocatedForGpuUse();

                uint binding = bindingIndexOverride
                    ?? Data.BindingIndexOverride
                    ?? (_resolvedProgramBindings.TryGetValue(program, out uint resolved) ? resolved : 0u);
                program.BindBuffer(Data, binding);
            }

            protected internal override void PreDeleted()
            {
                UnmapBufferData();
                GPUSideSource?.Dispose();
                GPUSideSource = null;
                _uploadedByteCount = 0ul;
                _hasPendingUpload = false;
            }

            public void Bind() { /* Vulkan: binding is handled via descriptor sets */ }
            public void Unbind() { /* Vulkan: unbinding is not required */ }

            public bool IsMapped => Data.ActivelyMapping.Contains(this);

            public override bool IsGenerated => _vkBuffer.HasValue && _vkBuffer.Value.Handle != 0;

            public VoidPtr? GetMappedAddress() => GPUSideSource?.Address;

            internal bool SupportsDescriptorType(DescriptorType descriptorType)
                => SupportsDescriptorType(descriptorType, _lastUsageFlags);

            internal static bool SupportsDescriptorType(DescriptorType descriptorType, BufferUsageFlags usageFlags)
                => descriptorType switch
                {
                    DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic
                        => (usageFlags & BufferUsageFlags.StorageBufferBit) != 0,
                    DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic
                        => (usageFlags & BufferUsageFlags.UniformBufferBit) != 0,
                    DescriptorType.UniformTexelBuffer
                        => (usageFlags & BufferUsageFlags.UniformTexelBufferBit) != 0,
                    DescriptorType.StorageTexelBuffer
                        => (usageFlags & BufferUsageFlags.StorageTexelBufferBit) != 0,
                    _ => true,
                };

            internal bool TryCaptureComputeBufferSnapshot(
                bool allowSynchronousUpload,
                out VulkanComputeBufferBinding snapshot)
            {
                snapshot = default;
                if (!TryEnsureReadyForRendering(allowSynchronousUpload))
                    return false;

                ulong requestedRange = Math.Max((ulong)Data.Length, 1UL);
                if (_bufferSize < requestedRange && allowSynchronousUpload)
                    PushData();

                if (_vkBuffer is not { } buffer ||
                    buffer.Handle == 0 ||
                    _bufferSize < requestedRange)
                {
                    return false;
                }

                snapshot = new VulkanComputeBufferBinding(Data, buffer, requestedRange, _lastUsageFlags);
                return true;
            }

            // --- Helper: Should use device-local + staging for static/immutable buffers ---
            private bool AllowsUpdatesWhileMapped()
                => (Data.StorageFlags & EBufferMapStorageFlags.Persistent) != 0 ||
                   (Data.RangeFlags & EBufferMapRangeFlags.Persistent) != 0;

            private bool HasBlockingActiveMapping()
                => Data.ActivelyMapping.Count > 0 && !AllowsUpdatesWhileMapped();

            private bool ShouldDisposeAfterUpload()
                => Data.DisposeOnPush &&
                   !_hasPendingUpload &&
                   _uploadedByteCount >= (ulong)Data.Length;

            private bool CanAllocateBufferVram(ulong requestedBytes)
            {
                long requested = requestedBytes > long.MaxValue ? long.MaxValue : (long)requestedBytes;
                if (RuntimeEngine.Rendering.Stats.Vram.CanAllocateVram(requested, _allocatedVRAMBytes, out long projectedBytes, out long budgetBytes))
                    return true;

                _hasPendingUpload = false;
                _lastUploadRoute = "SkippedVramBudget";
                Debug.VulkanWarningEvery(
                    $"VkDataBuffer.VramBudget.{GetDescribingName()}",
                    TimeSpan.FromSeconds(5),
                    "[VRAM Budget] Skipping Vulkan buffer allocation for '{0}' ({1} bytes). Projected={2} bytes, Budget={3} bytes.",
                    GetDescribingName(),
                    requested,
                    projectedBytes,
                    budgetBytes);
                return false;
            }

            private bool SkipUploadBecauseDeviceLost(string operation)
            {
                if (BackendContext.IsDeviceOperational)
                    return false;

                _hasPendingUpload = true;
                _lastUploadRoute = "SkippedDeviceLost";
                ReportBackendState();
                Debug.VulkanWarningEvery(
                    $"VkDataBuffer.DeviceLost.{operation}.{GetDescribingName()}",
                    TimeSpan.FromSeconds(2),
                    "[VkDataBuffer] {0} skipped for '{1}' because the Vulkan device is lost.",
                    operation,
                    GetDescribingName());
                return true;
            }

            private bool IsUsingDeviceLocalBacking()
                => (_lastMemProps & MemoryPropertyFlags.DeviceLocalBit) != 0;

            private static bool ShouldUseDeviceLocal(XRDataBuffer data, ulong byteCount)
                => !data.ShouldMap &&
                   !HasHostVisibleIntent(data) &&
                   byteCount >= DeviceLocalStaticUploadMinimumBytes &&
                   (data.Usage == EBufferUsage.StaticDraw || data.Usage == EBufferUsage.StaticCopy);

            private static bool HasHostVisibleIntent(XRDataBuffer data)
                => data.ShouldMap ||
                   (data.StorageFlags &
                    (EBufferMapStorageFlags.Read |
                     EBufferMapStorageFlags.Write |
                     EBufferMapStorageFlags.Persistent |
                     EBufferMapStorageFlags.Coherent |
                     EBufferMapStorageFlags.ClientStorage)) != 0 ||
                   (data.RangeFlags &
                    (EBufferMapRangeFlags.Read |
                     EBufferMapRangeFlags.Write |
                     EBufferMapRangeFlags.Persistent |
                     EBufferMapRangeFlags.Coherent |
                     EBufferMapRangeFlags.FlushExplicit)) != 0;

            private static MemoryPropertyFlags ResolveMemoryProperties(XRDataBuffer data, ulong byteCount)
            {
                if (ShouldUseDeviceLocal(data, byteCount))
                    return MemoryPropertyFlags.DeviceLocalBit;

                MemoryPropertyFlags flags = MemoryPropertyFlags.HostVisibleBit;

                bool wantsRead =
                    (data.StorageFlags & EBufferMapStorageFlags.Read) != 0 ||
                    (data.RangeFlags & EBufferMapRangeFlags.Read) != 0 ||
                    data.Usage is EBufferUsage.StaticRead or EBufferUsage.StreamRead or EBufferUsage.DynamicRead;
                if (wantsRead)
                    flags |= MemoryPropertyFlags.HostCachedBit;

                bool wantsCoherent =
                    (data.StorageFlags & EBufferMapStorageFlags.Coherent) != 0 ||
                    (data.RangeFlags & EBufferMapRangeFlags.Coherent) != 0 ||
                    (data.RangeFlags & EBufferMapRangeFlags.FlushExplicit) == 0;
                if (wantsCoherent)
                    flags |= MemoryPropertyFlags.HostCoherentBit;

                return flags;
            }

            private static string ResolveHostVisibleUploadRoute(MemoryPropertyFlags properties)
            {
                if ((properties & MemoryPropertyFlags.HostCachedBit) != 0)
                    return "HostVisibleCached";

                return (properties & MemoryPropertyFlags.HostCoherentBit) != 0
                    ? "HostVisibleCoherent"
                    : "HostVisibleExplicitFlush";
            }

            private static string ResolveHostVisibleSubDataUploadRoute(MemoryPropertyFlags properties)
            {
                if ((properties & MemoryPropertyFlags.HostCachedBit) != 0)
                    return "HostVisibleCachedSubData";

                return (properties & MemoryPropertyFlags.HostCoherentBit) != 0
                    ? "HostVisibleCoherentSubData"
                    : "HostVisibleExplicitFlushSubData";
            }

            internal void EnsureStorageAllocatedForGpuUse()
            {
                _requiresStorageBufferUsage = true;
                if (!RuntimeEngine.IsRenderThread)
                {
                    TraceDeferredRenderThreadUpload("EnsureStorageAllocatedForGpuUse");
                    return;
                }

                bool hasStorageUsage = (_lastUsageFlags & BufferUsageFlags.StorageBufferBit) != 0;
                if (_vkBuffer is null || _vkMemory is null || _bufferSize < (ulong)Data.Length || !hasStorageUsage)
                    PushData();
            }

            void IApiDataBuffer.EnsureStorageAllocatedForGpuUse()
                => EnsureStorageAllocatedForGpuUse();

            private bool CanFlushMappedMemory(out ulong length)
            {
                length = 0ul;
                if (_vkMemory is null || _bufferSize == 0)
                    return false;

                length = _bufferSize;
                return true;
            }

            private bool NormalizeMappedRange(int offset, uint length, out ulong memoryOffset, out ulong mappedLength)
            {
                memoryOffset = 0ul;
                mappedLength = 0ul;

                if (_vkMemory is null || _bufferSize == 0 || offset < 0 || length == 0)
                    return false;

                ulong bufferOffset = (uint)offset;
                if (bufferOffset >= _bufferSize)
                    return false;

                mappedLength = Math.Min((ulong)length, _bufferSize - bufferOffset);
                memoryOffset = GetMappedMemoryOffset(bufferOffset);
                return mappedLength > 0;
            }

            private void WarnUnsupportedMappingFlags()
            {
                if ((Data.StorageFlags & EBufferMapStorageFlags.ClientStorage) != 0)
                {
                    Debug.VulkanWarningEvery(
                        $"VkDataBuffer.ClientStorage.Noop.{GetDescribingName()}",
                        TimeSpan.FromSeconds(10),
                        "[VkDataBuffer] ClientStorage is a Vulkan no-op for '{0}'; memory placement is selected from map/read/write intent.",
                        GetDescribingName());
                }

                if ((Data.RangeFlags & EBufferMapRangeFlags.Unsynchronized) != 0)
                {
                    Debug.VulkanWarningEvery(
                        $"VkDataBuffer.Unsynchronized.Diagnostic.{GetDescribingName()}",
                        TimeSpan.FromSeconds(10),
                        "[VkDataBuffer] Unsynchronized mapping requested for '{0}'. Vulkan will not add implicit hazard avoidance; caller must guarantee no overlapping GPU use.",
                        GetDescribingName());
                }
            }

            private void RecordMappedReadbackBytes(ulong bytes)
            {
                bool readIntent = (Data.StorageFlags & EBufferMapStorageFlags.Read) != 0 ||
                                  (Data.RangeFlags & EBufferMapRangeFlags.Read) != 0;
                if (!readIntent || bytes == 0)
                    return;

                long count = bytes > long.MaxValue ? long.MaxValue : (long)bytes;
                RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(count);
                XRBufferWriteTelemetry.RecordHostCachedReadback(count);
            }

            private void RecordUploadDiagnostics(long byteCount, bool recreate, bool fullUpload)
            {
                if (byteCount > 0 && fullUpload)
                    RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.BufferUploadBytes, byteCount);

                if (!IsBufferUploadLoggingEnabled())
                    return;

                Debug.Vulkan(
                    "[VkBufferUpload] name='{0}' target={1} usage={2} bytes={3} allocated={4} uploaded={5} ready={6} route={7} recreate={8} resizable={9} storage={10} range={11} memProps={12} deviceAddressEnabled={13} deviceAddress=0x{14:X} deviceAddressStatus={15} compressed={16}.",
                    GetDescribingName(),
                    Data.Target,
                    Data.Usage,
                    byteCount,
                    _bufferSize,
                    _uploadedByteCount,
                    IsReadyForRendering,
                    _lastUploadRoute,
                    recreate,
                    Data.Resizable,
                    Data.StorageFlags,
                    Data.RangeFlags,
                    _lastMemProps,
                    _lastDeviceAddressEnabled,
                    DeviceAddress,
                    BackendContext.Resources.Buffers.ResolveDeviceAddressStatus(BackendContext, Data, DeviceAddress),
                    _lastUploadUsedCompressedGpuPath);
            }

            private void TracePushSubData(int offset, uint length, string stage)
            {
                if (!RenderDiagnosticsFlags.PushSubDataTrace && !RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging)
                    return;

                Debug.Vulkan(
                    "[VkBufferSubData] name='{0}' stage={1} offset={2} length={3} dataLength={4} allocated={5} uploaded={6} pending={7} immutable={8} generated={9} route={10}.",
                    GetDescribingName(),
                    stage,
                    offset,
                    length,
                    Data.Length,
                    _bufferSize,
                    _uploadedByteCount,
                    _hasPendingUpload,
                    _immutableStorageSet,
                    IsGenerated,
                    _lastUploadRoute);
            }

            private void TraceQueuedUpload(string reason)
            {
                if (!RenderDiagnosticsFlags.UploadStageLogging &&
                    !RenderDiagnosticsFlags.PushSubDataTrace)
                {
                    return;
                }

                Debug.Vulkan(
                    "[VkBufferUploadQueue] name='{0}' reason={1} full={2} sub={3} start={4} end={5} queued={6} ready={7} dataLength={8} uploaded={9} allocated={10}.",
                    GetDescribingName(),
                    reason,
                    _queuedUploadIsFull,
                    _queuedSubUpload,
                    _queuedSubUploadStart,
                    _queuedSubUploadEnd,
                    _queuedRenderThreadUpload,
                    IsReadyForRendering,
                    Data.Length,
                    _uploadedByteCount,
                    _bufferSize);
            }

            private bool CanUploadFromRenderReadinessCheck(bool allowSynchronousUpload)
                => allowSynchronousUpload && RuntimeEngine.IsRenderThread;

            private void TraceDeferredRenderThreadUpload(string stage)
            {
                if (!RenderDiagnosticsFlags.UploadStageLogging &&
                    !RenderDiagnosticsFlags.PushSubDataTrace &&
                    !RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging)
                {
                    return;
                }

                Debug.VulkanWarningEvery(
                    $"VkDataBuffer.DeferredRenderThreadUpload.{stage}.{GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[VkDataBuffer] deferred upload for '{0}' at {1}: readiness checks cannot enqueue render-thread uploads from thread {2}; generated={3} ready={4} length={5} uploaded={6} allocated={7}.",
                    GetDescribingName(),
                    stage,
                    Environment.CurrentManagedThreadId,
                    IsGenerated,
                    IsReadyForRendering,
                    Data.Length,
                    _uploadedByteCount,
                    _bufferSize);
            }

            private static bool IsBufferUploadLoggingEnabled()
                => RenderDiagnosticsFlags.UploadStageLogging ||
                   RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging;

            private bool TryGetUploadSlice(int offset, uint length, out VoidPtr sourceSlice)
            {
                sourceSlice = VoidPtr.Zero;
                if (offset < 0 || length == 0)
                    return false;

                if (!Data.TryGetAddress(out var baseAddress) || baseAddress.Pointer == null)
                {
                    Debug.VulkanWarningEvery(
                        $"VkDataBuffer.NoAddress.{GetDescribingName()}",
                        TimeSpan.FromSeconds(2),
                        "[VkDataBuffer] '{0}' upload skipped: CPU-side data source has no valid address (disposed?).",
                        GetDescribingName());
                    return false;
                }

                sourceSlice = baseAddress + offset;
                return true;
            }

            private BufferUsageFlags ResolveVkUsageFlags(EBufferTarget target, EBufferUsage usage)
            {
                BufferUsageFlags flags = ToVkUsageFlags(target) | ToVkUsageFlags(usage);
                if (target == EBufferTarget.TransformFeedbackBuffer && BackendContext.Supports(EVulkanDeviceCapability.TransformFeedback))
                {
                    flags |= BufferUsageFlags.TransformFeedbackBufferBitExt |
                        BufferUsageFlags.TransformFeedbackCounterBufferBitExt;
                }

                if (_requiresStorageBufferUsage || ShouldAddStorageUsageForComputeDeformationSource(target))
                    flags |= BufferUsageFlags.StorageBufferBit;

                if (flags == 0)
                    flags = BufferUsageFlags.StorageBufferBit;
                return flags;
            }

            private static bool ShouldAddStorageUsageForComputeDeformationSource(EBufferTarget target)
                => target == EBufferTarget.ArrayBuffer &&
                   (RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader ||
                    RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader);

            public static BufferUsageFlags ToVkUsageFlags(EBufferTarget target) => target switch
            {
                EBufferTarget.ArrayBuffer => BufferUsageFlags.VertexBufferBit,
                EBufferTarget.ElementArrayBuffer => BufferUsageFlags.IndexBufferBit,
                EBufferTarget.PixelPackBuffer => BufferUsageFlags.TransferDstBit,
                EBufferTarget.PixelUnpackBuffer => BufferUsageFlags.TransferSrcBit,
                EBufferTarget.UniformBuffer => BufferUsageFlags.UniformBufferBit,
                EBufferTarget.TextureBuffer => BufferUsageFlags.UniformTexelBufferBit | BufferUsageFlags.StorageTexelBufferBit,
                EBufferTarget.TransformFeedbackBuffer => BufferUsageFlags.StorageBufferBit,
                EBufferTarget.CopyReadBuffer => BufferUsageFlags.TransferSrcBit,
                EBufferTarget.CopyWriteBuffer => BufferUsageFlags.TransferDstBit,
                EBufferTarget.DrawIndirectBuffer => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
                EBufferTarget.ShaderStorageBuffer => BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
                EBufferTarget.DispatchIndirectBuffer => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
                EBufferTarget.QueryBuffer => BufferUsageFlags.TransferDstBit,
                EBufferTarget.AtomicCounterBuffer => BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
                EBufferTarget.ParameterBuffer => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
                _ => BufferUsageFlags.StorageBufferBit,
            };

            // --- Helper: Convert usage to Vulkan flags ---
            public static BufferUsageFlags ToVkUsageFlags(EBufferUsage usage) => usage switch
            {
                EBufferUsage.StaticDraw => BufferUsageFlags.TransferDstBit,
                EBufferUsage.StreamDraw or EBufferUsage.DynamicDraw => BufferUsageFlags.TransferDstBit,
                // Read usage describes a CPU readback destination: GPU transfer writes,
                // then the host reads after the submission fence completes.
                EBufferUsage.StreamRead or EBufferUsage.DynamicRead or EBufferUsage.StaticRead => BufferUsageFlags.TransferDstBit,
                EBufferUsage.StreamCopy or EBufferUsage.DynamicCopy => BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                EBufferUsage.StaticCopy => BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                _ => 0,
            };

            protected override uint CreateObjectInternal()
            {
                // Actual Vulkan buffer creation is deferred to PostGenerated/PushData,
                // but we must return a valid non-zero ID so that IsActive becomes true
                // and subsequent Generate() calls short-circuit correctly.
                return CacheObject(this);
            }

            protected override void DeleteObjectInternal()
            {
                // Track VRAM deallocation
                if (_allocatedVRAMBytes > 0)
                {
                    RuntimeEngine.Rendering.Stats.Vram.RemoveBufferAllocation(_allocatedVRAMBytes);
                    _allocatedVRAMBytes = 0;
                }

                // Retire buffer handles for deferred destruction ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â a command buffer
                // recorded this frame (or still in-flight on the GPU) may still
                // reference this buffer.
                ReleasePersistentMappingBeforeResourceRetire();
                if (_vkBuffer.HasValue && _vkMemory.HasValue)
                {
                    BackendContext.Resources.Buffers.Retire(_vkBuffer.Value, _vkMemory.Value, "VkDataBuffer.DeleteObjectInternal");
                }
                else
                {
                    // Partial state ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â destroy immediately (shouldn't happen normally).
                    if (_vkBuffer.HasValue)
                        BackendContext.Resources.Buffers.Retire(_vkBuffer.Value, default, "VkDataBuffer.DeleteObjectInternal.PartialState");
                    if (_vkMemory.HasValue)
                        BackendContext.Resources.Buffers.Retire(default, _vkMemory.Value, "VkDataBuffer.DeleteObjectInternal.PartialState");
                }

                _vkBuffer = null;
                _vkMemory = null;
                DeviceAddress = 0ul;
            }
        }
}
