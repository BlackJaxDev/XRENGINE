using Extensions;
using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using XREngine.Data;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        /// <summary>
        /// Vulkan data buffer with best practices: staging, synchronization, descriptor integration, lifetime, mapping, error handling, and multi-frame support.
        /// </summary>
        public class VkDataBuffer(VulkanRenderer renderer, XRDataBuffer buffer) : VkObject<XRDataBuffer>(renderer, buffer), IApiDataBuffer
        {
            // --- Resource handles ---
            private Buffer? _vkBuffer; // Device-local or host-visible buffer
            private DeviceMemory? _vkMemory;
            private ulong _bufferSize = 0;

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
                if (Engine.InvokeOnMainThread(PushData))
                    return;

                // Determine usage and memory flags
                BufferUsageFlags usage = ToVkUsageFlags(Data.Usage);
                MemoryPropertyFlags memProps = ShouldUseDeviceLocal(Data.Usage)
                    ? MemoryPropertyFlags.DeviceLocalBit
                    : MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;

                // Only recreate if size or usage changes
                if (_vkBuffer != null &&
                    _bufferSize == Data.Length &&
                    _lastUsageFlags == usage &&
                    _lastMemProps == memProps)
                    return;

                // Destroy previous buffer if exists
                Destroy();
                _bufferSize = Data.Length;
                _lastUsageFlags = usage;
                _lastMemProps = memProps;

                // --- Staging buffer pattern for device-local ---
                if (ShouldUseDeviceLocal(Data.Usage))
                {
                    // 1. Create staging buffer (host visible)
                    BufferUsageFlags stagingUsage = BufferUsageFlags.TransferSrcBit;
                    MemoryPropertyFlags stagingProps = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                    var (stagingBuffer, stagingMemory) = Renderer.CreateBuffer(_bufferSize, stagingUsage, stagingProps, Data.TryGetAddress(out var address) ? address : null);

                    // 2. Create device-local buffer
                    var (deviceBuffer, deviceMemory) = Renderer.CreateBuffer(_bufferSize, usage | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit, null);

                    // 3. Copy from staging to device-local
                    Renderer.CopyBuffer(stagingBuffer, deviceBuffer, _bufferSize);

                    // 4. Destroy staging buffer
                    Renderer.DestroyBuffer(stagingBuffer, stagingMemory);

                    _vkBuffer = deviceBuffer;
                    _vkMemory = deviceMemory;
                }
                else
                {
                    // Host-visible buffer for dynamic/stream
                    (_vkBuffer, _vkMemory) = Renderer.CreateBuffer(_bufferSize, usage, memProps, Data.TryGetAddress(out var address) ? address : null);
                }

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
                if (Engine.InvokeOnMainThread(() => PushSubData(offset, length)))
                    return;
                if (_vkBuffer == null || _vkMemory == null)
                    PushData();

                // For device-local, use staging buffer for subdata
                if (ShouldUseDeviceLocal(Data.Usage))
                {
                    BufferUsageFlags stagingUsage = BufferUsageFlags.TransferSrcBit;
                    MemoryPropertyFlags stagingProps = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                    var (stagingBuffer, stagingMemory) = Renderer.CreateBuffer(length, stagingUsage, stagingProps, Data.Address);
                    Renderer.CopyBuffer(stagingBuffer, _vkBuffer, length, (ulong)offset);
                    //stagingBuffer.Dispose();
                    //stagingMemory.Dispose();
                }
                else
                {
                    // Host-visible: map, copy, unmap
                    void* addr = Data.Address;
                    Renderer.UpdateBuffer(_vkBuffer, _vkMemory, (ulong)offset, (ulong)length, addr);
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
                if (Engine.InvokeOnMainThread(Flush))
                    return;
                // Only needed for non-coherent memory
                if ((_lastMemProps & MemoryPropertyFlags.HostCoherentBit) == 0)
                    Renderer.FlushBuffer(_vkMemory, 0, _bufferSize);
            }
            public void FlushRange(int offset, uint length)
            {
                if (Data.ActivelyMapping.Contains(this))
                    return;
                if (Engine.InvokeOnMainThread(() => FlushRange(offset, length)))
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
                    Debug.LogWarning($"Buffer {GetDescribingName()} is already mapped.");
                    return;
                }
                if (Data.Resizable)
                {
                    Debug.LogWarning($"Buffer {GetDescribingName()} is resizable and cannot be mapped.");
                    return;
                }
                if (Engine.InvokeOnMainThread(MapBufferData))
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
                if (Engine.InvokeOnMainThread(UnmapBufferData))
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

            // --- Helper: Convert usage to Vulkan flags ---
            public static BufferUsageFlags ToVkUsageFlags(EBufferUsage usage) => usage switch
            {
                EBufferUsage.StaticDraw => BufferUsageFlags.VertexBufferBit,
                EBufferUsage.DynamicDraw => BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
                EBufferUsage.StreamDraw => BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
                EBufferUsage.StaticRead => BufferUsageFlags.TransferSrcBit,
                EBufferUsage.DynamicRead => BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                EBufferUsage.StreamRead => BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                EBufferUsage.StaticCopy => BufferUsageFlags.TransferDstBit,
                EBufferUsage.DynamicCopy => BufferUsageFlags.TransferDstBit,
                EBufferUsage.StreamCopy => BufferUsageFlags.TransferDstBit,
                _ => BufferUsageFlags.VertexBufferBit,
            };

            protected override uint CreateObjectInternal()
            {
                // No explicit Vulkan object creation needed here, handled in PushData
                // Return 0 to indicate no Vulkan object ID
                return 0;
            }

            protected override void DeleteObjectInternal()
            {
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

            if (CurrentCommandBuffer is null)
            {
                // Create a new command buffer if not already created
                CommandBufferAllocateInfo allocInfo = new()
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = commandPool,
                    Level = CommandBufferLevel.Primary,
                    CommandBufferCount = 1
                };
                CommandBuffer c;
                Api!.AllocateCommandBuffers(device, ref allocInfo, &c);
                CurrentCommandBuffer = c;
            }

            var copyRegion = new BufferCopy
            {
                SrcOffset = 0,
                DstOffset = offset,
                Size = length
            };

            Api!.CmdCopyBuffer(CurrentCommandBuffer.Value, stagingBuffer.Value, vkBuffer.Value, 1, &copyRegion);
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

            CurrentCommandBuffer = null; // Reset command buffer after unmapping
        }

        private CommandBuffer? CurrentCommandBuffer;

        public void CopyBuffer(
            Buffer? stagingBuffer,
            Buffer? deviceBuffer,
            ulong bufferSize)
        {
            if (stagingBuffer is null || deviceBuffer is null)
                throw new ArgumentNullException("Buffers cannot be null for copy operation.");

            if (CurrentCommandBuffer is null)
            {
                // Create a new command buffer if not already created
                CommandBufferAllocateInfo allocInfo = new()
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = commandPool,
                    Level = CommandBufferLevel.Primary,
                    CommandBufferCount = 1
                };
                CommandBuffer c;
                Api!.AllocateCommandBuffers(device, ref allocInfo, &c);
                CurrentCommandBuffer = c;
            }

            var copyRegion = new BufferCopy
            {
                SrcOffset = 0,
                DstOffset = 0,
                Size = bufferSize
            };
            Api!.CmdCopyBuffer(CurrentCommandBuffer.Value, stagingBuffer.Value, deviceBuffer.Value, 1, &copyRegion);
        }

        public void DestroyBuffer(Buffer? vkBuffer, DeviceMemory? vkMemory)
        {
            if (vkBuffer.HasValue)
            {
                Api!.DestroyBuffer(device, vkBuffer.Value, null);
            }
            if (vkMemory.HasValue)
            {
                Api!.FreeMemory(device, vkMemory.Value, null);
            }
        }

        public (Buffer stagingBuffer, DeviceMemory stagingMemory) CreateBuffer(
            ulong bufferSize,
            BufferUsageFlags stagingUsage,
            MemoryPropertyFlags stagingProps,
            VoidPtr dataPtr)
        {
            if (bufferSize == 0)
                throw new ArgumentException("Buffer size must be greater than zero.", nameof(bufferSize));

            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = bufferSize,
                Usage = stagingUsage,
                SharingMode = SharingMode.Exclusive
            };

            if (Api!.CreateBuffer(device, ref bufferInfo, null, out Buffer stagingBuffer) != Result.Success)
                throw new Exception("Failed to create Vulkan staging buffer.");

            var memoryRequirements = Api.GetBufferMemoryRequirements(device, stagingBuffer);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memoryRequirements.Size,
                MemoryTypeIndex = FindMemoryType(memoryRequirements.MemoryTypeBits, stagingProps)
            };

            if (Api.AllocateMemory(device, ref memoryInfo, null, out DeviceMemory stagingMemory) != Result.Success)
                throw new Exception("Failed to allocate Vulkan staging memory.");

            Api.BindBufferMemory(device, stagingBuffer, stagingMemory, 0);

            // Map the buffer if needed
            if (dataPtr != null)
            {
                void* mappedPtr;
                if (Api.MapMemory(device, stagingMemory, 0, bufferSize, 0, &mappedPtr) != Result.Success)
                    throw new Exception("Failed to map Vulkan staging memory.");
                Unsafe.CopyBlock(mappedPtr, dataPtr.Pointer, (uint)bufferSize);
                Api.UnmapMemory(device, stagingMemory);
            }

            return (stagingBuffer, stagingMemory);
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