using Extensions;
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

        /// <summary>
        /// Vulkan data buffer with best practices: staging, synchronization, descriptor integration, lifetime, mapping, error handling, and multi-frame support.
        /// </summary>
        public class VkDataBuffer(VulkanRenderer renderer, XRDataBuffer buffer) : VkObject<XRDataBuffer>(renderer, buffer), IApiDataBuffer
        {
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

            protected internal override void PostGenerated()
            {
                if (Data.Resizable)
                    PushData();
                else
                    AllocateImmutable();
            }

            /// <summary>
            /// Pushes data to the GPU. Uses staging buffer for device-local, host-visible for dynamic.
            /// </summary>
            public void PushData()
            {
                if (Data.ActivelyMapping.Contains(this))
                    return;
                if (Engine.InvokeOnMainThread(PushData, "VkDataBuffer.PushData"))
                    return;

                // Determine usage and memory flags
                BufferUsageFlags usage = ResolveVkUsageFlags(Data.Target, Data.Usage);
                MemoryPropertyFlags memProps = ShouldUseDeviceLocal(Data.Usage)
                    ? MemoryPropertyFlags.DeviceLocalBit
                    : MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;

                bool needsRecreate =
                    _vkBuffer is null ||
                    _vkMemory is null ||
                    _bufferSize != Data.Length ||
                    _lastUsageFlags != usage ||
                    _lastMemProps != memProps;

                if (needsRecreate)
                {
                    // Destroy previous buffer if exists (this will also track VRAM deallocation)
                    Destroy();
                    _bufferSize = Data.Length;
                    _lastUsageFlags = usage;
                    _lastMemProps = memProps;

                    // --- Staging buffer pattern for device-local ---
                    if (ShouldUseDeviceLocal(Data.Usage))
                    {
                        bool preferIndirectCopy = Renderer.SupportsNvCopyMemoryIndirect && Renderer.SupportsBufferDeviceAddress;

                        // Create device-local buffer first.
                        BufferUsageFlags deviceUsage = usage | BufferUsageFlags.TransferDstBit;
                        if (preferIndirectCopy)
                            deviceUsage |= BufferUsageFlags.ShaderDeviceAddressBit;

                        var (deviceBuffer, deviceMemory) = Renderer.CreateBuffer(
                            _bufferSize,
                            deviceUsage,
                            MemoryPropertyFlags.DeviceLocalBit,
                            null,
                            preferIndirectCopy);
                        _vkBuffer = deviceBuffer;
                        _vkMemory = deviceMemory;

                        if (TryUploadGpuCompressedPayload(deviceBuffer))
                        {
                            // GPU-side decompression upload succeeded; no staging copy required.
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
                        }
                    }
                    else
                    {
                        // Host-visible buffer for dynamic/stream
                        (_vkBuffer, _vkMemory) = Renderer.CreateBuffer(_bufferSize, usage, memProps, Data.TryGetAddress(out var address) ? address : null);
                    }

                    // Track VRAM allocation only when the backing allocation is recreated.
                    _allocatedVRAMBytes = (long)_bufferSize;
                    Engine.Rendering.Stats.AddBufferAllocation(_allocatedVRAMBytes);
                }
                else
                {
                    // Reuse the existing allocation and upload fresh data even when size/usage are unchanged.
                    PushSubData(0, Data.Length);
                    if (Data.DisposeOnPush)
                        Data.Dispose();
                    return;
                }

                Renderer.TrackBufferBinding(Data);

                if (Data.DisposeOnPush)
                    Data.Dispose();
            }

            /// <summary>
            /// Pushes a subrange of data to the GPU. Uses staging if device-local.
            /// </summary>
            public void PushSubData(int offset, uint length)
            {
                if (Data.ActivelyMapping.Contains(this))
                    return;
                if (Engine.InvokeOnMainThread(() => PushSubData(offset, length), "VkDataBuffer.PushSubData"))
                    return;
                if (offset < 0 || length == 0)
                    return;

                uint totalLength = Data.Length;
                if ((uint)offset >= totalLength)
                    return;

                uint clampedLength = Math.Min(length, totalLength - (uint)offset);
                if (clampedLength == 0)
                    return;

                if (_vkBuffer == null || _vkMemory == null)
                    PushData();

                if (_vkBuffer is null || _vkMemory is null)
                    return;

                // For device-local, use staging buffer for subdata
                if (ShouldUseDeviceLocal(Data.Usage))
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

                    bool preferIndirectCopy = Renderer.SupportsNvCopyMemoryIndirect && Renderer.SupportsBufferDeviceAddress;

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
                    Renderer.UpdateBuffer(_vkBuffer, _vkMemory, (ulong)offset, (ulong)clampedLength, sourceSlice.Pointer);
                }

                Renderer.TrackBufferBinding(Data);
            }

            private bool TryUploadGpuCompressedPayload(Buffer deviceBuffer)
            {
                if (!Data.HasGpuCompressedPayload || Data.GpuCompressedSource is null)
                    return false;

                if (Data.GpuCompressionCodec != XRDataBuffer.EBufferCompressionCodec.GDeflate)
                    return false;

                if (!Renderer.SupportsNvMemoryDecompression || !Renderer.SupportsBufferDeviceAddress)
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
            public void PushSubData() => PushSubData(0, Data.Length);

            /// <summary>
            /// Flushes mapped memory range. Only needed for non-coherent memory.
            /// </summary>
            public void Flush()
            {
                if (Data.ActivelyMapping.Contains(this))
                    return;
                if (Engine.InvokeOnMainThread(Flush, "VkDataBuffer.Flush"))
                    return;
                // Only needed for non-coherent memory
                if ((_lastMemProps & MemoryPropertyFlags.HostCoherentBit) == 0)
                    Renderer.FlushBuffer(_vkMemory, 0, _bufferSize);
            }
            public void FlushRange(int offset, uint length)
            {
                if (Data.ActivelyMapping.Contains(this))
                    return;
                if (Engine.InvokeOnMainThread(() => FlushRange(offset, length), "VkDataBuffer.FlushRange"))
                    return;
                if ((_lastMemProps & MemoryPropertyFlags.HostCoherentBit) == 0)
                    Renderer.FlushBuffer(_vkMemory, (ulong)offset, (ulong)length);
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
                if (Data.ActivelyMapping.Contains(this))
                {
                    Debug.VulkanWarning($"Buffer {GetDescribingName()} is already mapped.");
                    return;
                }
                if (Data.Resizable)
                {
                    Debug.VulkanWarning($"Buffer {GetDescribingName()} is resizable and cannot be mapped.");
                    return;
                }
                if (Engine.InvokeOnMainThread(MapBufferData, "VkDataBuffer.MapBufferData"))
                    return;
                MapToClientSide();
            }
            public void MapToClientSide()
            {
                if (_vkBuffer == null || _vkMemory == null)
                    return;
                GPUSideSource?.Dispose();
                // Persistent mapping for dynamic buffers
                if (_persistentMappedPtr == null)
                    _persistentMappedPtr = Renderer.MapBuffer(_vkMemory, 0, _bufferSize);
                if (_persistentMappedPtr == null)
                    return;
                GPUSideSource = new DataSource(_persistentMappedPtr, (uint)_bufferSize);
                Data.ActivelyMapping.Add(this);
            }
            public void MapToClientSide(int offset, uint length)
            {
                if (_vkBuffer == null || _vkMemory == null)
                    return;
                GPUSideSource?.Dispose();
                if (_persistentMappedPtr == null)
                    _persistentMappedPtr = Renderer.MapBuffer(_vkMemory, (ulong)offset, (ulong)length);
                if (_persistentMappedPtr == null)
                    return;
                GPUSideSource = new DataSource(_persistentMappedPtr, length);
                Data.ActivelyMapping.Add(this);
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
                if (Engine.InvokeOnMainThread(UnmapBufferData, "VkDataBuffer.UnmapBufferData"))
                    return;
                if (_persistentMappedPtr != null)
                {
                    Renderer.UnmapBuffer(_vkMemory);
                    _persistentMappedPtr = null;
                }
                Data.ActivelyMapping.Remove(this);
                GPUSideSource?.Dispose();
                GPUSideSource = null;
            }

            /// <summary>
            /// Hooks for descriptor set integration (uniform/storage buffer binding).
            /// </summary>
            public void SetUniformBlockName(XRRenderProgram program, string blockName)
            {
                // Vulkan: handled via descriptor set layouts and bindings
                // Integrate with descriptor set manager if needed
            }
            public void SetBlockIndex(uint blockIndex)
            {
                // Vulkan: handled via descriptor set binding
                // Integrate with descriptor set manager if needed
            }

            public void BindSSBO(XRRenderProgram program, uint? bindingIndexOverride = null)
            {
                if (program is null)
                    return;

                uint binding = bindingIndexOverride ?? Data.BindingIndexOverride ?? 0u;
                program.BindBuffer(Data, binding);
            }

            protected internal override void PreDeleted()
                => UnmapBufferData();

            public void Bind() { /* Vulkan: binding is handled via descriptor sets */ }
            public void Unbind() { /* Vulkan: unbinding is not required */ }

            public bool IsMapped => Data.ActivelyMapping.Contains(this);

            public override bool IsGenerated { get; }

            public VoidPtr? GetMappedAddress() => GPUSideSource?.Address;

            // --- Helper: Should use device-local + staging for static/immutable buffers ---
            private static bool ShouldUseDeviceLocal(EBufferUsage usage)
                => usage == EBufferUsage.StaticDraw || usage == EBufferUsage.StaticCopy;

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

            private static BufferUsageFlags ResolveVkUsageFlags(EBufferTarget target, EBufferUsage usage)
            {
                BufferUsageFlags flags = ToVkUsageFlags(target) | ToVkUsageFlags(usage);
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
                EBufferTarget.DrawIndirectBuffer => BufferUsageFlags.IndirectBufferBit,
                EBufferTarget.ShaderStorageBuffer => BufferUsageFlags.StorageBufferBit,
                EBufferTarget.DispatchIndirectBuffer => BufferUsageFlags.IndirectBufferBit,
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
                // No explicit Vulkan object creation needed here, handled in PushData
                // Return 0 to indicate no Vulkan object ID
                return 0;
            }

            protected override void DeleteObjectInternal()
            {
                // Track VRAM deallocation
                if (_allocatedVRAMBytes > 0)
                {
                    Engine.Rendering.Stats.RemoveBufferAllocation(_allocatedVRAMBytes);
                    _allocatedVRAMBytes = 0;
                }

                // Clean up Vulkan resources
                if (_vkBuffer.HasValue)
                {
                    Api!.DestroyBuffer(Renderer.device, _vkBuffer.Value, null);
                    _vkBuffer = null;
                }
                if (_vkMemory.HasValue)
                {
                    Api!.FreeMemory(Renderer.device, _vkMemory.Value, null);
                    _vkMemory = null;
                }
                _persistentMappedPtr = null; // Reset persistent mapping pointer
            }
        }

        private void* MapBuffer(DeviceMemory? vkMemory, ulong offset, ulong length)
        {
            if (vkMemory is null)
                throw new ArgumentNullException(nameof(vkMemory), "Cannot map null Vulkan memory.");

            void* mappedPtr;
            if (Api!.MapMemory(device, vkMemory.Value, offset, length, 0, &mappedPtr) != Result.Success)
                throw new Exception("Failed to map Vulkan buffer memory.");

            return mappedPtr; // Return the mapped pointer
        }

        private void CopyBuffer(Buffer? stagingBuffer, Buffer? vkBuffer, uint length, ulong offset)
        {
            if (stagingBuffer is null || vkBuffer is null)
                throw new ArgumentNullException("Buffers cannot be null for copy operation.");

            if (TryCopyBufferViaIndirectNv(stagingBuffer.Value, vkBuffer.Value, length, 0, offset))
                return;

            using var scope = NewCommandScope();
            BufferCopy copyRegion = new()
            {
                SrcOffset = 0,
                DstOffset = offset,
                Size = length
            };

            Api!.CmdCopyBuffer(scope.CommandBuffer, stagingBuffer.Value, vkBuffer.Value, 1, &copyRegion);
        }

        private void UpdateBuffer(Buffer? vkBuffer, DeviceMemory? vkMemory, ulong offset, ulong length, void* addr)
        {
            if (vkBuffer is null || vkMemory is null || addr is null)
                throw new ArgumentNullException("Buffer, memory, or address cannot be null for update operation.");

            void* mappedPtr;
            if (Api!.MapMemory(device, vkMemory.Value, offset, length, 0, &mappedPtr) != Result.Success)
                throw new Exception("Failed to map Vulkan buffer memory.");

            Unsafe.CopyBlock(mappedPtr, addr, (uint)length);
            Api.UnmapMemory(device, vkMemory.Value); // Unmap after copying
        }

        private void UnmapBuffer(DeviceMemory? vkMemory)
        {
            if (vkMemory is null)
                throw new ArgumentNullException(nameof(vkMemory), "Cannot unmap null Vulkan memory.");

            Api!.UnmapMemory(device, vkMemory.Value);
        }

        public void CopyBuffer(
            Buffer? stagingBuffer,
            Buffer? deviceBuffer,
            ulong bufferSize)
        {
            if (stagingBuffer is null || deviceBuffer is null)
                throw new ArgumentNullException("Buffers cannot be null for copy operation.");

            if (TryCopyBufferViaIndirectNv(stagingBuffer.Value, deviceBuffer.Value, bufferSize, 0, 0))
                return;

            using var scope = NewCommandScope();
            BufferCopy copyRegion = new()
            {
                SrcOffset = 0,
                DstOffset = 0,
                Size = bufferSize
            };

            Api!.CmdCopyBuffer(scope.CommandBuffer, stagingBuffer.Value, deviceBuffer.Value, 1, &copyRegion);
        }

        public void DestroyBuffer(Buffer? vkBuffer, DeviceMemory? vkMemory)
        {
            if (vkBuffer.HasValue && vkMemory.HasValue && _stagingManager.TryRelease(vkBuffer.Value, vkMemory.Value))
                return;

            DestroyBufferRaw(vkBuffer, vkMemory);
        }

        public (Buffer stagingBuffer, DeviceMemory stagingMemory) CreateBuffer(
            ulong bufferSize,
            BufferUsageFlags stagingUsage,
            MemoryPropertyFlags stagingProps,
            VoidPtr dataPtr,
            bool enableDeviceAddress = false)
        {
            if (bufferSize == 0)
                throw new ArgumentException("Buffer size must be greater than zero.", nameof(bufferSize));

            if (_stagingManager.CanPool(stagingUsage, stagingProps))
                return _stagingManager.Acquire(this, bufferSize, stagingUsage, stagingProps, dataPtr);

            (Buffer stagingBuffer, DeviceMemory stagingMemory) = CreateBufferRaw(bufferSize, stagingUsage, stagingProps, enableDeviceAddress);

            // Map the buffer if needed.
            if (dataPtr != null)
            {
                void* mappedPtr = null;
                if (Api!.MapMemory(device, stagingMemory, 0, bufferSize, 0, &mappedPtr) != Result.Success)
                    throw new Exception("Failed to map Vulkan memory.");
                Unsafe.CopyBlock(mappedPtr, dataPtr.Pointer, (uint)bufferSize);
                Api.UnmapMemory(device, stagingMemory);
            }

            return (stagingBuffer, stagingMemory);
        }

        internal (Buffer buffer, DeviceMemory memory) CreateBufferRaw(
            ulong bufferSize,
            BufferUsageFlags usage,
            MemoryPropertyFlags properties,
            bool enableDeviceAddress = false)
        {
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

            MemoryRequirements memoryRequirements = Api.GetBufferMemoryRequirements(device, buffer);
            MemoryAllocateInfo memoryInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = FindMemoryType(memoryRequirements.MemoryTypeBits, properties)
            };

            MemoryAllocateFlagsInfo memoryAllocateFlagsInfo = new()
            {
                SType = StructureType.MemoryAllocateFlagsInfo,
                PNext = null,
                Flags = MemoryAllocateFlags.DeviceAddressBit,
                DeviceMask = 0,
            };

            if (enableDeviceAddress)
                memoryInfo.PNext = &memoryAllocateFlagsInfo;

            if (Api.AllocateMemory(device, ref memoryInfo, null, out DeviceMemory memory) != Result.Success)
            {
                Api.DestroyBuffer(device, buffer, null);
                throw new Exception("Failed to allocate Vulkan buffer memory.");
            }

            Result bindResult = Api.BindBufferMemory(device, buffer, memory, 0);
            if (bindResult != Result.Success)
            {
                Api.FreeMemory(device, memory, null);
                Api.DestroyBuffer(device, buffer, null);
                throw new Exception($"Failed to bind Vulkan buffer memory ({bindResult}).");
            }

            return (buffer, memory);
        }

        internal unsafe void UploadBufferMemory(DeviceMemory memory, ulong size, void* source)
        {
            if (source == null || size == 0)
                return;

            void* mappedPtr = null;
            if (Api!.MapMemory(device, memory, 0, size, 0, &mappedPtr) != Result.Success)
                throw new Exception("Failed to map Vulkan memory for staging upload.");

            try
            {
                Unsafe.CopyBlock(mappedPtr, source, (uint)size);
            }
            finally
            {
                Api.UnmapMemory(device, memory);
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

            if (string.IsNullOrWhiteSpace(filePath) || length <= 0)
                return false;

            (stagingBuffer, stagingMemory) = CreateBufferRaw(
                (ulong)length,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* mappedPtr = null;
            if (Api!.MapMemory(device, stagingMemory, 0, (ulong)length, 0, &mappedPtr) != Result.Success)
            {
                DestroyBufferRaw(stagingBuffer, stagingMemory);
                stagingBuffer = default;
                stagingMemory = default;
                return false;
            }

            try
            {
                DirectStorageIO.TryReadInto(filePath, offset, length, mappedPtr);
            }
            catch
            {
                Api.UnmapMemory(device, stagingMemory);
                DestroyBufferRaw(stagingBuffer, stagingMemory);
                stagingBuffer = default;
                stagingMemory = default;
                return false;
            }

            Api.UnmapMemory(device, stagingMemory);
            return true;
        }

        internal void DestroyBufferRaw(Buffer? buffer, DeviceMemory? memory)
        {
            if (buffer.HasValue && buffer.Value.Handle != 0)
                Api!.DestroyBuffer(device, buffer.Value, null);

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

            var v = new MappedMemoryRange
            {
                SType = StructureType.MappedMemoryRange,
                Memory = vkMemory.Value,
                Offset = offset,
                Size = length
            };

            if (Api!.FlushMappedMemoryRanges(device, 1, ref v) != Result.Success)
                throw new Exception("Failed to flush Vulkan buffer memory.");
        }
    }
} 
