using System;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	private int UniformBufferSlotCount => Math.Max(_uniformDrawSlotCapacity, 1);

	private int UniformBufferFrameCount => Math.Max(BackendContext.Resources.Descriptors.FrameSlotCount, 1);

	private int UniformBufferArrayLength => UniformBufferFrameCount * UniformBufferSlotCount;

	internal void EnsureUniformDrawSlotCapacity(int requiredSlots)
	{
		requiredSlots = Math.Max(requiredSlots, 1);
		if (requiredSlots <= _uniformDrawSlotCapacity)
			return;

		int newCapacity = Math.Max(_uniformDrawSlotCapacity, 1);
		while (newCapacity < requiredSlots)
			newCapacity <<= 1;

		// Reserve logical draw slots only. The renderer owns Vulkan storage in
		// frame arenas. A new slot must not change recorded descriptor storage.
		_uniformDrawSlotCapacity = newCapacity;
	}

	private int ResolveUniformBufferIndex(int frameIndex, int drawUniformSlot, int bufferCount)
	{
		if (bufferCount <= 1)
			return 0;

		int frame = Math.Clamp(frameIndex, 0, UniformBufferFrameCount - 1);
		int slot = Math.Clamp(drawUniformSlot, 0, UniformBufferSlotCount - 1);
		int index = frame * UniformBufferSlotCount + slot;
		return Math.Clamp(index, 0, bufferCount - 1);
	}

	/// <summary>
	/// Ensures per-frame/per-draw-slot engine uniform buffers exist and are large enough.
	/// Destroys and recreates if the frame count, slot count, or size has changed.
	/// </summary>
	private bool EnsureEngineUniformBuffer(string name, uint size)
	{
		size = Math.Max(size, 1u);
		int bufferCount = UniformBufferArrayLength;
		bool useFrameArena = BackendContext.Resources.MappedFrameArena is not null &&
			!string.Equals(name, FallbackDescriptorUniformName, StringComparison.Ordinal);
		if (_engineUniformBuffers.TryGetValue(name, out EngineUniformBuffer[]? existing))
		{
			bool valid = EngineUniformBuffersValid(name, existing, bufferCount, size) &&
				(!useFrameArena || !existing[0].OwnsBuffer);
			if (valid)
				return true;

			DestroyEngineUniformBufferArray(existing);
			_engineUniformBuffers.Remove(name);
			_engineUniformBufferCertificates.Remove(name);
		}

		if (useFrameArena)
		{
			if (!TryCreateEngineUniformArenaViews(name, size, out EngineUniformBuffer[] arenaBuffers))
				return false;
			_engineUniformBuffers[name] = arenaBuffers;
			EngineUniformBuffersValid(name, arenaBuffers, bufferCount, size);
			return true;
		}

		EngineUniformBuffer[] buffers = new EngineUniformBuffer[bufferCount];
		BufferUsageFlags usage = string.Equals(name, FallbackDescriptorUniformName, StringComparison.Ordinal)
			? BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit
			: BufferUsageFlags.UniformBufferBit;
		ulong stride = ResolveUniformBufferStride(size);
		if (!TryComputeUniformBufferByteSize(stride, bufferCount, out ulong totalSize) ||
			!CreateHostVisibleBuffer(totalSize, usage, out var buffer, out var memory))
		{
			return false;
		}

		if (!BackendContext.Resources.Buffers.TryCreateMappedSlice(BackendContext, buffer, memory, 0, totalSize, out VulkanMappedMemorySlice mappedSlice))
		{
			BackendContext.Resources.Buffers.Destroy(BackendContext, buffer, memory, "VkMeshRenderer.UniformBuffer");
			return false;
		}

		for (int i = 0; i < bufferCount; i++)
		{
			ulong offset = stride * (ulong)i;
			if (!BackendContext.Resources.Buffers.TryCreateMappedSlice(BackendContext, buffer, memory, offset, size, out VulkanMappedMemorySlice slotSlice))
			{
				BackendContext.Resources.Buffers.Destroy(BackendContext, buffer, memory, "VkMeshRenderer.UniformBuffer");
				return false;
			}
			buffers[i] = new EngineUniformBuffer(buffer, memory, size, offset, ownsBuffer: i == 0, mappedMemorySlice: slotSlice);
		}

		_engineUniformBuffers[name] = buffers;
		EngineUniformBuffersValid(name, buffers, bufferCount, size);
		return true;
	}

	/// <summary>
	/// Ensures per-frame/per-draw-slot auto uniform buffers exist and are large enough.
	/// Destroys and recreates if the frame count, slot count, or size has changed.
	/// </summary>
	private bool EnsureAutoUniformBuffer(string name, uint size)
	{
		size = Math.Max(size, 1u);
		int bufferCount = UniformBufferArrayLength;
		bool useFrameArena = BackendContext.Resources.MappedFrameArena is not null;
		if (_autoUniformBuffers.TryGetValue(name, out AutoUniformBuffer[]? existing))
		{
			bool frequencyOwnedArena =
				useFrameArena &&
				_program is not null &&
				_program.TryGetAutoUniformBlock(
					name,
					out AutoUniformBlockInfo existingBlock) &&
				existingBlock.Frequency != EVulkanBindingFrequency.Unknown;
			bool valid = AutoUniformBuffersValid(
					name,
					existing,
					bufferCount,
					size,
					requireMappedPointers: !frequencyOwnedArena) &&
				(!useFrameArena || !existing[0].OwnsBuffer);
			if (valid)
				return true;

			DestroyAutoUniformBufferArray(existing);
			_autoUniformBuffers.Remove(name);
			_autoUniformBufferCertificates.Remove(name);
			_autoUniformOwnerSlotTables.Remove(name);
			_publishedAutoUniformMaterialWritePlans.Remove(name);
		}

		if (useFrameArena)
		{
			if (!TryCreateAutoUniformArenaViews(name, size, out AutoUniformBuffer[] arenaBuffers))
				return false;
			_autoUniformBuffers[name] = arenaBuffers;
			AutoUniformBuffersValid(name, arenaBuffers, bufferCount, size, requireMappedPointers: true);
			return true;
		}

		AutoUniformBuffer[] buffers = new AutoUniformBuffer[bufferCount];
		ulong stride = ResolveUniformBufferStride(size);
		if (!TryComputeUniformBufferByteSize(stride, bufferCount, out ulong totalSize) ||
			!CreateHostVisibleBuffer(totalSize, BufferUsageFlags.UniformBufferBit, out var buffer, out var memory))
		{
			return false;
		}

		if (!BackendContext.Resources.Buffers.TryCreateMappedSlice(BackendContext, buffer, memory, 0, totalSize, out VulkanMappedMemorySlice mappedSlice))
		{
			BackendContext.Resources.Buffers.Destroy(BackendContext, buffer, memory, "VkMeshRenderer.UniformBuffer");
			return false;
		}

		for (int i = 0; i < bufferCount; i++)
		{
			ulong offset = stride * (ulong)i;
			if (!BackendContext.Resources.Buffers.TryCreateMappedSlice(BackendContext, buffer, memory, offset, size, out VulkanMappedMemorySlice slotSlice))
			{
				BackendContext.Resources.Buffers.Destroy(BackendContext, buffer, memory, "VkMeshRenderer.UniformBuffer");
				return false;
			}
			buffers[i] = new AutoUniformBuffer(buffer, memory, size, offset, ownsBuffer: i == 0, mappedMemorySlice: slotSlice);
		}

		_autoUniformBuffers[name] = buffers;
		AutoUniformBuffersValid(name, buffers, bufferCount, size, requireMappedPointers: true);
		return true;
	}

	private bool TryCreateEngineUniformArenaViews(string name, uint size, out EngineUniformBuffer[] buffers)
	{
		buffers = new EngineUniformBuffer[UniformBufferArrayLength];
		VulkanMappedFrameArena? arena = BackendContext.Resources.MappedFrameArena;
		if (arena is null)
			return false;
		for (int drawSlot = 0; drawSlot < UniformBufferSlotCount; drawSlot++)
		{
			if (!arena.TryReserve(this, name, isAutoUniform: false, drawSlot, size, out VulkanMappedFrameReservation reservation))
				return false;

			for (int frame = 0; frame < UniformBufferFrameCount; frame++)
			{
				if (!arena.TryGetSlice(frame, reservation, out VulkanMappedFrameSlice slice))
					return false;
				int index = frame * UniformBufferSlotCount + drawSlot;
				buffers[index] = new EngineUniformBuffer(
					 slice.Buffer, slice.Memory, size, offset: slice.Offset,
					ownsBuffer: false, mappedSlice: slice);
			}
		}
		return true;
	}

	private bool TryCreateAutoUniformArenaViews(string name, uint size, out AutoUniformBuffer[] buffers)
	{
		buffers = new AutoUniformBuffer[UniformBufferArrayLength];
		VulkanMappedFrameArena? arena = BackendContext.Resources.MappedFrameArena;
		if (arena is null)
			return false;
		if (_program is not null &&
			_program.TryGetAutoUniformBlock(
				name,
				out AutoUniformBlockInfo frequencyBlock) &&
			frequencyBlock.Frequency != EVulkanBindingFrequency.Unknown)
		{
			for (int frame = 0; frame < UniformBufferFrameCount; frame++)
			{
				if (!arena.TryGetSlice(frame, offset: 0, length: size, out VulkanMappedFrameSlice slice))
				{
					_lastDescriptorPreparationFailure =
						$"frequency auto-uniform slice rejected for '{name}': {arena.DescribeSliceRejection(frame, 0, size)}";
					return false;
				}

				for (int drawSlot = 0;
					 drawSlot < UniformBufferSlotCount;
					 drawSlot++)
				{
					int index =
						frame * UniformBufferSlotCount + drawSlot;
					buffers[index] = new AutoUniformBuffer(
						slice.Buffer,
						slice.Memory,
						size,
						offset: 0,
						ownsBuffer: false,
						mappedSlice: slice);
				}
			}

			return true;
		}

		for (int drawSlot = 0; drawSlot < UniformBufferSlotCount; drawSlot++)
		{
			if (!arena.TryReserve(this, name, isAutoUniform: true, drawSlot, size, out VulkanMappedFrameReservation reservation))
				return false;

			for (int frame = 0; frame < UniformBufferFrameCount; frame++)
			{
				if (!arena.TryGetSlice(frame, reservation, out VulkanMappedFrameSlice slice))
					return false;
				int index = frame * UniformBufferSlotCount + drawSlot;
				buffers[index] = new AutoUniformBuffer(
					slice.Buffer, slice.Memory, size, offset: slice.Offset,
					ownsBuffer: false, mappedSlice: slice);
			}
		}
		return true;
	}

	private ulong ResolveUniformBufferStride(uint size)
	{
		ulong alignment = Math.Max(BackendContext.DeviceContext.MinUniformBufferOffsetAlignment, 1UL);
		ulong value = Math.Max(size, 1u);
		ulong remainder = value % alignment;
		return remainder == 0 ? value : value + alignment - remainder;
	}

	private static bool TryComputeUniformBufferByteSize(ulong stride, int bufferCount, out ulong totalSize)
	{
		totalSize = 0;
		if (bufferCount <= 0 || stride == 0)
			return false;

		ulong count = (ulong)bufferCount;
		if (stride > ulong.MaxValue / count)
			return false;

		totalSize = stride * count;
		return true;
	}

	/// <summary>
	/// Allocates a host-visible, host-coherent Vulkan buffer with the given usage flags.
	/// The buffer uses a mapped write lease for later updates.
	/// </summary>
	private bool CreateHostVisibleBuffer(ulong size, BufferUsageFlags usage, out Silk.NET.Vulkan.Buffer buffer, out DeviceMemory memory)
	{
		buffer = default;
		memory = default;
		size = Math.Max(size, 1UL);
		bool enableDeviceAddress = BackendContext.Resources.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap;
		if (enableDeviceAddress)
			usage |= BufferUsageFlags.ShaderDeviceAddressBit;

		MemoryPropertyFlags props = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
		try
		{
			(buffer, memory) = BackendContext.Resources.Buffers.CreateRaw(BackendContext, size, usage, props, enableDeviceAddress, "VkMeshRenderer.UniformBuffer");
			return true;
		}
		catch (Exception ex)
		{
			WarnOnce($"Failed to create engine uniform buffer '{size}' bytes: {ex.Message}");
			buffer = default;
			memory = default;
			return false;
		}
	}
}
