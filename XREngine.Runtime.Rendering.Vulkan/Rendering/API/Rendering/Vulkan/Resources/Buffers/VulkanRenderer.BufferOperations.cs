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
    public unsafe partial class VulkanRenderer
    {
        /// <summary>The active memory allocator (legacy per-resource or block suballocator).</summary>
        internal IVulkanMemoryAllocator MemoryAllocator
            => _bufferResourceManager.MemoryAllocator
                ?? throw new InvalidOperationException("Memory allocator not initialized.");

        /// <summary>
        /// Tracks image allocations made through the allocator.
        /// Key: Image.Handle.
        /// </summary>
        internal ulong GetBufferAllocationOffset(Buffer buffer)
        {
            if (_bufferResourceManager.Allocations.TryGetValue(buffer.Handle, out VulkanMemoryAllocation alloc))
                return alloc.Offset;

            return _bufferResourceManager.LegacyAllocations.TryGetValue(buffer.Handle, out VulkanMemoryAllocation legacyAlloc)
                ? legacyAlloc.Offset
                : 0;
        }

        private bool TryGetBufferMemoryAllocation(Buffer buffer, out VulkanMemoryAllocation allocation)
        {
            if (_bufferResourceManager.Allocations.TryGetValue(buffer.Handle, out allocation))
                return true;

            return _bufferResourceManager.LegacyAllocations.TryGetValue(buffer.Handle, out allocation);
        }

        internal bool TryMapBufferMemory(
            Buffer buffer,
            DeviceMemory memory,
            ulong bufferOffset,
            ulong length,
            out void* mappedPtr)
        {
            mappedPtr = null;
            if (!IsDeviceOperational)
                return false;

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
            => _imageAllocationTracker.Allocations.TryGetValue(image.Handle, out VulkanMemoryAllocation alloc) ? alloc.Offset : 0;

        /// <summary>
        /// Vulkan data buffer with best practices: staging, synchronization, descriptor integration, lifetime, mapping, error handling, and multi-frame support.
        /// </summary>
        private void* MapBuffer(Buffer? vkBuffer, DeviceMemory? vkMemory, ulong offset, ulong length)
        {
            if (vkBuffer is null)
                throw new ArgumentNullException(nameof(vkBuffer), "Cannot map null Vulkan buffer.");
            if (vkMemory is null)
                throw new ArgumentNullException(nameof(vkMemory), "Cannot map null Vulkan memory.");

            return MapBufferMemoryOrThrow(vkBuffer.Value, vkMemory.Value, offset, length, "Failed to map Vulkan buffer memory.");
        }

        private bool CopyBuffer(Buffer? stagingBuffer, Buffer? vkBuffer, uint length, ulong offset)
        {
            if (_deviceLost)
                return false;

            if (stagingBuffer is null || vkBuffer is null)
                throw new ArgumentNullException("Buffers cannot be null for copy operation.");

            if (TryCopyBufferViaIndirectNv(stagingBuffer.Value, vkBuffer.Value, length, 0, offset))
                return true;

            return ExecuteTransferBufferUpload(stagingBuffer.Value, vkBuffer.Value, length, 0, offset);
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
            FlushBuffer(vkBuffer, vkMemory.Value, GetBufferAllocationOffset(vkBuffer.Value) + offset, length);
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

        public bool CopyBuffer(
            Buffer? stagingBuffer,
            Buffer? deviceBuffer,
            ulong bufferSize)
        {
            if (_deviceLost)
                return false;

            if (stagingBuffer is null || deviceBuffer is null)
                throw new ArgumentNullException("Buffers cannot be null for copy operation.");

            if (TryCopyBufferViaIndirectNv(stagingBuffer.Value, deviceBuffer.Value, bufferSize, 0, 0))
                return true;

            return ExecuteTransferBufferUpload(stagingBuffer.Value, deviceBuffer.Value, bufferSize, 0, 0);
        }

        private bool ExecuteTransferBufferUpload(
            Buffer stagingBuffer,
            Buffer deviceBuffer,
            ulong copySize,
            ulong sourceOffset,
            ulong destinationOffset)
        {
            if (_deviceLost)
                return false;

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

            // Synchronous uploads are deliberately submitted on the graphics queue.
            // Queue order then protects an existing buffer from earlier graphics uses
            // without a global queue-idle wait. A dedicated transfer queue requires an
            // asynchronous upload plan with semaphore-backed ownership transfers; using
            // it here would otherwise race prior graphics submissions or force a drain.
            using (var uploadScope = NewCommandScope())
            {
                BufferCopy copyRegion = new()
                {
                    SrcOffset = sourceOffset,
                    DstOffset = destinationOffset,
                    Size = copySize
                };

                CmdCopyBufferTracked(uploadScope.CommandBuffer, stagingBuffer, deviceBuffer, 1, &copyRegion);
            }

            return !_deviceLost;
        }

        public void DestroyBuffer(Buffer? vkBuffer, DeviceMemory? vkMemory)
        {
            RetireBuffer(vkBuffer.GetValueOrDefault(), vkMemory.GetValueOrDefault());
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
            ThrowIfPersistentResourceAllocationDuringRecording(operation);

            if (IsDeviceOperational)
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
                FlushBuffer(stagingBuffer, stagingMemory, GetBufferAllocationOffset(stagingBuffer), requestedSize);
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
            TrackLiveBuffer(buffer);

            // VMA knows how to allocate buffer-device-address resources when the
            // allocator was created with VMA_ALLOCATOR_CREATE_BUFFER_DEVICE_ADDRESS_BIT.
            if (enableDeviceAddress && MemoryAllocator is not VulkanVmaAllocator)
                return CreateBufferRawLegacy(buffer, usage, properties, bufferSize);

            // Route through the selected allocator backend.
            VulkanMemoryAllocation allocation = AllocateBufferMemoryWithFallback(buffer, properties);
            _bufferResourceManager.Allocations[buffer.Handle] = allocation;

            RecordAllocationTelemetry(properties, (long)allocation.Size);
            RecordBufferAllocationDiagnostics(buffer, usage, properties, allocation, bufferSize, enableDeviceAddress, "Allocator");

            Result bindResult = Api.BindBufferMemory(device, buffer, allocation.Memory, allocation.Offset);
            if (bindResult != Result.Success)
            {
                _bufferResourceManager.Allocations.TryRemove(buffer.Handle, out _);
                FreeMemoryAllocation(allocation);
                if (TryBeginDestroyBuffer(buffer, "CreateBufferRaw.BindFailure"))
                    Api.DestroyBuffer(device, buffer, null);
                throw new Exception($"Failed to bind Vulkan buffer memory ({bindResult}).");
            }

            if (enableDeviceAddress)
            {
                ulong address = GetBufferDeviceAddress(buffer);
                RegisterVulkanDeviceAddressRange(buffer, address, bufferSize, $"Buffer.Allocator.{usage}");
            }

            return (buffer, allocation.Memory);
        }

        internal bool TryGetTrackedBufferAllocation(Buffer buffer, out VulkanMemoryAllocation allocation)
        {
            if (buffer.Handle != 0 && _bufferResourceManager.Allocations.TryGetValue(buffer.Handle, out allocation))
                return true;

            if (buffer.Handle != 0 && _bufferResourceManager.LegacyAllocations.TryGetValue(buffer.Handle, out allocation))
                return true;

            allocation = VulkanMemoryAllocation.Null;
            return false;
        }

        /// <summary>
        /// Creates a buffer backed by a dedicated <c>vkAllocateMemory</c> allocation.
        /// Use this for persistently mapped renderer-owned buffers whose map lifetime
        /// must not depend on allocator suballocation bookkeeping during shutdown.
        /// </summary>
        internal (Buffer buffer, DeviceMemory memory) CreateDedicatedBufferRaw(
            ulong bufferSize,
            BufferUsageFlags usage,
            MemoryPropertyFlags properties,
            bool enableDeviceAddress = false)
        {
            ThrowIfDeviceLostForResourceCreation("CreateDedicatedBufferRaw");

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
                throw new Exception("Failed to create dedicated Vulkan buffer.");
            TrackLiveBuffer(buffer);

            return CreateBufferRawLegacy(buffer, usage, properties, bufferSize, enableDeviceAddress);
        }

        /// <summary>Legacy path for direct Vulkan memory allocations, including non-VMA device-address buffers.</summary>
        private (Buffer buffer, DeviceMemory memory) CreateBufferRawLegacy(
            Buffer buffer,
            BufferUsageFlags usage,
            MemoryPropertyFlags properties,
            ulong bufferSize,
            bool enableDeviceAddress = true)
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
            if (enableDeviceAddress)
                memoryInfo.PNext = &memoryAllocateFlagsInfo;

            if (Api.AllocateMemory(device, ref memoryInfo, null, out DeviceMemory memory) != Result.Success)
            {
                if (TryBeginDestroyBuffer(buffer, "CreateBufferRawLegacy.AllocateFailure"))
                    Api.DestroyBuffer(device, buffer, null);
                string description = enableDeviceAddress ? "device-address" : "dedicated";
                throw new Exception($"Failed to allocate Vulkan buffer memory ({description}).");
            }

            RecordAllocationTelemetry(properties, (long)memoryInfo.AllocationSize);

            VulkanMemoryAllocation allocation = new(
                memory,
                0,
                memoryInfo.AllocationSize,
                memoryInfo.MemoryTypeIndex,
                properties,
                -1);
            _bufferResourceManager.LegacyAllocations[buffer.Handle] = allocation;
            RecordBufferAllocationDiagnostics(
                buffer,
                usage,
                properties,
                allocation,
                bufferSize,
                enableDeviceAddress,
                enableDeviceAddress ? "LegacyDeviceAddress" : "Dedicated");

            Result bindResult = Api.BindBufferMemory(device, buffer, memory, 0);
            if (bindResult != Result.Success)
            {
                _bufferResourceManager.LegacyAllocations.TryRemove(buffer.Handle, out _);
                Api.FreeMemory(device, memory, null);
                if (TryBeginDestroyBuffer(buffer, "CreateBufferRawLegacy.BindFailure"))
                    Api.DestroyBuffer(device, buffer, null);
                throw new Exception($"Failed to bind Vulkan buffer memory ({bindResult}).");
            }

            if (enableDeviceAddress)
            {
                ulong address = GetBufferDeviceAddress(buffer);
                RegisterVulkanDeviceAddressRange(buffer, address, bufferSize, $"Buffer.Legacy.{usage}");
            }

            return (buffer, memory);
        }

        internal unsafe void UploadBufferMemory(Buffer buffer, DeviceMemory memory, ulong size, void* source)
        {
            if (_deviceLost)
                return;

            if (source == null || size == 0)
                return;

            if (!TryMapBufferMemory(buffer, memory, 0, size, out void* mappedPtr))
                throw new Exception("Failed to map Vulkan memory for staging upload.");

            try
            {
                Unsafe.CopyBlock(mappedPtr, source, (uint)size);
                FlushBuffer(buffer, memory, GetBufferAllocationOffset(buffer), size);
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
                VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                    ObjectType.Buffer,
                    buffer.Value.Handle,
                    nameof(DestroyBufferRaw));
                if (!IsVulkanRetirementReady(ticket))
                {
                    RetireBuffer(buffer.Value, memory.GetValueOrDefault());
                    return;
                }
            }

            if (buffer.HasValue && buffer.Value.Handle != 0)
                _stagingManager.TryForget(buffer.Value, memory.GetValueOrDefault());

            if (buffer.HasValue && buffer.Value.Handle != 0)
            {
                if (TryDestroyKnownBufferAllocation(buffer.Value, out _, out _))
                    return;

                if (!TryBeginDestroyBuffer(buffer.Value, "DestroyBufferRaw"))
                    return;

                Api!.DestroyBuffer(device, buffer.Value, null);
            }

            // Untracked memory (device-address, legacy, or staging pool) — free directly.
            if (memory.HasValue && memory.Value.Handle != 0)
                FreeUntrackedBufferMemory(memory.Value, "DestroyBufferRaw");
        }

        private bool TryDestroyKnownBufferAllocation(
            Buffer buffer,
            out bool destroyedBuffer,
            out bool freedMemory)
        {
            destroyedBuffer = false;
            freedMemory = false;

            if (_bufferResourceManager.Allocations.TryRemove(buffer.Handle, out VulkanMemoryAllocation allocation))
            {
                if (TryBeginDestroyBuffer(buffer, "TrackedAllocatorBuffer"))
                {
                    Api!.DestroyBuffer(device, buffer, null);
                    destroyedBuffer = true;
                    FreeMemoryAllocation(allocation);
                    freedMemory = true;
                }
                return true;
            }

            if (_bufferResourceManager.LegacyAllocations.TryRemove(buffer.Handle, out VulkanMemoryAllocation legacyAllocation))
            {
                if (TryBeginDestroyBuffer(buffer, "TrackedLegacyBuffer"))
                {
                    Api!.DestroyBuffer(device, buffer, null);
                    destroyedBuffer = true;
                    FreeLegacyBufferMemory(legacyAllocation);
                    freedMemory = legacyAllocation.Memory.Handle != 0;
                }
                return true;
            }

            return false;
        }

        private void TrackLiveBuffer(Buffer buffer)
        {
            if (buffer.Handle != 0)
            {
                _bufferResourceManager.LiveHandles[buffer.Handle] = 0;
                RegisterVulkanResource(ObjectType.Buffer, buffer.Handle, "Buffer.Allocation");
            }
        }

        private bool TryBeginDestroyBuffer(Buffer buffer, string owner)
        {
            if (buffer.Handle == 0)
                return false;

            VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                ObjectType.Buffer,
                buffer.Handle,
                owner);
            if (!IsVulkanRetirementReady(ticket))
                return false;

            if (_bufferResourceManager.LiveHandles.TryRemove(buffer.Handle, out _))
            {
                UnregisterVulkanDeviceAddressRange(buffer);
                CompleteVulkanResourceDestruction(ObjectType.Buffer, buffer.Handle);
                return true;
            }

            Debug.VulkanWarningEvery(
                $"Vulkan.Buffer.SkipStaleDestroy.{GetHashCode()}.{owner}.{buffer.Handle}",
                TimeSpan.FromSeconds(5),
                "[Vulkan] Skipping stale destroy for buffer 0x{0:X} in {1}; the handle is not live in renderer tracking.",
                buffer.Handle,
                owner);
            return false;
        }

        private bool TryFreeTrackedLegacyBufferMemory(DeviceMemory memory)
        {
            foreach (var pair in _bufferResourceManager.LegacyAllocations.ToArray())
            {
                if (pair.Value.Memory.Handle != memory.Handle)
                    continue;

                if (!_bufferResourceManager.LegacyAllocations.TryRemove(pair.Key, out VulkanMemoryAllocation allocation))
                    return false;

                FreeLegacyBufferMemory(allocation);
                return true;
            }

            return false;
        }

        private void FreeLegacyBufferMemory(VulkanMemoryAllocation allocation)
        {
            if (allocation.Memory.Handle != 0)
                Api!.FreeMemory(device, allocation.Memory, null);
        }

        /// <summary>
        /// Releases memory that no live buffer allocation owns. Raw <c>vkFreeMemory</c>
        /// is only safe for the legacy allocator; allocator-backed modes must skip
        /// unknown handles because they may belong to shared native allocator blocks.
        /// </summary>
        private bool FreeUntrackedBufferMemory(DeviceMemory memory, string owner)
        {
            if (TryFreeTrackedLegacyBufferMemory(memory))
                return true;

            if (MemoryAllocator is VulkanLegacyAllocator)
            {
                Api!.FreeMemory(device, memory, null);
                return true;
            }

            Debug.VulkanWarningEvery(
                $"Vulkan.BufferMemory.SkipUnknownRawFree.{GetHashCode()}.{owner}.{memory.Handle}",
                TimeSpan.FromSeconds(5),
                "[Vulkan] Skipping raw vkFreeMemory for untracked buffer memory 0x{0:X} in {1}; current allocator is {2}, so the handle may be allocator-owned shared memory.",
                memory.Handle,
                owner,
                MemoryAllocator.GetType().Name);
            return false;
        }

        public void FlushBuffer(
            Buffer? vkBuffer,
            DeviceMemory? vkMemory,
            ulong offset,
            ulong length)
        {
            if (vkMemory is null)
                throw new ArgumentNullException(nameof(vkMemory), "Cannot flush null Vulkan memory.");

            if (length == 0)
                return;

            VulkanMemoryAllocation allocation = default;
            bool hasTrackedAllocation =
                vkBuffer is { } buffer &&
                TryGetBufferMemoryAllocation(buffer, out allocation);
            if (!hasTrackedAllocation)
                hasTrackedAllocation = TryGetTrackedMemoryAllocation(vkMemory.Value, offset, out allocation);

            if (hasTrackedAllocation && allocation.IsCoherent)
            {
                return;
            }

            NormalizeMappedMemoryRange(
                vkMemory.Value,
                offset,
                length,
                in allocation,
                hasTrackedAllocation,
                out ulong flushOffset,
                out ulong flushSize);

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
            foreach (KeyValuePair<ulong, VulkanMemoryAllocation> pair in _bufferResourceManager.Allocations)
            {
                VulkanMemoryAllocation candidate = pair.Value;
                if (candidate.Memory.Handle != memory.Handle)
                    continue;

                ulong allocationEnd = candidate.Offset + candidate.Size;
                if (candidate.BlockId == -1 || (offset >= candidate.Offset && offset < allocationEnd))
                {
                    allocation = candidate;
                    return true;
                }
            }

            foreach (KeyValuePair<ulong, VulkanMemoryAllocation> pair in _imageAllocationTracker.Allocations)
            {
                VulkanMemoryAllocation candidate = pair.Value;
                if (candidate.Memory.Handle != memory.Handle)
                    continue;

                ulong allocationEnd = candidate.Offset + candidate.Size;
                if (candidate.BlockId == -1 || (offset >= candidate.Offset && offset < allocationEnd))
                {
                    allocation = candidate;
                    return true;
                }
            }

            foreach (KeyValuePair<ulong, VulkanMemoryAllocation> pair in _bufferResourceManager.LegacyAllocations)
            {
                VulkanMemoryAllocation candidate = pair.Value;
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
            bool hasTrackedAllocation = TryGetTrackedMemoryAllocation(
                memory,
                offset,
                out VulkanMemoryAllocation allocation);
            NormalizeMappedMemoryRange(
                memory,
                offset,
                length,
                in allocation,
                hasTrackedAllocation,
                out flushOffset,
                out flushSize);
        }

        private void NormalizeMappedMemoryRange(
            DeviceMemory memory,
            ulong offset,
            ulong length,
            in VulkanMemoryAllocation allocation,
            bool hasTrackedAllocation,
            out ulong flushOffset,
            out ulong flushSize)
        {
            ulong atomSize = _nonCoherentAtomSize == 0 ? 1UL : _nonCoherentAtomSize;
            flushOffset = (offset / atomSize) * atomSize;
            ulong flushEnd = AlignUp(offset + length, atomSize);

            if (hasTrackedAllocation)
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

            long trackedVramBytes = RuntimeRenderingHostServices.FrameTiming.TrackedVramBytes;
            string allocationClass = ClassifyVulkanAllocation(properties, allocation.Properties);
            RuntimeEngine.Rendering.Stats.Vram.CanAllocateVram(
                IsDeviceLocalVramAllocation(allocation.Properties)
                    ? (long)Math.Min(requestedSize, (ulong)long.MaxValue)
                    : 0L,
                0L,
                out long projectedTrackedVramBytes,
                out long trackedVramBudgetBytes);

            Debug.Vulkan(
                "[VkBufferAllocation] buffer=0x{0:X} backend={1} placement={2} allocationClass={3} memoryHeap={4} heapSize={5} heapFlags={6} memoryType={7} memoryTypeFlags={8} blockId={9} offset={10} size={11} requested={12} requirementsSize={13} alignment={14} usage={15} requestedProperties={16} allocationProperties={17} deviceAddress={18} activeVkAllocations={19} allocatorBytes={20} trackedVramBytes={21} trackedVramBudgetBytes={22} projectedTrackedVramBytes={23}.",
                buffer.Handle,
                backend,
                placement,
                allocationClass,
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
                allocation.Properties,
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

        private static bool IsDeviceLocalVramAllocation(MemoryPropertyFlags properties)
            => (properties & MemoryPropertyFlags.DeviceLocalBit) != 0;

        private static string ClassifyVulkanAllocation(
            MemoryPropertyFlags requestedProperties,
            MemoryPropertyFlags allocationProperties)
        {
            bool deviceLocal = (allocationProperties & MemoryPropertyFlags.DeviceLocalBit) != 0;
            bool hostVisible = (allocationProperties & MemoryPropertyFlags.HostVisibleBit) != 0;
            bool hostCached = (allocationProperties & MemoryPropertyFlags.HostCachedBit) != 0;

            if (deviceLocal && hostVisible)
                return "DeviceLocalHostVisible";
            if (deviceLocal)
                return "DeviceLocal";
            if (hostVisible && hostCached)
                return "Readback";
            if (hostVisible)
                return (requestedProperties & MemoryPropertyFlags.DeviceLocalBit) != 0
                    ? "UploadFallback"
                    : "Upload";

            return "Other";
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
                "GraphicsQueue",
                dedicatedTransferFamily
                    ? "synchronous-upload-requires-queue-order"
                    : "no-dedicated-transfer-family");
        }
    }
}
