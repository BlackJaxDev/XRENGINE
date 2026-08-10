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
    internal sealed unsafe partial class VulkanCommandRuntime
    {
        /// <summary>The active memory allocator (legacy per-resource or block suballocator).</summary>
        internal IVulkanMemoryAllocator MemoryAllocator
            => ResourceRuntime.Allocations.Buffers.MemoryAllocator
                ?? throw new InvalidOperationException("Memory allocator not initialized.");

        private VulkanBackendObjectContext BackendObjectContext
            => ResourceRuntime.BackendObjectContext
                ?? throw new InvalidOperationException("Vulkan backend object context is not initialized.");

        /// <summary>
        /// Tracks image allocations made through the allocator.
        /// Key: Image.Handle.
        /// </summary>
        internal ulong GetBufferAllocationOffset(Buffer buffer)
            => ResourceRuntime.GetBufferAllocationOffset(buffer);

        internal bool TryMapBufferMemory(
            Buffer buffer,
            DeviceMemory memory,
            ulong bufferOffset,
            ulong length,
            out void* mappedPtr)
            => ResourceRuntime.TryMapBufferMemory(buffer, memory, bufferOffset, length, out mappedPtr);

        internal void UnmapBufferMemory(Buffer buffer, DeviceMemory memory)
            => ResourceRuntime.UnmapBufferMemory(buffer, memory);

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
            => ResourceRuntime.Allocations.Images.Allocations.TryGetValue(
                image.Handle,
                out VulkanMemoryAllocation allocation)
                ? allocation.Offset
                : 0;

        /// <summary>
        /// Vulkan data buffer with best practices: staging, synchronization, descriptor integration, lifetime, mapping, error handling, and multi-frame support.
        /// </summary>
        internal void* MapBuffer(Buffer? vkBuffer, DeviceMemory? vkMemory, ulong offset, ulong length)
        {
            if (vkBuffer is null)
                throw new ArgumentNullException(nameof(vkBuffer), "Cannot map null Vulkan buffer.");
            if (vkMemory is null)
                throw new ArgumentNullException(nameof(vkMemory), "Cannot map null Vulkan memory.");

            return MapBufferMemoryOrThrow(vkBuffer.Value, vkMemory.Value, offset, length, "Failed to map Vulkan buffer memory.");
        }

        internal bool CopyBuffer(Buffer? stagingBuffer, Buffer? vkBuffer, uint length, ulong offset)
        {
            if (_deviceLost)
                return false;

            if (stagingBuffer is null || vkBuffer is null)
                throw new ArgumentNullException("Buffers cannot be null for copy operation.");

            if (TryCopyBufferViaIndirectNv(stagingBuffer.Value, vkBuffer.Value, length, 0, offset))
                return true;

            return _commandRuntime.ExecuteSynchronousBufferUpload(
                stagingBuffer.Value,
                vkBuffer.Value,
                length,
                0,
                offset);
        }

        internal void UpdateBuffer(Buffer? vkBuffer, DeviceMemory? vkMemory, ulong offset, ulong length, void* addr)
        {
            if (_deviceLost)
                return;

            if (vkBuffer is null || vkMemory is null || addr is null)
                throw new ArgumentNullException("Buffer, memory, or address cannot be null for update operation.");

            ResourceRuntime.UpdateBufferMemory(vkBuffer.Value, vkMemory.Value, offset, length, addr);
        }

        internal void UnmapBuffer(Buffer? vkBuffer, DeviceMemory? vkMemory)
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

            return _commandRuntime.ExecuteSynchronousBufferUpload(
                stagingBuffer.Value,
                deviceBuffer.Value,
                bufferSize,
                0,
                0);
        }

        public void DestroyBuffer(Buffer? vkBuffer, DeviceMemory? vkMemory)
        {
            ResourceRuntime.Buffers.Retire(vkBuffer.GetValueOrDefault(), vkMemory.GetValueOrDefault(), "VulkanCommandRuntime.Buffer");
        }

        private void ThrowIfDeviceLostForResourceCreation(string operation)
        {
            ThrowIfPersistentResourceAllocationDuringCommandRecording(operation);

            if (_deviceContext.IsOperational)
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
            => ResourceRuntime.Buffers.Create(
                BackendObjectContext,
                bufferSize,
                stagingUsage,
                stagingProps,
                dataPtr,
                enableDeviceAddress,
                "RendererCompatibility.Buffer");

        internal (Buffer buffer, DeviceMemory memory) CreateBufferRaw(
            ulong bufferSize,
            BufferUsageFlags usage,
            MemoryPropertyFlags properties,
            bool enableDeviceAddress = false)
            => ResourceRuntime.Buffers.CreateRaw(
                BackendObjectContext,
                bufferSize,
                usage,
                properties,
                enableDeviceAddress,
                "RendererCompatibility.RawBuffer");

        internal bool TryGetTrackedBufferAllocation(Buffer buffer, out VulkanMemoryAllocation allocation)
            => ResourceRuntime.Buffers.TryGetAllocation(buffer, out allocation);

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
            => ResourceRuntime.Buffers.CreateDedicatedRaw(
                BackendObjectContext,
                bufferSize,
                usage,
                properties,
                enableDeviceAddress,
                "RendererCompatibility.DedicatedBuffer");

        internal unsafe void UploadBufferMemory(Buffer buffer, DeviceMemory memory, ulong size, void* source)
            => ResourceRuntime.Buffers.Update(
                BackendObjectContext,
                buffer,
                memory,
                0,
                size,
                source);

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
            Buffer resolvedBuffer = buffer.GetValueOrDefault();
            DeviceMemory resolvedMemory = memory.GetValueOrDefault();
            ResourceRuntime.DestroyBufferRaw(
                resolvedBuffer,
                resolvedMemory,
                "RendererCompatibility.RawBuffer");
        }

        internal void TrackLiveBuffer(Buffer buffer, string owner = "Buffer.Allocation")
            => ResourceRuntime.Buffers.TrackLive(buffer, owner);

        /// <summary>
        /// Associates an allocator-backed buffer created by a target driver with the
        /// renderer's tracked destruction path. The live handle must already have
        /// been registered through <see cref="TrackLiveBuffer(Buffer, string)"/>.
        /// </summary>
        internal void TrackExternalBufferAllocation(Buffer buffer, in VulkanMemoryAllocation allocation)
            => ResourceRuntime.Buffers.TrackExternalAllocation(buffer, allocation);

        /// <summary>
        /// Releases memory that no live buffer allocation owns. Raw <c>vkFreeMemory</c>
        /// is only safe for the legacy allocator; allocator-backed modes must skip
        /// unknown handles because they may belong to shared native allocator blocks.
        /// </summary>
        internal bool FreeUntrackedBufferMemory(DeviceMemory memory, string owner)
            => ResourceRuntime.Buffers.FreeUntrackedMemory(
                BackendObjectContext,
                memory,
                owner);

        public void FlushBuffer(
            Buffer? vkBuffer,
            DeviceMemory? vkMemory,
            ulong offset,
            ulong length)
        {
            if (vkBuffer is null || vkMemory is null)
                throw new ArgumentNullException(vkBuffer is null ? nameof(vkBuffer) : nameof(vkMemory));
            ResourceRuntime.Buffers.Flush(
                BackendObjectContext,
                vkBuffer.Value,
                vkMemory.Value,
                offset,
                length);
        }

        internal void InvalidateBuffer(DeviceMemory? vkMemory, ulong offset, ulong length)
        {
            if (vkMemory is null)
                throw new ArgumentNullException(nameof(vkMemory));
            ResourceRuntime.Buffers.Invalidate(
                BackendObjectContext,
                vkMemory.Value,
                offset,
                length);
        }

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

    }
}

