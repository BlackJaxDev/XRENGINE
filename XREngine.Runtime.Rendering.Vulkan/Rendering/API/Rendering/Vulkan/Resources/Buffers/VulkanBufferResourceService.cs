using Silk.NET.Vulkan;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Data;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns buffer allocation lookup and CPU mapping semantics for one logical-device
/// resource lifetime.  It intentionally shares the allocation registries held by
/// <see cref="VulkanAllocationAuthority"/>; wrappers never create shadow maps.
/// </summary>
internal unsafe sealed class VulkanBufferResourceService(VulkanAllocationAuthority allocations)
{
    private VulkanLifetimeAuthority? _lifetime;
    private int _frameSlot;

    internal int CurrentFrameSlot => Volatile.Read(ref _frameSlot);
    private static readonly HashSet<string> SceneDatabaseDeviceAddressBuffers = new(StringComparer.Ordinal)
    {
        "DrawMetadataBuffer", "TransformBuffer", "PrevTransformBuffer", "BoundsBuffer",
        "SkinningPaletteBuffer", "MaterialStateBuffer", "MaterialTable", "MaterialTextureHandleTable",
    };

    internal bool ShouldEnableDeviceAddress(VulkanBackendObjectContext context, XRDataBuffer buffer)
        => context.Supports(EVulkanDeviceCapability.BufferDeviceAddress) && IsSceneDatabaseDeviceAddressCandidate(buffer);

    internal string ResolveDeviceAddressStatus(VulkanBackendObjectContext context, XRDataBuffer buffer, ulong address)
    {
        if (!IsSceneDatabaseDeviceAddressCandidate(buffer))
            return "not-scene-database-buffer";
        if (!context.Supports(EVulkanDeviceCapability.BufferDeviceAddress))
            return "fallback-descriptor-buffer-device-address-unsupported";
        return address != 0 ? "resolved-device-address" : "fallback-descriptor-address-unresolved";
    }

    /// <summary>
    /// Records whether a scene-database consumer was able to use the buffer device address.
    /// The resource service owns this policy because eligibility is a property of the buffer
    /// resource, rather than of the renderer path that happened to consume it.
    /// </summary>
    internal void RecordDeviceAddressConsumer(
        VulkanBackendObjectContext context,
        XRDataBuffer buffer,
        ulong resolvedAddress,
        string consumer,
        bool consumed,
        string reason)
    {
        string status = ResolveDeviceAddressStatus(context, buffer, resolvedAddress);
        XRBufferWriteTelemetry.RecordDeviceAddressConsumer(consumed);
        if (consumed)
        {
            if (RenderDiagnosticsFlags.UploadStageLogging ||
                RuntimeEngine.EffectiveSettings.EnableGpuIndirectDebugLogging)
            {
                Debug.Vulkan(
                    "[VkSceneDatabaseBDA] consumer={0} buffer='{1}' status={2} address=0x{3:X} bytes={4} reason={5}.",
                    consumer,
                    buffer.AttributeName,
                    status,
                    resolvedAddress,
                    buffer.Length,
                    reason);
            }

            return;
        }

        Debug.VulkanWarningEvery(
            $"VkSceneDatabaseBDA.Fallback.{consumer}.{buffer.AttributeName}.{reason}",
            TimeSpan.FromSeconds(2),
            "[VkSceneDatabaseBDA] consumer={0} buffer='{1}' consumed=false status={2} reason={3} supportsBda={4} bytes={5}.",
            consumer,
            buffer.AttributeName,
            status,
            reason,
            context.Supports(EVulkanDeviceCapability.BufferDeviceAddress),
            buffer.Length);
    }

    internal static bool IsSceneDatabaseDeviceAddressCandidate(XRDataBuffer buffer)
        => buffer.Target == EBufferTarget.ShaderStorageBuffer && SceneDatabaseDeviceAddressBuffers.Contains(buffer.AttributeName);

    internal void BindLifetime(VulkanLifetimeAuthority lifetime)
        => _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));

    internal void PublishFrameSlot(int frameSlot)
    {
        VulkanLifetimeAuthority lifetime = RequireLifetime();
        if ((uint)frameSlot >= (uint)lifetime.Retirement.BufferViews.Length)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
        Volatile.Write(ref _frameSlot, frameSlot);
    }

    internal void RegisterBufferView(VulkanBackendObjectContext context, BufferView bufferView, in BufferViewCreateInfo createInfo, string owner)
    {
        if (bufferView.Handle == 0)
            return;
        context.Resources.Descriptors.DescriptorHeapBufferViewCreateInfos[bufferView.Handle] = createInfo with { PNext = null };
        VulkanResourceLifetimeTracker tracker = RequireLifetime().Tracker;
        tracker.RegisterResource(new VulkanResourceLifetimeKey(ObjectType.BufferView, bufferView.Handle), owner, externallyOwned: false);
        lock (tracker.SyncRoot)
            tracker.BufferViewBackingBuffers[bufferView.Handle] = createInfo.Buffer.Handle;
    }

    internal void RetireBufferView(VulkanBackendObjectContext context, BufferView bufferView, string owner)
    {
        if (bufferView.Handle == 0)
            return;
        context.Resources.Descriptors.DescriptorHeapBufferViewCreateInfos.TryRemove(bufferView.Handle, out _);
        VulkanLifetimeAuthority lifetime = RequireLifetime();
        VulkanResourceLifetimeKey key = new(ObjectType.BufferView, bufferView.Handle);
        VulkanRetirementTicket ticket = CaptureTicket(lifetime, key, owner);
        int frameSlot = Volatile.Read(ref _frameSlot);
        lock (lifetime.Retirement.SyncRoot)
            VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(frameSlot, bufferView.Handle,
                new RetiredBufferView(bufferView, ticket), lifetime.Retirement.BufferViews,
                lifetime.Retirement.BufferViewHandles, lifetime.Retirement.AllBufferViewHandles);
    }

    /// <summary>
    /// Queues a buffer allocation for destruction after the current frame slot's
    /// submitted work has completed.  Upload publication uses this resource-owned
    /// path so it does not need a renderer callback to retire staging storage.
    /// </summary>
    internal void Retire(Buffer buffer, DeviceMemory memory, string owner)
    {
        if (buffer.Handle == 0 && memory.Handle == 0)
            return;

        VulkanLifetimeAuthority lifetime = RequireLifetime();
        VulkanRetirementTicket ticket = CaptureTicket(
            lifetime,
            new VulkanResourceLifetimeKey(ObjectType.Buffer, buffer.Handle),
            owner);
        if (buffer.Handle == 0 && memory.Handle != 0)
            ticket = ticket.Merge(lifetime.Tracker.CaptureRetirementWatermark());

        int frameSlot = Volatile.Read(ref _frameSlot);
        lock (lifetime.Retirement.SyncRoot)
        {
            if (buffer.Handle != 0 && !lifetime.Retirement.AllBufferHandles.Add(buffer.Handle))
                return;

            if (buffer.Handle != 0 && !lifetime.Retirement.BufferHandles[frameSlot].Add(buffer.Handle))
                buffer = default;

            if (memory.Handle != 0)
            {
                if (!lifetime.Retirement.AllMemoryHandles.Add(memory.Handle))
                    memory = default;
                else
                    lifetime.Retirement.MemoryHandles[frameSlot].Add(memory.Handle);
            }

            if (buffer.Handle != 0 || memory.Handle != 0)
                lifetime.Retirement.Buffers[frameSlot].Add(new RetiredBuffer(buffer, memory, ticket));
        }
    }

    internal bool TryGetAllocation(Buffer buffer, out VulkanMemoryAllocation allocation)
    {
        if (buffer.Handle != 0 && allocations.Buffers.Allocations.TryGetValue(buffer.Handle, out allocation))
            return true;
        if (buffer.Handle != 0 && allocations.Buffers.LegacyAllocations.TryGetValue(buffer.Handle, out allocation))
            return true;

        allocation = VulkanMemoryAllocation.Null;
        return false;
    }

    internal ulong GetAllocationOffset(Buffer buffer)
        => TryGetAllocation(buffer, out VulkanMemoryAllocation allocation) ? allocation.Offset : 0;

    internal bool TryMap(
        VulkanBackendObjectContext context,
        Buffer buffer,
        DeviceMemory memory,
        ulong offset,
        ulong length,
        out void* mapped)
    {
        mapped = null;
        if (!context.IsDeviceOperational)
            return false;

        if (TryGetAllocation(buffer, out VulkanMemoryAllocation allocation))
            return RequireAllocator().TryMap(context.Api, context.Device, allocation, offset, Math.Max(length, 1UL), out mapped, out _);

        void* local = null;
        Result result = context.Api.MapMemory(context.Device, memory, offset, Math.Max(length, 1UL), 0, &local);
        mapped = local;
        return result == Result.Success;
    }

    internal void Unmap(VulkanBackendObjectContext context, Buffer buffer, DeviceMemory memory)
    {
        if (TryGetAllocation(buffer, out VulkanMemoryAllocation allocation))
        {
            RequireAllocator().Unmap(context.Api, context.Device, allocation);
            return;
        }

        context.Api.UnmapMemory(context.Device, memory);
    }

    /// <summary>
    /// Flushes an allocation-aligned range. Vulkan requires non-coherent writes to
    /// be aligned to <c>nonCoherentAtomSize</c>; the allocation bounds prevent a
    /// suballocation flush from spilling into an adjacent resource.
    /// </summary>
    internal void Flush(VulkanBackendObjectContext context, Buffer buffer, DeviceMemory memory, ulong offset, ulong length)
    {
        if (length == 0 || (TryGetAllocation(buffer, out VulkanMemoryAllocation allocation) && allocation.IsCoherent))
            return;

        ulong atomSize = context.DeviceContext.NonCoherentAtomSize;
        atomSize = atomSize == 0 ? 1UL : atomSize;
        ulong flushOffset = offset / atomSize * atomSize;
        ulong flushEnd = AlignUp(offset + length, atomSize);
        if (allocation.Memory.Handle != 0)
        {
            ulong allocationStart = allocation.BlockId == -1 ? 0UL : allocation.Offset;
            ulong allocationEnd = allocationStart + allocation.Size;
            flushOffset = Math.Max(flushOffset, allocationStart);
            flushEnd = Math.Min(flushEnd, allocationEnd);
        }

        MappedMemoryRange range = new()
        {
            SType = StructureType.MappedMemoryRange,
            Memory = memory,
            Offset = flushOffset,
            Size = flushEnd > flushOffset ? flushEnd - flushOffset : Vk.WholeSize,
        };
        if (context.Api.FlushMappedMemoryRanges(context.Device, 1, ref range) != Result.Success)
            throw new InvalidOperationException("Failed to flush Vulkan buffer memory.");
    }

    internal void Invalidate(VulkanBackendObjectContext context, DeviceMemory memory, ulong offset, ulong length)
    {
        if (length == 0 || TryGetMemoryAllocation(memory, offset, out VulkanMemoryAllocation allocation) && allocation.IsCoherent)
            return;

        NormalizeRange(context, memory, offset, length, in allocation, out ulong rangeOffset, out ulong rangeSize);
        MappedMemoryRange range = new()
        {
            SType = StructureType.MappedMemoryRange,
            Memory = memory,
            Offset = rangeOffset,
            Size = rangeSize,
        };
        if (context.Api.InvalidateMappedMemoryRanges(context.Device, 1, ref range) != Result.Success)
            throw new InvalidOperationException("Failed to invalidate Vulkan buffer memory.");
    }

    internal bool TryMapReadback(
        VulkanBackendObjectContext context,
        Buffer buffer,
        DeviceMemory memory,
        ulong offset,
        ulong length,
        out void* mapped)
    {
        mapped = null;
        ulong mappedLength = Math.Max(length, 1UL);
        ulong memoryOffset = GetAllocationOffset(buffer) + offset;
        if (TryGetAllocation(buffer, out VulkanMemoryAllocation allocation))
        {
            ulong allocationStart = allocation.BlockId == -1 ? 0UL : allocation.Offset;
            ulong allocationEnd = allocationStart + allocation.Size;
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

        if (!TryMap(context, buffer, memory, offset, mappedLength, out mapped))
            return false;

        Invalidate(context, memory, memoryOffset, mappedLength);
        RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
        RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(
            checked((long)Math.Min(length, mappedLength)));
        return true;
    }

    internal ulong GetDeviceAddress(VulkanBackendObjectContext context, Buffer buffer)
    {
        if (!context.Supports(EVulkanDeviceCapability.BufferDeviceAddress) || buffer.Handle == 0)
            return 0;

        BufferDeviceAddressInfo info = new() { SType = StructureType.BufferDeviceAddressInfo, Buffer = buffer };
        return context.Api.GetBufferDeviceAddress(context.Device, &info);
    }

    /// <summary>
    /// Creates a tracked buffer without routing wrapper allocation through the
    /// renderer facade.  This deliberately does not acquire the legacy staging
    /// pool: callers that need pooled staging use the dedicated upload authority.
    /// </summary>
    internal (Buffer buffer, DeviceMemory memory) Create(
        VulkanBackendObjectContext context,
        ulong size,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        VoidPtr data = default,
        bool enableDeviceAddress = false,
        string owner = "BackendObject.Buffer")
    {
        (Buffer buffer, DeviceMemory memory) = CreateRaw(
            context, size, usage, properties, enableDeviceAddress, owner);
        if (data.Pointer is null || size == 0)
            return (buffer, memory);

        try
        {
            if (!TryMap(context, buffer, memory, 0, size, out void* mapped))
                throw new InvalidOperationException("Failed to map Vulkan buffer memory.");
            try
            {
                Unsafe.CopyBlock(mapped, data.Pointer, checked((uint)size));
                Flush(context, buffer, memory, GetAllocationOffset(buffer), size);
            }
            finally
            {
                Unmap(context, buffer, memory);
            }
            return (buffer, memory);
        }
        catch
        {
            DestroyUnpublished(context, buffer, memory);
            throw;
        }
    }

    /// <summary>Creates and binds an allocator-backed buffer for a backend wrapper.</summary>
    internal (Buffer buffer, DeviceMemory memory) CreateRaw(
        VulkanBackendObjectContext context,
        ulong size,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        bool enableDeviceAddress = false,
        string owner = "BackendObject.Buffer")
    {
        if (!context.IsDeviceOperational)
            throw new InvalidOperationException("Cannot create a Vulkan buffer while the device is not operational.");

        size = Math.Max(size, 1UL);
        if (enableDeviceAddress)
            usage |= BufferUsageFlags.ShaderDeviceAddressBit;

        BufferCreateInfo createInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        if (context.Api.CreateBuffer(context.Device, ref createInfo, null, out Buffer buffer) != Result.Success)
            throw new InvalidOperationException("Failed to create Vulkan buffer.");

        VulkanMemoryAllocation allocation = default;
        try
        {
            allocation = Allocate(context, buffer, properties);
            allocations.Buffers.Allocations[buffer.Handle] = allocation;
            Result bind = context.Api.BindBufferMemory(context.Device, buffer, allocation.Memory, allocation.Offset);
            if (bind != Result.Success)
                throw new InvalidOperationException($"Failed to bind Vulkan buffer memory ({bind}).");

            allocations.Buffers.LiveHandles[buffer.Handle] = 0;
            RequireLifetime().Tracker.RegisterResource(
                new VulkanResourceLifetimeKey(ObjectType.Buffer, buffer.Handle), owner, externallyOwned: false);
            return (buffer, allocation.Memory);
        }
        catch
        {
            allocations.Buffers.Allocations.TryRemove(buffer.Handle, out _);
            if (allocation.Memory.Handle != 0)
                RequireAllocator().Free(context.Api, context.Device, allocation);
            context.Api.DestroyBuffer(context.Device, buffer, null);
            throw;
        }
    }

    /// <summary>
    /// Creates a buffer backed by one dedicated Vulkan memory allocation.  This
    /// path is intentionally independent of the configured block allocator so a
    /// persistently mapped owner can keep a stable allocation until retirement.
    /// </summary>
    internal (Buffer buffer, DeviceMemory memory) CreateDedicatedRaw(
        VulkanBackendObjectContext context,
        ulong size,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        bool enableDeviceAddress = false,
        string owner = "BackendObject.DedicatedBuffer")
    {
        if (!context.IsDeviceOperational)
            throw new InvalidOperationException("Cannot create a Vulkan buffer while the device is not operational.");

        size = Math.Max(size, 1UL);
        if (enableDeviceAddress)
            usage |= BufferUsageFlags.ShaderDeviceAddressBit;

        BufferCreateInfo createInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        if (context.Api.CreateBuffer(context.Device, ref createInfo, null, out Buffer buffer) != Result.Success)
            throw new InvalidOperationException("Failed to create a dedicated Vulkan buffer.");

        DeviceMemory memory = default;
        try
        {
            MemoryRequirements requirements = context.Api.GetBufferMemoryRequirements(context.Device, buffer);
            uint memoryTypeIndex = ResolveMemoryType(context, requirements.MemoryTypeBits, properties);
            MemoryAllocateFlagsInfo addressFlags = new()
            {
                SType = StructureType.MemoryAllocateFlagsInfo,
                Flags = MemoryAllocateFlags.DeviceAddressBit,
            };
            MemoryAllocateInfo allocationInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryTypeIndex,
                PNext = enableDeviceAddress ? &addressFlags : null,
            };
            Result allocationResult = context.Api.AllocateMemory(context.Device, ref allocationInfo, null, out memory);
            if (allocationResult != Result.Success)
                throw new VulkanOutOfMemoryException(
                    $"Dedicated Vulkan buffer allocation failed ({allocationResult}). Requested={properties}",
                    properties);

            VulkanMemoryAllocation allocation = new(
                memory,
                0,
                requirements.Size,
                memoryTypeIndex,
                properties,
                -1);
            if (context.Api.BindBufferMemory(context.Device, buffer, memory, 0) != Result.Success)
                throw new InvalidOperationException("Failed to bind dedicated Vulkan buffer memory.");

            allocations.Buffers.LegacyAllocations[buffer.Handle] = allocation;
            TrackLive(buffer, owner);
            RecordAllocationTelemetry(properties, checked((long)requirements.Size));
            return (buffer, memory);
        }
        catch
        {
            allocations.Buffers.LegacyAllocations.TryRemove(buffer.Handle, out _);
            if (memory.Handle != 0)
                context.Api.FreeMemory(context.Device, memory, null);
            context.Api.DestroyBuffer(context.Device, buffer, null);
            throw;
        }
    }

    internal void TrackLive(Buffer buffer, string owner = "Buffer.Allocation")
    {
        if (buffer.Handle == 0)
            return;

        allocations.Buffers.LiveHandles[buffer.Handle] = 0;
        RequireLifetime().Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.Buffer, buffer.Handle), owner, externallyOwned: false);
    }

    internal void TrackExternalAllocation(Buffer buffer, in VulkanMemoryAllocation allocation)
    {
        if (buffer.Handle == 0)
            throw new ArgumentException("A tracked buffer allocation requires a live Vulkan buffer.", nameof(buffer));
        if (allocation.Memory.Handle == 0)
            throw new ArgumentException("A tracked buffer allocation requires bound Vulkan memory.", nameof(allocation));

        allocations.Buffers.Allocations[buffer.Handle] = allocation;
    }

    /// <summary>
    /// Releases memory which no published buffer allocation owns. Unknown raw
    /// memory is freed only when the configured allocator uses one Vulkan memory
    /// object per resource; block allocators may share the handle.
    /// </summary>
    internal bool FreeUntrackedMemory(
        VulkanBackendObjectContext context,
        DeviceMemory memory,
        string owner)
    {
        foreach (KeyValuePair<ulong, VulkanMemoryAllocation> pair in allocations.Buffers.LegacyAllocations.ToArray())
        {
            if (pair.Value.Memory.Handle != memory.Handle)
                continue;
            if (!allocations.Buffers.LegacyAllocations.TryRemove(pair.Key, out VulkanMemoryAllocation allocation))
                return false;

            if (allocation.Memory.Handle != 0)
                context.Api.FreeMemory(context.Device, allocation.Memory, null);
            return true;
        }

        IVulkanMemoryAllocator allocator = RequireAllocator();
        if (allocator is VulkanLegacyAllocator)
        {
            context.Api.FreeMemory(context.Device, memory, null);
            return true;
        }

        Debug.VulkanWarningEvery(
            $"Vulkan.BufferMemory.SkipUnknownRawFree.{GetHashCode()}.{owner}.{memory.Handle}",
            TimeSpan.FromSeconds(5),
            "[Vulkan] Skipping raw vkFreeMemory for untracked buffer memory 0x{0:X} in {1}; current allocator is {2}, so the handle may be allocator-owned shared memory.",
            memory.Handle,
            owner,
            allocator.GetType().Name);
        return false;
    }

    internal void DestroyRemainingTrackedAllocations(VulkanBackendObjectContext context)
    {
        foreach (KeyValuePair<ulong, VulkanMemoryAllocation> pair in allocations.Buffers.Allocations.ToArray())
        {
            if (!allocations.Buffers.Allocations.TryRemove(pair.Key, out VulkanMemoryAllocation allocation))
                continue;

            Buffer buffer = new() { Handle = pair.Key };
            if (TryBeginImmediateDestroy(context, buffer, "DestroyRemainingTrackedBufferAllocations"))
                context.Api.DestroyBuffer(context.Device, buffer, null);
            RequireAllocator().Free(context.Api, context.Device, allocation);
        }

        foreach (KeyValuePair<ulong, VulkanMemoryAllocation> pair in allocations.Buffers.LegacyAllocations.ToArray())
        {
            if (!allocations.Buffers.LegacyAllocations.TryRemove(pair.Key, out VulkanMemoryAllocation allocation))
                continue;

            Buffer buffer = new() { Handle = pair.Key };
            if (TryBeginImmediateDestroy(context, buffer, "DestroyRemainingTrackedLegacyBufferAllocations"))
                context.Api.DestroyBuffer(context.Device, buffer, null);
            if (allocation.Memory.Handle != 0)
                context.Api.FreeMemory(context.Device, allocation.Memory, null);
        }
    }

    private bool TryBeginImmediateDestroy(
        VulkanBackendObjectContext context,
        Buffer buffer,
        string owner)
    {
        if (buffer.Handle == 0)
            return false;

        VulkanLifetimeAuthority lifetime = RequireLifetime();
        VulkanRetirementTicket ticket = CaptureTicket(
            lifetime,
            new VulkanResourceLifetimeKey(ObjectType.Buffer, buffer.Handle),
            owner);
        if (!lifetime.Tracker.IsRetirementReady(ticket))
            return false;
        if (allocations.Buffers.LiveHandles.TryRemove(buffer.Handle, out _))
        {
            context.Resources.CompleteResourceDestruction(ObjectType.Buffer, buffer.Handle);
            return true;
        }

        Debug.VulkanWarningEvery(
            $"Vulkan.Buffer.SkipStaleDestroy.{GetHashCode()}.{owner}.{buffer.Handle}",
            TimeSpan.FromSeconds(5),
            "[Vulkan] Skipping stale destroy for buffer 0x{0:X} in {1}; the handle is not live in resource tracking.",
            buffer.Handle,
            owner);
        return false;
    }

    /// <summary>Queues a wrapper-owned buffer for safe destruction after in-flight work completes.</summary>
    internal void Destroy(VulkanBackendObjectContext context, Buffer buffer, DeviceMemory memory, string owner)
        => Retire(buffer, memory, owner);

    internal void Update(
        VulkanBackendObjectContext context,
        Buffer buffer,
        DeviceMemory memory,
        ulong offset,
        ulong length,
        void* source)
    {
        if (source is null || length == 0 || !context.IsDeviceOperational)
            return;
        if (!TryMap(context, buffer, memory, offset, length, out void* mapped))
            throw new InvalidOperationException("Failed to map Vulkan buffer memory.");
        try
        {
            Unsafe.CopyBlock(mapped, source, checked((uint)length));
            Flush(context, buffer, memory, GetAllocationOffset(buffer) + offset, length);
        }
        finally
        {
            Unmap(context, buffer, memory);
        }
    }

    internal bool CanUseNvIndirectCopyUploads(VulkanBackendObjectContext context)
        // The feature remains intentionally disabled until the indirect-copy upload
        // authority owns its command-buffer protocol.
        => false;

    internal static bool IsDeviceLocalVramAllocation(MemoryPropertyFlags properties)
        => (properties & MemoryPropertyFlags.DeviceLocalBit) != 0 &&
           (properties & MemoryPropertyFlags.HostVisibleBit) == 0;

    private bool TryGetMemoryAllocation(DeviceMemory memory, ulong offset, out VulkanMemoryAllocation allocation)
    {
        foreach (VulkanMemoryAllocation candidate in allocations.Buffers.Allocations.Values)
            if (Matches(candidate, memory, offset))
            {
                allocation = candidate;
                return true;
            }
        foreach (VulkanMemoryAllocation candidate in allocations.Buffers.LegacyAllocations.Values)
            if (Matches(candidate, memory, offset))
            {
                allocation = candidate;
                return true;
            }
        foreach (VulkanMemoryAllocation candidate in allocations.Images.Allocations.Values)
            if (Matches(candidate, memory, offset))
            {
                allocation = candidate;
                return true;
            }

        allocation = default;
        return false;
    }

    private void NormalizeRange(VulkanBackendObjectContext context, DeviceMemory memory, ulong offset, ulong length, in VulkanMemoryAllocation allocation, out ulong rangeOffset, out ulong rangeSize)
    {
        ulong atomSize = context.DeviceContext.NonCoherentAtomSize;
        atomSize = atomSize == 0 ? 1UL : atomSize;
        rangeOffset = offset / atomSize * atomSize;
        ulong rangeEnd = AlignUp(offset + length, atomSize);
        if (allocation.Memory.Handle != 0)
        {
            ulong start = allocation.BlockId == -1 ? 0UL : allocation.Offset;
            rangeOffset = Math.Max(rangeOffset, start);
            rangeEnd = Math.Min(rangeEnd, start + allocation.Size);
        }
        rangeSize = rangeEnd > rangeOffset ? rangeEnd - rangeOffset : Vk.WholeSize;
    }

    private static bool Matches(in VulkanMemoryAllocation allocation, DeviceMemory memory, ulong offset)
        => allocation.Memory.Handle == memory.Handle &&
           (allocation.BlockId == -1 || (offset >= allocation.Offset && offset < allocation.Offset + allocation.Size));

    private IVulkanMemoryAllocator RequireAllocator()
        => allocations.Buffers.MemoryAllocator
            ?? throw new InvalidOperationException("The Vulkan memory allocator has not been initialized.");

    private static uint ResolveMemoryType(
        VulkanBackendObjectContext context,
        uint typeFilter,
        MemoryPropertyFlags properties)
    {
        if (context.DeviceContext.TryFindMemoryType(context.Api, typeFilter, properties, out uint exactIndex))
            return exactIndex;

        MemoryPropertyFlags readbackPreference = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit;
        if ((properties & readbackPreference) == readbackPreference &&
            context.DeviceContext.TryFindMemoryType(
                context.Api,
                typeFilter,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out uint coherentIndex))
        {
            Debug.VulkanWarningEvery(
                "Vulkan.ReadbackMemoryTypeFallback",
                TimeSpan.FromSeconds(10),
                "[Vulkan] Host-cached readback memory unavailable; falling back to host-coherent staging memory.");
            return coherentIndex;
        }

        return context.DeviceContext.FindMemoryType(context.Api, typeFilter, properties);
    }

    private static void RecordAllocationTelemetry(MemoryPropertyFlags properties, long bytes)
    {
        if ((properties & MemoryPropertyFlags.DeviceLocalBit) != 0 &&
            (properties & MemoryPropertyFlags.HostVisibleBit) == 0)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAllocation(
                RuntimeEngine.Rendering.Stats.Vulkan.EVulkanAllocationTelemetryClass.DeviceLocal,
                bytes);
            return;
        }

        if ((properties & MemoryPropertyFlags.HostVisibleBit) != 0 &&
            (properties & MemoryPropertyFlags.HostCachedBit) != 0)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAllocation(
                RuntimeEngine.Rendering.Stats.Vulkan.EVulkanAllocationTelemetryClass.Readback,
                bytes);
            return;
        }

        if ((properties & MemoryPropertyFlags.HostVisibleBit) != 0)
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAllocation(
                RuntimeEngine.Rendering.Stats.Vulkan.EVulkanAllocationTelemetryClass.Upload,
                bytes);
    }

    private VulkanLifetimeAuthority RequireLifetime()
        => _lifetime ?? throw new InvalidOperationException("Vulkan buffer resource lifetime has not been bound.");

    private static VulkanRetirementTicket CaptureTicket(VulkanLifetimeAuthority lifetime, VulkanResourceLifetimeKey key, string owner)
    {
        lifetime.Tracker.FenceResourceRecordingAdmission(key, owner);
        lock (lifetime.Tracker.SyncRoot)
        {
            VulkanResourceLifetimeRecord record = lifetime.Tracker.GetOrRegisterResourceNoLock(key, owner);
            if ((record.State & (EVulkanResourceLifetimeState.Destroyed | EVulkanResourceLifetimeState.PendingRetirement)) != 0)
                return record.RetirementTicket;
            VulkanRetirementTicket ticket = new(record.Pins.LastGraphicsSequence, record.Pins.LastTransferSequence,
                record.Pins.LastOtherSequence, Stopwatch.GetTimestamp(), record.Generation,
                (record.State & EVulkanResourceLifetimeState.External) != 0,
                VulkanRetirementPinSet.Single(key, record.Generation));
            record.RetirementSerial = unchecked((ulong)Interlocked.Increment(ref lifetime.Tracker.RetirementSerial));
            record.State |= EVulkanResourceLifetimeState.PendingRetirement;
            record.RetirementTicket = ticket;
            lifetime.Tracker.PublishedResourceGenerations[key] = 0;
            return ticket;
        }
    }

    private static ulong AlignUp(ulong value, ulong alignment)
        => alignment <= 1 ? value : (value + alignment - 1UL) / alignment * alignment;

    private VulkanMemoryAllocation Allocate(
        VulkanBackendObjectContext context,
        Buffer buffer,
        MemoryPropertyFlags requiredProperties)
    {
        IVulkanMemoryAllocator allocator = RequireAllocator();
        if (allocator.TryAllocateForBuffer(context.Api, context.Device, buffer, requiredProperties, out VulkanMemoryAllocation allocation, out _))
            return allocation;

        if (requiredProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit) &&
            allocator.TryAllocateForBuffer(
                context.Api,
                context.Device,
                buffer,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out allocation,
                out _))
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanOomFallback();
            return allocation;
        }

        throw new VulkanOutOfMemoryException($"Vulkan buffer allocation failed. Requested={requiredProperties}", requiredProperties);
    }

    internal void DestroyUnpublished(VulkanBackendObjectContext context, Buffer buffer, DeviceMemory memory)
    {
        if (buffer.Handle != 0)
        {
            allocations.Buffers.Allocations.TryRemove(buffer.Handle, out VulkanMemoryAllocation allocation);
            allocations.Buffers.LiveHandles.TryRemove(buffer.Handle, out _);
            context.Api.DestroyBuffer(context.Device, buffer, null);
            if (allocation.Memory.Handle != 0)
                RequireAllocator().Free(context.Api, context.Device, allocation);
            else if (memory.Handle != 0)
                context.Api.FreeMemory(context.Device, memory, null);
        }
    }
}
