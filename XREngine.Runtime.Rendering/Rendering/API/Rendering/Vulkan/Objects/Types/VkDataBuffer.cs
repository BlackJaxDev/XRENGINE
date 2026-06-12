using XREngine.Extensions;
using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private readonly VulkanStagingManager _stagingManager = new();
        private IVulkanMemoryAllocator? _memoryAllocator;

        /// <summary>The active memory allocator (legacy per-resource or block suballocator).</summary>
        internal IVulkanMemoryAllocator MemoryAllocator
            => _memoryAllocator ?? throw new InvalidOperationException("Memory allocator not initialized.");

        /// <summary>
        /// Tracks buffer allocations made through the allocator so that DestroyBufferRaw
        /// can free through the correct path (suballocator or legacy).
        /// Key: Buffer.Handle.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, VulkanMemoryAllocation> _bufferAllocations = new();

        /// <summary>
        /// Tracks buffers allocated outside the allocator path, currently device-address
        /// buffers that require <see cref="MemoryAllocateFlags.DeviceAddressBit"/>.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, VulkanMemoryAllocation> _legacyBufferAllocations = new();

        /// <summary>
        /// Tracks image allocations made through the allocator.
        /// Key: Image.Handle.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, VulkanMemoryAllocation> _imageAllocations = new();

        /// <summary>
        /// Returns the suballocation offset for a tracked buffer, or 0 if untracked (legacy).
        /// Use when mapping memory for a buffer that was allocated through the allocator.
        /// </summary>
        internal ulong GetBufferAllocationOffset(Buffer buffer)
        {
            if (_bufferAllocations.TryGetValue(buffer.Handle, out VulkanMemoryAllocation alloc))
                return alloc.Offset;

            return _legacyBufferAllocations.TryGetValue(buffer.Handle, out VulkanMemoryAllocation legacyAlloc)
                ? legacyAlloc.Offset
                : 0;
        }

        private bool TryGetBufferMemoryAllocation(Buffer buffer, out VulkanMemoryAllocation allocation)
        {
            if (_bufferAllocations.TryGetValue(buffer.Handle, out allocation))
                return true;

            return _legacyBufferAllocations.TryGetValue(buffer.Handle, out allocation);
        }

        internal bool TryMapBufferMemory(
            Buffer buffer,
            DeviceMemory memory,
            ulong bufferOffset,
            ulong length,
            out void* mappedPtr)
        {
            mappedPtr = null;
            ulong mappedLength = Math.Max(length, 1UL);

            if (TryGetBufferMemoryAllocation(buffer, out VulkanMemoryAllocation allocation))
                return MemoryAllocator.TryMap(Api!, device, allocation, bufferOffset, mappedLength, out mappedPtr);

            void* localPtr = null;
            Result result = Api!.MapMemory(device, memory, bufferOffset, mappedLength, 0, &localPtr);
            if (result != Result.Success)
                return false;

            mappedPtr = localPtr;
            return true;
        }

        internal void UnmapBufferMemory(Buffer buffer, DeviceMemory memory)
        {
            if (TryGetBufferMemoryAllocation(buffer, out VulkanMemoryAllocation allocation))
            {
                MemoryAllocator.Unmap(Api!, device, allocation);
                return;
            }

            Api!.UnmapMemory(device, memory);
        }

        internal void* MapBufferMemoryOrThrow(
            Buffer buffer,
            DeviceMemory memory,
            ulong bufferOffset,
            ulong length,
            string failureMessage)
        {
            if (!TryMapBufferMemory(buffer, memory, bufferOffset, length, out void* mappedPtr))
                throw new InvalidOperationException(failureMessage);

            return mappedPtr;
        }

        /// <summary>
        /// Returns the suballocation offset for a tracked image, or 0 if untracked (legacy).
        /// </summary>
        internal ulong GetImageAllocationOffset(Image image)
            => _imageAllocations.TryGetValue(image.Handle, out VulkanMemoryAllocation alloc) ? alloc.Offset : 0;

        /// <summary>
        /// Vulkan data buffer with best practices: staging, synchronization, descriptor integration, lifetime, mapping, error handling, and multi-frame support.
        /// </summary>
        public class VkDataBuffer(VulkanRenderer renderer, XRDataBuffer buffer) : VkObject<XRDataBuffer>(renderer, buffer), IApiDataBuffer
        {
            private const ulong IndirectCopyDeviceAddressThresholdBytes = 256UL * 1024UL;

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
            private readonly Dictionary<XRRenderProgram, uint> _resolvedProgramBindings = [];

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

                downgradeReason = Renderer.ResolveSceneDatabaseDeviceAddressStatus(Data, DeviceAddress);
                Data.ReportDeviceAddressDowngrade(downgradeReason);
                return false;
            }

            internal void EnsureReadyForRendering()
            {
                if (!IsActive)
                {
                    Generate();
                    return;
                }

                if (!IsReadyForRendering)
                    PushData();
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
                if (_lastMemProps.HasFlag(MemoryPropertyFlags.HostVisibleBit))
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

            protected internal override void PostGenerated()
            {
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
                if (RuntimeEngine.InvokeOnMainThread(PushData, "VkDataBuffer.PushData"))
                    return;

                // Determine usage and memory flags
                BufferUsageFlags usage = ResolveVkUsageFlags(Data.Target, Data.Usage);
                MemoryPropertyFlags memProps = ResolveMemoryProperties(Data);
                bool enableDeviceAddress = Renderer.ShouldEnableDeviceAddressForSceneDatabaseBuffer(Data);
                if (enableDeviceAddress)
                    usage |= BufferUsageFlags.ShaderDeviceAddressBit;

                bool needsRecreate =
                    _vkBuffer is null ||
                    _vkMemory is null ||
                    _bufferSize != Data.Length ||
                    _lastUsageFlags != usage ||
                    _lastMemProps != memProps ||
                    _lastDeviceAddressEnabled != enableDeviceAddress ||
                    (_immutableStorageSet && !Data.StorageFlags.HasFlag(EBufferMapStorageFlags.DynamicStorage));

                if (needsRecreate)
                {
                    ulong requestedAllocationBytes = Math.Max((ulong)Data.Length, 1UL);
                    if (!CanAllocateBufferVram(requestedAllocationBytes))
                        return;

                    _hasPendingUpload = true;
                    ReportBackendState();

                    // Retire old buffer handles for deferred cleanup — the command buffer
                    // currently being recorded may still reference them, so we must not
                    // destroy them synchronously.  Do NOT call Destroy() here because
                    // that also resets _bindingId, which would make IsActive return false
                    // and trigger redundant Generate() cycles on every draw call.
                    if (_vkBuffer.HasValue || _vkMemory.HasValue)
                    {
                        ReleasePersistentMappingBeforeResourceRetire();
                        if (_vkBuffer.HasValue && _vkMemory.HasValue)
                            Renderer.RetireBuffer(_vkBuffer.Value, _vkMemory.Value);
                        else
                        {
                            // Partial state — still retire to avoid use-after-free.
                            if (_vkBuffer.HasValue)
                                Renderer.RetireBuffer(_vkBuffer.Value, default);
                            if (_vkMemory.HasValue)
                                Api!.FreeMemory(Renderer.device, _vkMemory.Value, null);
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

                    _bufferSize = Data.Length;
                    bool uploadedContent = _bufferSize == 0;
                    _lastUsageFlags = usage;
                    _lastMemProps = memProps;
                    _lastDeviceAddressEnabled = enableDeviceAddress;
                    _lastUploadUsedCompressedGpuPath = false;

                    // --- Staging buffer pattern for device-local ---
                    if (ShouldUseDeviceLocal(Data))
                    {
                        bool canUseGpuDecompression = CanUseGpuDecompressionUpload();
                        bool preferIndirectCopy = ShouldUseDeviceAddressForIndirectCopy(_bufferSize);
                        bool createDeviceAddress = preferIndirectCopy || enableDeviceAddress || canUseGpuDecompression;
                        _lastUploadRoute = createDeviceAddress
                            ? "DeviceLocalStagingDeviceAddress"
                            : "DeviceLocalStaging";

                        // Create device-local buffer first.
                        BufferUsageFlags deviceUsage = usage | BufferUsageFlags.TransferDstBit;
                        if (createDeviceAddress)
                            deviceUsage |= BufferUsageFlags.ShaderDeviceAddressBit;

                        var (deviceBuffer, deviceMemory) = Renderer.CreateBuffer(
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
                        // If CPU-side data exists, upload through a transient staging buffer.
                        else if (_bufferSize > 0 && TryGetUploadSlice(0, (uint)_bufferSize, out VoidPtr sourceSlice))
                        {
                            BufferUsageFlags stagingUsage = BufferUsageFlags.TransferSrcBit;
                            if (preferIndirectCopy)
                                stagingUsage |= BufferUsageFlags.ShaderDeviceAddressBit;

                            MemoryPropertyFlags stagingProps = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                            var (stagingBuffer, stagingMemory) = Renderer.CreateBuffer(
                                _bufferSize,
                                stagingUsage,
                                stagingProps,
                                sourceSlice,
                                preferIndirectCopy);
                            try
                            {
                                Renderer.CopyBuffer(stagingBuffer, deviceBuffer, _bufferSize);
                            }
                            finally
                            {
                                Renderer.DestroyBuffer(stagingBuffer, stagingMemory);
                            }

                            uploadedContent = true;
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
                                Renderer.SupportsNvMemoryDecompression,
                                Renderer.SupportsBufferDeviceAddress);
                            _lastUploadRoute = "DeviceLocalCompressedFallbackMissingCpuData";
                        }
                    }
                    else
                    {
                        // Host-visible buffer for dynamic/stream
                        _lastUploadRoute = ResolveHostVisibleUploadRoute(memProps);
                        VoidPtr initialData = Data.TryGetAddress(out var address) ? address : VoidPtr.Zero;
                        (_vkBuffer, _vkMemory) = Renderer.CreateBuffer(
                            _bufferSize,
                            usage,
                            memProps,
                            initialData,
                            enableDeviceAddress);
                        uploadedContent = _bufferSize == 0 || initialData != VoidPtr.Zero;
                    }

                    RefreshDeviceAddress();
                    _uploadedByteCount = uploadedContent ? _bufferSize : 0ul;
                    _hasPendingUpload = false;
                    ReportBackendState();

                    // Track VRAM allocation only when the backing allocation is recreated.
                    _allocatedVRAMBytes = (long)_bufferSize;
                    RuntimeEngine.Rendering.Stats.Vram.AddBufferAllocation(_allocatedVRAMBytes);
                }
                else
                {
                    // Reuse the existing allocation and upload fresh data even when size/usage are unchanged.
                    PushSubData(0, Data.Length);
                    if (ShouldDisposeAfterUpload())
                        Data.Dispose();
                    return;
                }

                Renderer.TrackBufferBinding(Data);
                RecordUploadDiagnostics((long)_bufferSize, recreate: needsRecreate, fullUpload: true);
                ReportBackendState();

                if (ShouldDisposeAfterUpload())
                    Data.Dispose();
            }

            /// <summary>
            /// Pushes a subrange of data to the GPU. Uses staging if device-local.
            /// </summary>
            public void PushSubData(int offset, uint length)
            {
                if (SkipUploadBecauseDeviceLost("PushSubData"))
                    return;
                if (HasBlockingActiveMapping())
                    return;
                if (RuntimeEngine.InvokeOnMainThread(() => PushSubData(offset, length), "VkDataBuffer.PushSubData"))
                    return;
                if (offset < 0)
                {
                    TracePushSubData(offset, length, "negative-offset-ignored");
                    return;
                }
                if (length == 0)
                    return;

                uint totalLength = Data.Length;
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

                if (_immutableStorageSet && !Data.StorageFlags.HasFlag(EBufferMapStorageFlags.DynamicStorage))
                {
                    TracePushSubData(offset, clampedLength, "immutable-no-dynstore-full-upload");
                    PushData();
                    return;
                }

                // For device-local, use staging buffer for subdata
                if (ShouldUseDeviceLocal(Data))
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

                    bool preferIndirectCopy = ShouldUseDeviceAddressForIndirectCopy(clampedLength);
                    _lastUploadRoute = preferIndirectCopy
                        ? "DeviceLocalSubDataStagingDeviceAddress"
                        : "DeviceLocalSubDataStaging";

                    BufferUsageFlags stagingUsage = BufferUsageFlags.TransferSrcBit;
                    if (preferIndirectCopy)
                        stagingUsage |= BufferUsageFlags.ShaderDeviceAddressBit;

                    MemoryPropertyFlags stagingProps = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                    var (stagingBuffer, stagingMemory) = Renderer.CreateBuffer(
                        clampedLength,
                        stagingUsage,
                        stagingProps,
                        sourceSlice,
                        preferIndirectCopy);
                    try
                    {
                        Renderer.CopyBuffer(stagingBuffer, _vkBuffer, clampedLength, (ulong)offset);
                    }
                    finally
                    {
                        Renderer.DestroyBuffer(stagingBuffer, stagingMemory);
                    }
                }
                else
                {
                    if (!TryGetUploadSlice(offset, clampedLength, out VoidPtr sourceSlice))
                        return;

                    // Host-visible: map, copy, unmap
                    _lastUploadRoute = ResolveHostVisibleUploadRoute(_lastMemProps) + "SubData";
                    Renderer.UpdateBuffer(_vkBuffer, _vkMemory, (ulong)offset, (ulong)clampedLength, sourceSlice.Pointer);
                }

                ulong uploadedEnd = (ulong)offset + clampedLength;
                if (uploadedEnd > _uploadedByteCount)
                    _uploadedByteCount = uploadedEnd;
                _hasPendingUpload = false;
                Renderer.TrackBufferBinding(Data);
                RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.BufferUploadBytes, clampedLength);
                TracePushSubData(offset, clampedLength, "done");
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

                var (compressedBuffer, compressedMemory) = Renderer.CreateBuffer(
                    compressedLength,
                    stagingUsage,
                    stagingProps,
                    compressedSource.Address,
                    enableDeviceAddress: true);

                try
                {
                    return Renderer.TryDecompressBufferGDeflateNv(
                        compressedBuffer,
                        srcOffset: 0,
                        compressedSize: compressedLength,
                        dstBuffer: deviceBuffer,
                        dstOffset: 0,
                        decompressedSize: decodedLength);
                }
                finally
                {
                    Renderer.DestroyBuffer(compressedBuffer, compressedMemory);
                }
            }

            private bool CanUseGpuDecompressionUpload()
                => Data.HasGpuCompressedPayload &&
                   Data.GpuCompressionCodec == XRDataBuffer.EBufferCompressionCodec.GDeflate &&
                   Renderer.SupportsNvMemoryDecompression &&
                   Renderer.SupportsBufferDeviceAddress;

            private bool ShouldUseDeviceAddressForIndirectCopy(ulong byteCount)
                => byteCount >= IndirectCopyDeviceAddressThresholdBytes &&
                   Renderer.CanUseNvIndirectBufferCopyUploads;
            public void PushSubData() => PushSubData(0, Data.Length);

            private void RefreshDeviceAddress()
            {
                DeviceAddress = 0ul;
                if (!_lastDeviceAddressEnabled || !Renderer.SupportsBufferDeviceAddress || _vkBuffer is not { } buffer)
                    return;

                DeviceAddress = Renderer.GetBufferDeviceAddress(buffer);
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
                    Renderer.FlushBuffer(_vkMemory, GetMappedMemoryOffset(0), length);
            }
            public void FlushRange(int offset, uint length)
            {
                if (RuntimeEngine.InvokeOnMainThread(() => FlushRange(offset, length), "VkDataBuffer.FlushRange"))
                    return;
                if (!NormalizeMappedRange(offset, length, out ulong memoryOffset, out ulong mappedLength))
                    return;
                if ((_lastMemProps & MemoryPropertyFlags.HostCoherentBit) == 0)
                    Renderer.FlushBuffer(_vkMemory, memoryOffset, mappedLength);
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
                if (Renderer.IsDeviceLost)
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
                if (Renderer.IsDeviceLost)
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
                    _persistentMappedPtr = Renderer.MapBuffer(_vkBuffer, _vkMemory, 0, Math.Max(_bufferSize, 1UL));
                if (_persistentMappedPtr == null)
                    return;
                GPUSideSource = new DataSource(_persistentMappedPtr, (uint)_bufferSize);
                RecordMappedReadbackBytes(_bufferSize);
                if (!Data.ActivelyMapping.Contains(this))
                    Data.ActivelyMapping.Add(this);
            }
            public void MapToClientSide(int offset, uint length)
            {
                if (Renderer.IsDeviceLost)
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
                    _persistentMappedPtr = Renderer.MapBuffer(_vkBuffer, _vkMemory, (ulong)offset, mappedLength);
                if (_persistentMappedPtr == null)
                    return;
                GPUSideSource = new DataSource(_persistentMappedPtr, (uint)mappedLength);
                RecordMappedReadbackBytes(mappedLength);
                if (!Data.ActivelyMapping.Contains(this))
                    Data.ActivelyMapping.Add(this);
            }

            private ulong GetMappedMemoryOffset(ulong bufferOffset)
                => _vkBuffer.HasValue
                    ? Renderer.GetBufferAllocationOffset(_vkBuffer.Value) + bufferOffset
                    : bufferOffset;

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
                    if (Data.RangeFlags.HasFlag(EBufferMapRangeFlags.Read) ||
                        Data.RangeFlags.HasFlag(EBufferMapRangeFlags.InvalidateRange) ||
                        Data.RangeFlags.HasFlag(EBufferMapRangeFlags.InvalidateBuffer))
                    {
                        Renderer.InvalidateBuffer(_vkMemory, GetMappedMemoryOffset(0), _bufferSize);
                    }

                    Renderer.UnmapBuffer(_vkBuffer, _vkMemory);
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
                        if (Data.RangeFlags.HasFlag(EBufferMapRangeFlags.Read) ||
                            Data.RangeFlags.HasFlag(EBufferMapRangeFlags.InvalidateRange) ||
                            Data.RangeFlags.HasFlag(EBufferMapRangeFlags.InvalidateBuffer))
                        {
                            Renderer.InvalidateBuffer(_vkMemory, GetMappedMemoryOffset(0), _bufferSize);
                        }

                        Renderer.UnmapBuffer(_vkBuffer, _vkMemory);
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
                => descriptorType switch
                {
                    DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic
                        => _lastUsageFlags.HasFlag(BufferUsageFlags.StorageBufferBit),
                    DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic
                        => _lastUsageFlags.HasFlag(BufferUsageFlags.UniformBufferBit),
                    DescriptorType.UniformTexelBuffer
                        => _lastUsageFlags.HasFlag(BufferUsageFlags.UniformTexelBufferBit),
                    DescriptorType.StorageTexelBuffer
                        => _lastUsageFlags.HasFlag(BufferUsageFlags.StorageTexelBufferBit),
                    _ => true,
                };

            // --- Helper: Should use device-local + staging for static/immutable buffers ---
            private bool AllowsUpdatesWhileMapped()
                => Data.StorageFlags.HasFlag(EBufferMapStorageFlags.Persistent) ||
                   Data.RangeFlags.HasFlag(EBufferMapRangeFlags.Persistent);

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
                if (!Renderer.IsDeviceLost)
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

            private static bool ShouldUseDeviceLocal(XRDataBuffer data)
                => !data.ShouldMap &&
                   !HasHostVisibleIntent(data) &&
                   (data.Usage == EBufferUsage.StaticDraw || data.Usage == EBufferUsage.StaticCopy);

            private static bool HasHostVisibleIntent(XRDataBuffer data)
                => data.ShouldMap ||
                   data.StorageFlags.HasFlag(EBufferMapStorageFlags.Read) ||
                   data.StorageFlags.HasFlag(EBufferMapStorageFlags.Write) ||
                   data.StorageFlags.HasFlag(EBufferMapStorageFlags.Persistent) ||
                   data.StorageFlags.HasFlag(EBufferMapStorageFlags.Coherent) ||
                   data.StorageFlags.HasFlag(EBufferMapStorageFlags.ClientStorage) ||
                   data.RangeFlags.HasFlag(EBufferMapRangeFlags.Read) ||
                   data.RangeFlags.HasFlag(EBufferMapRangeFlags.Write) ||
                   data.RangeFlags.HasFlag(EBufferMapRangeFlags.Persistent) ||
                   data.RangeFlags.HasFlag(EBufferMapRangeFlags.Coherent) ||
                   data.RangeFlags.HasFlag(EBufferMapRangeFlags.FlushExplicit);

            private static MemoryPropertyFlags ResolveMemoryProperties(XRDataBuffer data)
            {
                if (ShouldUseDeviceLocal(data))
                    return MemoryPropertyFlags.DeviceLocalBit;

                MemoryPropertyFlags flags = MemoryPropertyFlags.HostVisibleBit;

                bool wantsRead =
                    data.StorageFlags.HasFlag(EBufferMapStorageFlags.Read) ||
                    data.RangeFlags.HasFlag(EBufferMapRangeFlags.Read) ||
                    data.Usage is EBufferUsage.StaticRead or EBufferUsage.StreamRead or EBufferUsage.DynamicRead;
                if (wantsRead)
                    flags |= MemoryPropertyFlags.HostCachedBit;

                bool wantsCoherent =
                    data.StorageFlags.HasFlag(EBufferMapStorageFlags.Coherent) ||
                    data.RangeFlags.HasFlag(EBufferMapRangeFlags.Coherent) ||
                    !data.RangeFlags.HasFlag(EBufferMapRangeFlags.FlushExplicit);
                if (wantsCoherent)
                    flags |= MemoryPropertyFlags.HostCoherentBit;

                return flags;
            }

            private static string ResolveHostVisibleUploadRoute(MemoryPropertyFlags properties)
            {
                if (properties.HasFlag(MemoryPropertyFlags.HostCachedBit))
                    return "HostVisibleCached";

                return properties.HasFlag(MemoryPropertyFlags.HostCoherentBit)
                    ? "HostVisibleCoherent"
                    : "HostVisibleExplicitFlush";
            }

            private void EnsureStorageAllocatedForGpuUse()
            {
                if (_vkBuffer is null || _vkMemory is null || _bufferSize < (ulong)Data.Length)
                    PushData();
            }

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
                if (Data.StorageFlags.HasFlag(EBufferMapStorageFlags.ClientStorage))
                {
                    Debug.VulkanWarningEvery(
                        $"VkDataBuffer.ClientStorage.Noop.{GetDescribingName()}",
                        TimeSpan.FromSeconds(10),
                        "[VkDataBuffer] ClientStorage is a Vulkan no-op for '{0}'; memory placement is selected from map/read/write intent.",
                        GetDescribingName());
                }

                if (Data.RangeFlags.HasFlag(EBufferMapRangeFlags.Unsynchronized))
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
                bool readIntent = Data.StorageFlags.HasFlag(EBufferMapStorageFlags.Read) ||
                                  Data.RangeFlags.HasFlag(EBufferMapRangeFlags.Read);
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
                    Renderer.ResolveSceneDatabaseDeviceAddressStatus(Data, DeviceAddress),
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
                if (target == EBufferTarget.TransformFeedbackBuffer && Renderer.SupportsTransformFeedback)
                {
                    flags |= BufferUsageFlags.TransformFeedbackBufferBitExt |
                        BufferUsageFlags.TransformFeedbackCounterBufferBitExt;
                }

                if (flags == 0)
                    flags = BufferUsageFlags.StorageBufferBit;
                return flags;
            }

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
                EBufferTarget.DrawIndirectBuffer => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit,
                EBufferTarget.ShaderStorageBuffer => BufferUsageFlags.StorageBufferBit,
                EBufferTarget.DispatchIndirectBuffer => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit,
                EBufferTarget.QueryBuffer => BufferUsageFlags.TransferDstBit,
                EBufferTarget.AtomicCounterBuffer => BufferUsageFlags.StorageBufferBit,
                EBufferTarget.ParameterBuffer => BufferUsageFlags.UniformBufferBit,
                _ => BufferUsageFlags.StorageBufferBit,
            };

            // --- Helper: Convert usage to Vulkan flags ---
            public static BufferUsageFlags ToVkUsageFlags(EBufferUsage usage) => usage switch
            {
                EBufferUsage.StaticDraw => BufferUsageFlags.TransferDstBit,
                EBufferUsage.StreamDraw or EBufferUsage.DynamicDraw => BufferUsageFlags.TransferDstBit,
                EBufferUsage.StreamRead or EBufferUsage.DynamicRead => BufferUsageFlags.TransferSrcBit,
                EBufferUsage.StreamCopy or EBufferUsage.DynamicCopy => BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                EBufferUsage.StaticRead => BufferUsageFlags.TransferSrcBit,
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

                // Retire buffer handles for deferred destruction — a command buffer
                // recorded this frame (or still in-flight on the GPU) may still
                // reference this buffer.
                ReleasePersistentMappingBeforeResourceRetire();
                if (_vkBuffer.HasValue && _vkMemory.HasValue)
                {
                    Renderer.RetireBuffer(_vkBuffer.Value, _vkMemory.Value);
                }
                else
                {
                    // Partial state — destroy immediately (shouldn't happen normally).
                    if (_vkBuffer.HasValue)
                        Api!.DestroyBuffer(Renderer.device, _vkBuffer.Value, null);
                    if (_vkMemory.HasValue)
                        Api!.FreeMemory(Renderer.device, _vkMemory.Value, null);
                }

                _vkBuffer = null;
                _vkMemory = null;
                DeviceAddress = 0ul;
            }
        }

        private void* MapBuffer(Buffer? vkBuffer, DeviceMemory? vkMemory, ulong offset, ulong length)
        {
            if (vkBuffer is null)
                throw new ArgumentNullException(nameof(vkBuffer), "Cannot map null Vulkan buffer.");
            if (vkMemory is null)
                throw new ArgumentNullException(nameof(vkMemory), "Cannot map null Vulkan memory.");

            return MapBufferMemoryOrThrow(vkBuffer.Value, vkMemory.Value, offset, length, "Failed to map Vulkan buffer memory.");
        }

        private void CopyBuffer(Buffer? stagingBuffer, Buffer? vkBuffer, uint length, ulong offset)
        {
            if (_deviceLost)
                return;

            if (stagingBuffer is null || vkBuffer is null)
                throw new ArgumentNullException("Buffers cannot be null for copy operation.");

            if (TryCopyBufferViaIndirectNv(stagingBuffer.Value, vkBuffer.Value, length, 0, offset))
                return;

            ExecuteTransferBufferUpload(stagingBuffer.Value, vkBuffer.Value, length, 0, offset);
        }

        private void UpdateBuffer(Buffer? vkBuffer, DeviceMemory? vkMemory, ulong offset, ulong length, void* addr)
        {
            if (_deviceLost)
                return;

            if (vkBuffer is null || vkMemory is null || addr is null)
                throw new ArgumentNullException("Buffer, memory, or address cannot be null for update operation.");

            void* mappedPtr;
            if (!TryMapBufferMemory(vkBuffer.Value, vkMemory.Value, offset, length, out mappedPtr))
                throw new Exception("Failed to map Vulkan buffer memory.");

            Unsafe.CopyBlock(mappedPtr, addr, (uint)length);
            FlushBuffer(vkMemory.Value, GetBufferAllocationOffset(vkBuffer.Value) + offset, length);
            UnmapBufferMemory(vkBuffer.Value, vkMemory.Value); // Unmap after copying
        }

        private void UnmapBuffer(Buffer? vkBuffer, DeviceMemory? vkMemory)
        {
            if (vkBuffer is null)
                throw new ArgumentNullException(nameof(vkBuffer), "Cannot unmap null Vulkan buffer.");
            if (vkMemory is null)
                throw new ArgumentNullException(nameof(vkMemory), "Cannot unmap null Vulkan memory.");

            UnmapBufferMemory(vkBuffer.Value, vkMemory.Value);
        }

        public void CopyBuffer(
            Buffer? stagingBuffer,
            Buffer? deviceBuffer,
            ulong bufferSize)
        {
            if (_deviceLost)
                return;

            if (stagingBuffer is null || deviceBuffer is null)
                throw new ArgumentNullException("Buffers cannot be null for copy operation.");

            if (TryCopyBufferViaIndirectNv(stagingBuffer.Value, deviceBuffer.Value, bufferSize, 0, 0))
                return;

            ExecuteTransferBufferUpload(stagingBuffer.Value, deviceBuffer.Value, bufferSize, 0, 0);
        }

        private void ExecuteTransferBufferUpload(
            Buffer stagingBuffer,
            Buffer deviceBuffer,
            ulong copySize,
            ulong sourceOffset,
            ulong destinationOffset)
        {
            if (_deviceLost)
                return;

            QueueFamilyIndices queueFamilies = FamilyQueueIndices;
            uint graphicsFamily = queueFamilies.GraphicsFamilyIndex ?? 0u;
            uint transferFamily = queueFamilies.TransferFamilyIndex ?? graphicsFamily;
            bool dedicatedTransferFamily = transferFamily != graphicsFamily;
            RecordTransferQueuePolicyDiagnostics(
                stagingBuffer,
                deviceBuffer,
                copySize,
                graphicsFamily,
                transferFamily,
                dedicatedTransferFamily);

            if (dedicatedTransferFamily)
                Api!.QueueWaitIdle(graphicsQueue);

            using (var transferScope = NewTransferCommandScope())
            {
                if (dedicatedTransferFamily)
                {
                    BufferMemoryBarrier acquireBarrier = new()
                    {
                        SType = StructureType.BufferMemoryBarrier,
                        SrcAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                        DstAccessMask = AccessFlags.TransferWriteBit,
                        SrcQueueFamilyIndex = graphicsFamily,
                        DstQueueFamilyIndex = transferFamily,
                        Buffer = deviceBuffer,
                        Offset = destinationOffset,
                        Size = copySize
                    };

                    CmdPipelineBarrierTracked(
                        transferScope.CommandBuffer,
                        PipelineStageFlags.BottomOfPipeBit,
                        PipelineStageFlags.TransferBit,
                        DependencyFlags.None,
                        0,
                        null,
                        1,
                        &acquireBarrier,
                        0,
                        null);
                }

                BufferCopy copyRegion = new()
                {
                    SrcOffset = sourceOffset,
                    DstOffset = destinationOffset,
                    Size = copySize
                };

                Api!.CmdCopyBuffer(transferScope.CommandBuffer, stagingBuffer, deviceBuffer, 1, &copyRegion);

                if (dedicatedTransferFamily)
                {
                    BufferMemoryBarrier releaseBarrier = new()
                    {
                        SType = StructureType.BufferMemoryBarrier,
                        SrcAccessMask = AccessFlags.TransferWriteBit,
                        DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                        SrcQueueFamilyIndex = transferFamily,
                        DstQueueFamilyIndex = graphicsFamily,
                        Buffer = deviceBuffer,
                        Offset = destinationOffset,
                        Size = copySize
                    };

                    CmdPipelineBarrierTracked(
                        transferScope.CommandBuffer,
                        PipelineStageFlags.TransferBit,
                        PipelineStageFlags.BottomOfPipeBit,
                        DependencyFlags.None,
                        0,
                        null,
                        1,
                        &releaseBarrier,
                        0,
                        null);
                }
            }

            if (dedicatedTransferFamily)
            {
                using var graphicsScope = NewCommandScope();
                BufferMemoryBarrier acquireOnGraphics = new()
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                    SrcQueueFamilyIndex = transferFamily,
                    DstQueueFamilyIndex = graphicsFamily,
                    Buffer = deviceBuffer,
                    Offset = destinationOffset,
                    Size = copySize
                };

                CmdPipelineBarrierTracked(
                    graphicsScope.CommandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.VertexInputBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                    DependencyFlags.None,
                    0,
                    null,
                    1,
                    &acquireOnGraphics,
                    0,
                    null);
            }
        }

        public void DestroyBuffer(Buffer? vkBuffer, DeviceMemory? vkMemory)
        {
            if (vkBuffer.HasValue && vkMemory.HasValue && _stagingManager.TryRelease(vkBuffer.Value, vkMemory.Value))
                return;

            DestroyBufferRaw(vkBuffer, vkMemory);
        }

        internal MemoryPropertyFlags GetReadbackMemoryProperties()
            => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit;

        internal (Buffer stagingBuffer, DeviceMemory stagingMemory) CreateReadbackBuffer(ulong bufferSize)
            => CreateBuffer(
                bufferSize,
                BufferUsageFlags.TransferDstBit,
                GetReadbackMemoryProperties(),
                null);

        private void ThrowIfDeviceLostForResourceCreation(string operation)
        {
            if (!_deviceLost)
                return;

            Debug.VulkanWarningEvery(
                $"Vulkan.DeviceLost.ResourceCreation.{operation}",
                TimeSpan.FromSeconds(2),
                "[Vulkan] {0} skipped because the Vulkan device is lost.",
                operation);

            throw new InvalidOperationException($"Cannot {operation} after the Vulkan device was lost.");
        }

        public (Buffer stagingBuffer, DeviceMemory stagingMemory) CreateBuffer(
            ulong bufferSize,
            BufferUsageFlags stagingUsage,
            MemoryPropertyFlags stagingProps,
            VoidPtr dataPtr,
            bool enableDeviceAddress = false)
        {
            ThrowIfDeviceLostForResourceCreation("CreateBuffer");

            ulong requestedSize = bufferSize;
            ulong allocationSize = Math.Max(requestedSize, 1UL);

            if (_stagingManager.CanPool(stagingUsage, stagingProps))
                return _stagingManager.Acquire(this, allocationSize, stagingUsage, stagingProps, dataPtr);

            (Buffer stagingBuffer, DeviceMemory stagingMemory) = CreateBufferRaw(allocationSize, stagingUsage, stagingProps, enableDeviceAddress);

            // Map the buffer if needed.
            if (dataPtr != null && requestedSize > 0)
            {
                void* mappedPtr = null;
                if (!TryMapBufferMemory(stagingBuffer, stagingMemory, 0, requestedSize, out mappedPtr))
                    throw new Exception("Failed to map Vulkan memory.");
                Unsafe.CopyBlock(mappedPtr, dataPtr.Pointer, (uint)requestedSize);
                FlushBuffer(stagingMemory, GetBufferAllocationOffset(stagingBuffer), requestedSize);
                UnmapBufferMemory(stagingBuffer, stagingMemory);
            }

            return (stagingBuffer, stagingMemory);
        }

        internal (Buffer buffer, DeviceMemory memory) CreateBufferRaw(
            ulong bufferSize,
            BufferUsageFlags usage,
            MemoryPropertyFlags properties,
            bool enableDeviceAddress = false)
        {
            ThrowIfDeviceLostForResourceCreation("CreateBufferRaw");

            bufferSize = Math.Max(bufferSize, 1UL);

            if (enableDeviceAddress)
                usage |= BufferUsageFlags.ShaderDeviceAddressBit;

            BufferCreateInfo bufferInfo = new()
            {
                SType = StructureType.BufferCreateInfo,
                Size = bufferSize,
                Usage = usage,
                SharingMode = SharingMode.Exclusive
            };

            if (Api!.CreateBuffer(device, ref bufferInfo, null, out Buffer buffer) != Result.Success)
                throw new Exception("Failed to create Vulkan buffer.");

            // VMA knows how to allocate buffer-device-address resources when the
            // allocator was created with VMA_ALLOCATOR_CREATE_BUFFER_DEVICE_ADDRESS_BIT.
            if (enableDeviceAddress && MemoryAllocator is not VulkanVmaAllocator)
                return CreateBufferRawLegacy(buffer, usage, properties, bufferSize);

            // Route through the selected allocator backend.
            VulkanMemoryAllocation allocation = AllocateBufferMemoryWithFallback(buffer, properties);
            _bufferAllocations[buffer.Handle] = allocation;

            RecordAllocationTelemetry(properties, (long)allocation.Size);
            RecordBufferAllocationDiagnostics(buffer, usage, properties, allocation, bufferSize, enableDeviceAddress, "Allocator");

            Result bindResult = Api.BindBufferMemory(device, buffer, allocation.Memory, allocation.Offset);
            if (bindResult != Result.Success)
            {
                _bufferAllocations.TryRemove(buffer.Handle, out _);
                FreeMemoryAllocation(allocation);
                Api.DestroyBuffer(device, buffer, null);
                throw new Exception($"Failed to bind Vulkan buffer memory ({bindResult}).");
            }

            return (buffer, allocation.Memory);
        }

        /// <summary>Legacy path for non-VMA device-address buffers that need special allocation flags.</summary>
        private (Buffer buffer, DeviceMemory memory) CreateBufferRawLegacy(
            Buffer buffer,
            BufferUsageFlags usage,
            MemoryPropertyFlags properties,
            ulong bufferSize)
        {
            MemoryRequirements memoryRequirements = Api!.GetBufferMemoryRequirements(device, buffer);
            MemoryAllocateInfo memoryInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = ResolveMemoryType(memoryRequirements.MemoryTypeBits, properties)
            };

            MemoryAllocateFlagsInfo memoryAllocateFlagsInfo = new()
            {
                SType = StructureType.MemoryAllocateFlagsInfo,
                PNext = null,
                Flags = MemoryAllocateFlags.DeviceAddressBit,
                DeviceMask = 0,
            };
            memoryInfo.PNext = &memoryAllocateFlagsInfo;

            if (Api.AllocateMemory(device, ref memoryInfo, null, out DeviceMemory memory) != Result.Success)
            {
                Api.DestroyBuffer(device, buffer, null);
                throw new Exception("Failed to allocate Vulkan buffer memory (device-address).");
            }

            RecordAllocationTelemetry(properties, (long)memoryInfo.AllocationSize);

            VulkanMemoryAllocation allocation = new(
                memory,
                0,
                memoryInfo.AllocationSize,
                memoryInfo.MemoryTypeIndex,
                properties,
                -1);
            _legacyBufferAllocations[buffer.Handle] = allocation;
            RecordBufferAllocationDiagnostics(buffer, usage, properties, allocation, bufferSize, enableDeviceAddress: true, "LegacyDeviceAddress");

            Result bindResult = Api.BindBufferMemory(device, buffer, memory, 0);
            if (bindResult != Result.Success)
            {
                _legacyBufferAllocations.TryRemove(buffer.Handle, out _);
                Api.FreeMemory(device, memory, null);
                Api.DestroyBuffer(device, buffer, null);
                throw new Exception($"Failed to bind Vulkan buffer memory ({bindResult}).");
            }

            return (buffer, memory);
        }

        internal unsafe void UploadBufferMemory(Buffer buffer, DeviceMemory memory, ulong size, void* source)
        {
            if (_deviceLost)
                return;

            if (source == null || size == 0)
                return;

            void* mappedPtr = null;
            if (!TryMapBufferMemory(buffer, memory, 0, size, out mappedPtr))
                throw new Exception("Failed to map Vulkan memory for staging upload.");

            try
            {
                Unsafe.CopyBlock(mappedPtr, source, (uint)size);
                FlushBuffer(memory, GetBufferAllocationOffset(buffer), size);
            }
            finally
            {
                UnmapBufferMemory(buffer, memory);
            }
        }

        /// <summary>
        /// Creates a staging buffer and fills it directly from a file via DirectStorage,
        /// reading file data straight into mapped Vulkan host-visible memory.
        /// <para>
        /// This is the Vulkan equivalent of DirectStorage's D3D12 <c>DestinationBuffer</c>:
        /// file data goes NVMe → mapped staging buffer → <c>CmdCopyBuffer</c> → device-local.
        /// There is no intermediate managed <c>byte[]</c> allocation.
        /// </para>
        /// Use this for pre-cooked binary data (raw vertex/index buffers, DDS textures, etc.)
        /// that does not need CPU-side decoding.
        /// </summary>
        /// <param name="filePath">Source file path.</param>
        /// <param name="offset">Byte offset in the file.</param>
        /// <param name="length">Number of bytes to read.</param>
        /// <param name="stagingBuffer">The created staging buffer (TransferSrc, HostVisible).</param>
        /// <param name="stagingMemory">The staging buffer's device memory.</param>
        /// <returns><c>true</c> if successful.</returns>
        public bool TryCreateStagingBufferFromFile(
            string filePath, long offset, int length,
            out Buffer stagingBuffer, out DeviceMemory stagingMemory)
        {
            stagingBuffer = default;
            stagingMemory = default;

            if (_deviceLost)
                return false;

            if (string.IsNullOrWhiteSpace(filePath) || length <= 0)
                return false;

            (stagingBuffer, stagingMemory) = CreateBufferRaw(
                (ulong)length,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* mappedPtr = null;
            if (!TryMapBufferMemory(stagingBuffer, stagingMemory, 0, (ulong)length, out mappedPtr))
            {
                DestroyBufferRaw(stagingBuffer, stagingMemory);
                stagingBuffer = default;
                stagingMemory = default;
                return false;
            }

            try
            {
                RuntimeDirectStorageIO.TryReadInto(filePath, offset, length, mappedPtr);
            }
            catch
            {
                UnmapBufferMemory(stagingBuffer, stagingMemory);
                DestroyBufferRaw(stagingBuffer, stagingMemory);
                stagingBuffer = default;
                stagingMemory = default;
                return false;
            }

            UnmapBufferMemory(stagingBuffer, stagingMemory);
            return true;
        }

        internal void DestroyBufferRaw(Buffer? buffer, DeviceMemory? memory)
        {
            if (buffer.HasValue && buffer.Value.Handle != 0)
            {
                // If this buffer was tracked through the allocator, free through it.
                if (_bufferAllocations.TryRemove(buffer.Value.Handle, out VulkanMemoryAllocation allocation))
                {
                    Api!.DestroyBuffer(device, buffer.Value, null);
                    FreeMemoryAllocation(allocation);
                    return;
                }

                if (_legacyBufferAllocations.TryRemove(buffer.Value.Handle, out VulkanMemoryAllocation legacyAllocation) &&
                    (!memory.HasValue || memory.Value.Handle == 0))
                {
                    memory = legacyAllocation.Memory;
                }

                Api!.DestroyBuffer(device, buffer.Value, null);
            }

            // Untracked memory (device-address, legacy, or staging pool) — free directly.
            if (memory.HasValue && memory.Value.Handle != 0)
                Api!.FreeMemory(device, memory.Value, null);
        }

        public void FlushBuffer(
            DeviceMemory? vkMemory,
            ulong offset,
            ulong length)
        {
            if (vkMemory is null)
                throw new ArgumentNullException(nameof(vkMemory), "Cannot flush null Vulkan memory.");

            if (length == 0)
                return;

            if (TryGetTrackedMemoryAllocation(vkMemory.Value, offset, out VulkanMemoryAllocation allocation) &&
                allocation.IsCoherent)
            {
                return;
            }

            NormalizeMappedMemoryRange(vkMemory.Value, offset, length, out ulong flushOffset, out ulong flushSize);

            var v = new MappedMemoryRange
            {
                SType = StructureType.MappedMemoryRange,
                Memory = vkMemory.Value,
                Offset = flushOffset,
                Size = flushSize
            };

            if (Api!.FlushMappedMemoryRanges(device, 1, ref v) != Result.Success)
                throw new Exception("Failed to flush Vulkan buffer memory.");
        }

        private static ulong AlignUp(ulong value, ulong alignment)
            => alignment <= 1
                ? value
                : ((value + alignment - 1UL) / alignment) * alignment;

        private bool TryGetTrackedMemoryAllocation(DeviceMemory memory, ulong offset, out VulkanMemoryAllocation allocation)
        {
            foreach (VulkanMemoryAllocation candidate in _bufferAllocations.Values)
            {
                if (candidate.Memory.Handle != memory.Handle)
                    continue;

                ulong allocationEnd = candidate.Offset + candidate.Size;
                if (candidate.BlockId == -1 || (offset >= candidate.Offset && offset < allocationEnd))
                {
                    allocation = candidate;
                    return true;
                }
            }

            foreach (VulkanMemoryAllocation candidate in _imageAllocations.Values)
            {
                if (candidate.Memory.Handle != memory.Handle)
                    continue;

                ulong allocationEnd = candidate.Offset + candidate.Size;
                if (candidate.BlockId == -1 || (offset >= candidate.Offset && offset < allocationEnd))
                {
                    allocation = candidate;
                    return true;
                }
            }

            foreach (VulkanMemoryAllocation candidate in _legacyBufferAllocations.Values)
            {
                if (candidate.Memory.Handle != memory.Handle)
                    continue;

                ulong allocationEnd = candidate.Offset + candidate.Size;
                if (candidate.BlockId == -1 || (offset >= candidate.Offset && offset < allocationEnd))
                {
                    allocation = candidate;
                    return true;
                }
            }

            allocation = default;
            return false;
        }

        private void NormalizeMappedMemoryRange(DeviceMemory memory, ulong offset, ulong length, out ulong flushOffset, out ulong flushSize)
        {
            ulong atomSize = _nonCoherentAtomSize == 0 ? 1UL : _nonCoherentAtomSize;
            flushOffset = (offset / atomSize) * atomSize;
            ulong flushEnd = AlignUp(offset + length, atomSize);

            if (TryGetTrackedMemoryAllocation(memory, offset, out VulkanMemoryAllocation allocation))
            {
                ulong allocationStart = allocation.BlockId == -1 ? 0UL : allocation.Offset;
                ulong allocationEnd = allocationStart + allocation.Size;
                if (flushOffset < allocationStart)
                    flushOffset = allocationStart;

                if (offset + length >= allocationEnd || flushEnd > allocationEnd)
                    flushEnd = allocationEnd;
            }

            flushSize = flushEnd > flushOffset ? flushEnd - flushOffset : Vk.WholeSize;
        }

        internal bool TryMapReadbackMemory(Buffer buffer, DeviceMemory memory, ulong offset, ulong length, out void* mappedPtr)
        {
            mappedPtr = null;

            ulong mappedLength = Math.Max(length, 1UL);
            ulong memoryOffset = GetBufferAllocationOffset(buffer) + offset;

            if (TryGetBufferMemoryAllocation(buffer, out VulkanMemoryAllocation bufferAllocation))
            {
                ulong allocationStart = bufferAllocation.BlockId == -1 ? 0UL : bufferAllocation.Offset;
                ulong allocationEnd = allocationStart + bufferAllocation.Size;
                if (memoryOffset < allocationStart || memoryOffset >= allocationEnd)
                    return false;

                ulong availableLength = allocationEnd - memoryOffset;
                if (mappedLength > availableLength)
                {
                    Debug.VulkanWarningEvery(
                        "Vulkan.Readback.ClampMappedRange",
                        TimeSpan.FromSeconds(5),
                        "[Vulkan] Clamping readback map from {0} bytes to {1} bytes for buffer 0x{2:X}; requested range exceeds allocation.",
                        mappedLength,
                        availableLength,
                        buffer.Handle);
                    mappedLength = availableLength;
                }
            }

            if (!TryMapBufferMemory(buffer, memory, offset, mappedLength, out void* localMappedPtr))
                return false;

            mappedPtr = localMappedPtr;
            InvalidateBuffer(memory, memoryOffset, mappedLength);
            RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
            RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes((long)Math.Min(length, mappedLength));
            return true;
        }

        internal void InvalidateBuffer(DeviceMemory? vkMemory, ulong offset, ulong length)
        {
            if (vkMemory is null)
                throw new ArgumentNullException(nameof(vkMemory), "Cannot invalidate null Vulkan memory.");

            if (length == 0)
                return;

            if (TryGetTrackedMemoryAllocation(vkMemory.Value, offset, out VulkanMemoryAllocation allocation) &&
                allocation.IsCoherent)
            {
                return;
            }

            NormalizeMappedMemoryRange(vkMemory.Value, offset, length, out ulong invalidateOffset, out ulong invalidateSize);

            var v = new MappedMemoryRange
            {
                SType = StructureType.MappedMemoryRange,
                Memory = vkMemory.Value,
                Offset = invalidateOffset,
                Size = invalidateSize
            };

            if (Api!.InvalidateMappedMemoryRanges(device, 1, ref v) != Result.Success)
                throw new Exception("Failed to invalidate Vulkan buffer memory.");
        }

        internal uint ResolveMemoryType(uint typeFilter, MemoryPropertyFlags properties)
        {
            if (TryFindMemoryType(typeFilter, properties, out uint exactIndex))
                return exactIndex;

            bool prefersReadbackFallback =
                (properties & (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit)) ==
                (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit);

            if (prefersReadbackFallback &&
                TryFindMemoryType(typeFilter, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out uint coherentIndex))
            {
                Debug.VulkanWarningEvery(
                    "Vulkan.ReadbackMemoryTypeFallback",
                    TimeSpan.FromSeconds(10),
                    "[Vulkan] Host-cached readback memory unavailable; falling back to host-coherent staging memory.");
                return coherentIndex;
            }

            return FindMemoryType(typeFilter, properties);
        }

        private static void RecordAllocationTelemetry(MemoryPropertyFlags properties, long bytes)
        {
            if ((properties & MemoryPropertyFlags.DeviceLocalBit) != 0 &&
                (properties & MemoryPropertyFlags.HostVisibleBit) == 0)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAllocation(RuntimeEngine.Rendering.Stats.Vulkan.EVulkanAllocationTelemetryClass.DeviceLocal, bytes);
                return;
            }

            if ((properties & MemoryPropertyFlags.HostVisibleBit) != 0 &&
                (properties & MemoryPropertyFlags.HostCachedBit) != 0)
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAllocation(RuntimeEngine.Rendering.Stats.Vulkan.EVulkanAllocationTelemetryClass.Readback, bytes);
                return;
            }

            if ((properties & MemoryPropertyFlags.HostVisibleBit) != 0)
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAllocation(RuntimeEngine.Rendering.Stats.Vulkan.EVulkanAllocationTelemetryClass.Upload, bytes);
        }

        private void RecordBufferAllocationDiagnostics(
            Buffer buffer,
            BufferUsageFlags usage,
            MemoryPropertyFlags properties,
            VulkanMemoryAllocation allocation,
            ulong requestedSize,
            bool enableDeviceAddress,
            string backend)
        {
            if (!RenderDiagnosticsFlags.UploadStageLogging &&
                !RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging)
            {
                return;
            }

            string placement = allocation.BlockId == -1
                ? "Dedicated"
                : allocation.Offset == 0 && allocation.Size >= requestedSize
                    ? "BlockOrDedicated"
                    : "Suballocated";
            ResolveBufferAllocationDiagnosticFields(
                buffer,
                allocation,
                out ulong requirementsSize,
                out ulong alignment,
                out uint heapIndex,
                out ulong heapSize,
                out MemoryHeapFlags heapFlags,
                out MemoryPropertyFlags memoryTypeFlags);

            long trackedVramBytes = RuntimeRenderingHostServices.Current.TrackedVramBytes;
            RuntimeEngine.Rendering.Stats.Vram.CanAllocateVram(
                (long)Math.Min(requestedSize, (ulong)long.MaxValue),
                0L,
                out long projectedTrackedVramBytes,
                out long trackedVramBudgetBytes);

            Debug.Vulkan(
                "[VkBufferAllocation] buffer=0x{0:X} backend={1} placement={2} memoryHeap={3} heapSize={4} heapFlags={5} memoryType={6} memoryTypeFlags={7} blockId={8} offset={9} size={10} requested={11} requirementsSize={12} alignment={13} usage={14} properties={15} deviceAddress={16} activeVkAllocations={17} allocatorBytes={18} trackedVramBytes={19} trackedVramBudgetBytes={20} projectedTrackedVramBytes={21}.",
                buffer.Handle,
                backend,
                placement,
                heapIndex,
                heapSize,
                heapFlags,
                allocation.MemoryTypeIndex,
                memoryTypeFlags,
                allocation.BlockId,
                allocation.Offset,
                allocation.Size,
                requestedSize,
                requirementsSize,
                alignment,
                usage,
                properties,
                enableDeviceAddress,
                MemoryAllocator.ActiveVkAllocationCount,
                MemoryAllocator.TotalAllocatedBytes,
                trackedVramBytes,
                trackedVramBudgetBytes,
                projectedTrackedVramBytes);
        }

        private void ResolveBufferAllocationDiagnosticFields(
            Buffer buffer,
            VulkanMemoryAllocation allocation,
            out ulong requirementsSize,
            out ulong alignment,
            out uint heapIndex,
            out ulong heapSize,
            out MemoryHeapFlags heapFlags,
            out MemoryPropertyFlags memoryTypeFlags)
        {
            requirementsSize = 0UL;
            alignment = 1UL;
            heapIndex = uint.MaxValue;
            heapSize = 0UL;
            heapFlags = 0;
            memoryTypeFlags = 0;

            if (Api is null || device.Handle == 0 || buffer.Handle == 0)
                return;

            Api.GetBufferMemoryRequirements(device, buffer, out MemoryRequirements requirements);
            requirementsSize = requirements.Size;
            alignment = Math.Max(requirements.Alignment, 1UL);

            if (_physicalDevice.Handle == 0)
                return;

            Api.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);
            if (allocation.MemoryTypeIndex >= memoryProperties.MemoryTypeCount)
                return;

            MemoryType memoryType = memoryProperties.MemoryTypes[(int)allocation.MemoryTypeIndex];
            heapIndex = memoryType.HeapIndex;
            memoryTypeFlags = memoryType.PropertyFlags;
            if (heapIndex >= memoryProperties.MemoryHeapCount)
                return;

            MemoryHeap heap = memoryProperties.MemoryHeaps[(int)heapIndex];
            heapSize = heap.Size;
            heapFlags = heap.Flags;
        }

        private static void RecordTransferQueuePolicyDiagnostics(
            Buffer stagingBuffer,
            Buffer deviceBuffer,
            ulong copySize,
            uint graphicsFamily,
            uint transferFamily,
            bool dedicatedTransferFamily)
        {
            if (!RenderDiagnosticsFlags.UploadStageLogging &&
                !RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging)
            {
                return;
            }

            Debug.Vulkan(
                "[VkBufferTransferQueue] staging=0x{0:X} device=0x{1:X} bytes={2} graphicsFamily={3} transferFamily={4} route={5} reason={6}.",
                stagingBuffer.Handle,
                deviceBuffer.Handle,
                copySize,
                graphicsFamily,
                transferFamily,
                dedicatedTransferFamily ? "DedicatedTransferQueue" : "GraphicsQueue",
                dedicatedTransferFamily ? "dedicated-transfer-family-available" : "no-dedicated-transfer-family");
        }
    }
} 
