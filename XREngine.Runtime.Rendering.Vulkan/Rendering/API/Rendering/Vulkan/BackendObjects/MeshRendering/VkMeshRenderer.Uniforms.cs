// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.Uniforms.cs  – partial class: Uniform Buffer Management
//
// Allocates per-frame host-visible UBOs for engine and auto uniform blocks,
// writes typed values (scalars, vectors, matrices) into mapped buffer memory,
// and uploads legacy per-binding engine uniforms to Vulkan descriptor buffers.
// ──────────────────────────────────────────────────────────────────────────────

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

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
	private static readonly ConcurrentDictionary<(string Prefix, string Field), string> StructUniformFieldNames = new();
	private static readonly ConcurrentDictionary<(string Prefix, uint Index), string> IndexedUniformNames = new();

	#region Uniform Buffer Allocation

	private int UniformBufferSlotCount => Math.Max(_uniformDrawSlotCapacity, 1);

	private int UniformBufferFrameCount => Math.Max(BackendContext.Descriptors.FrameSlotCount, 1);

	private int UniformBufferArrayLength => UniformBufferFrameCount * UniformBufferSlotCount;

	internal void EnsureUniformDrawSlotCapacity(int requiredSlots)
	{
		requiredSlots = Math.Max(requiredSlots, 1);
		if (requiredSlots <= _uniformDrawSlotCapacity)
			return;

		int newCapacity = Math.Max(_uniformDrawSlotCapacity, 1);
		while (newCapacity < requiredSlots)
			newCapacity <<= 1;

		// This is a CPU-side logical reservation only. Vulkan storage lives in the
		// renderer-owned frame arenas and descriptor capacity no longer scales with draw
		// slots, so discovering another use cannot invalidate an already-recorded output.
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
		bool useFrameArena = Renderer.MeshFrameDataArenaEnabled &&
			!string.Equals(name, FallbackDescriptorUniformName, StringComparison.Ordinal);
		if (_engineUniformBuffers.TryGetValue(name, out EngineUniformBuffer[]? existing))
		{
			bool valid = EngineUniformBuffersValid(existing, bufferCount, size) &&
				(!useFrameArena || !existing[0].OwnsBuffer);
			if (valid)
				return true;

			DestroyEngineUniformBufferArray(existing);
			_engineUniformBuffers.Remove(name);
		}

		if (useFrameArena)
		{
			if (!TryCreateEngineUniformArenaViews(name, size, out EngineUniformBuffer[] arenaBuffers))
				return false;
			_engineUniformBuffers[name] = arenaBuffers;
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

		if (!Renderer.TryMapBufferMemory(buffer, memory, 0, totalSize, out void* mappedPtr))
		{
			Renderer.DestroyTrackedMeshUniformBuffer(buffer, memory);
			return false;
		}

		for (int i = 0; i < bufferCount; i++)
		{
			ulong offset = stride * (ulong)i;
			void* slotPtr = (byte*)mappedPtr + checked((nint)offset);
			buffers[i] = new EngineUniformBuffer(buffer, memory, size, slotPtr, offset, ownsBuffer: i == 0);
		}

		_engineUniformBuffers[name] = buffers;
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
		bool useFrameArena = Renderer.MeshFrameDataArenaEnabled;
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
					existing,
					bufferCount,
					size,
					requireMappedPointers: !frequencyOwnedArena) &&
				(!useFrameArena || !existing[0].OwnsBuffer);
			if (valid)
				return true;

			DestroyAutoUniformBufferArray(existing);
			_autoUniformBuffers.Remove(name);
			_autoUniformOwnerSlotTables.Remove(name);
			_publishedAutoUniformMaterialWritePlans.Remove(name);
		}

		if (useFrameArena)
		{
			if (!TryCreateAutoUniformArenaViews(name, size, out AutoUniformBuffer[] arenaBuffers))
				return false;
			_autoUniformBuffers[name] = arenaBuffers;
			return true;
		}

		AutoUniformBuffer[] buffers = new AutoUniformBuffer[bufferCount];
		ulong stride = ResolveUniformBufferStride(size);
		if (!TryComputeUniformBufferByteSize(stride, bufferCount, out ulong totalSize) ||
			!CreateHostVisibleBuffer(totalSize, BufferUsageFlags.UniformBufferBit, out var buffer, out var memory))
		{
			return false;
		}

		if (!Renderer.TryMapBufferMemory(buffer, memory, 0, totalSize, out void* mappedPtr))
		{
			Renderer.DestroyTrackedMeshUniformBuffer(buffer, memory);
			return false;
		}

		for (int i = 0; i < bufferCount; i++)
		{
			ulong offset = stride * (ulong)i;
			void* slotPtr = (byte*)mappedPtr + checked((nint)offset);
			buffers[i] = new AutoUniformBuffer(buffer, memory, size, slotPtr, offset, ownsBuffer: i == 0);
		}

		_autoUniformBuffers[name] = buffers;
		return true;
	}

	private bool TryCreateEngineUniformArenaViews(string name, uint size, out EngineUniformBuffer[] buffers)
	{
		buffers = new EngineUniformBuffer[UniformBufferArrayLength];
		for (int drawSlot = 0; drawSlot < UniformBufferSlotCount; drawSlot++)
		{
			if (!Renderer.TryReserveMeshFrameDataRange(this, name, isAutoUniform: false, drawSlot, size, out ulong offset))
				return false;

			for (int frame = 0; frame < UniformBufferFrameCount; frame++)
			{
				if (!Renderer.TryGetMeshFrameDataArenaRange(frame, offset, size, out var buffer, out var memory, out void* mappedPtr))
					return false;
				int index = frame * UniformBufferSlotCount + drawSlot;
				buffers[index] = new EngineUniformBuffer(buffer, memory, size, mappedPtr, offset, ownsBuffer: false);
			}
		}
		return true;
	}

	private bool TryCreateAutoUniformArenaViews(string name, uint size, out AutoUniformBuffer[] buffers)
	{
		buffers = new AutoUniformBuffer[UniformBufferArrayLength];
		if (_program is not null &&
			_program.TryGetAutoUniformBlock(
				name,
				out AutoUniformBlockInfo frequencyBlock) &&
			frequencyBlock.Frequency != EVulkanBindingFrequency.Unknown)
		{
			for (int frame = 0; frame < UniformBufferFrameCount; frame++)
			{
				if (!Renderer.TryGetMeshFrameDataArenaRange(
						frame,
						offset: 0,
						size,
						out var buffer,
						out var memory,
						out _))
				{
					return false;
				}

				for (int drawSlot = 0;
					 drawSlot < UniformBufferSlotCount;
					 drawSlot++)
				{
					int index =
						frame * UniformBufferSlotCount + drawSlot;
					buffers[index] = new AutoUniformBuffer(
						buffer,
						memory,
						size,
						mappedPtr: null,
						offset: 0,
						ownsBuffer: false);
				}
			}

			return true;
		}

		for (int drawSlot = 0; drawSlot < UniformBufferSlotCount; drawSlot++)
		{
			if (!Renderer.TryReserveMeshFrameDataRange(this, name, isAutoUniform: true, drawSlot, size, out ulong offset))
				return false;

			for (int frame = 0; frame < UniformBufferFrameCount; frame++)
			{
				if (!Renderer.TryGetMeshFrameDataArenaRange(frame, offset, size, out var buffer, out var memory, out void* mappedPtr))
					return false;
				int index = frame * UniformBufferSlotCount + drawSlot;
				buffers[index] = new AutoUniformBuffer(buffer, memory, size, mappedPtr, offset, ownsBuffer: false);
			}
		}
		return true;
	}

	private ulong ResolveUniformBufferStride(uint size)
	{
		ulong alignment = Math.Max(Renderer._uniformBufferOffsetAlignment, 1UL);
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

	private static bool EngineUniformBuffersValid(EngineUniformBuffer[] buffers, int expectedCount, uint requiredSize)
	{
		if (buffers.Length != expectedCount)
			return false;

		for (int i = 0; i < buffers.Length; i++)
		{
			if (buffers[i].Buffer.Handle == 0 || buffers[i].MappedPtr == null || buffers[i].Size < requiredSize)
				return false;
		}

		return true;
	}

	private static bool AutoUniformBuffersValid(
		AutoUniformBuffer[] buffers,
		int expectedCount,
		uint requiredSize,
		bool requireMappedPointers)
	{
		if (buffers.Length != expectedCount)
			return false;

		for (int i = 0; i < buffers.Length; i++)
		{
			if (buffers[i].Buffer.Handle == 0 ||
				(requireMappedPointers && buffers[i].MappedPtr == null) ||
				buffers[i].Size < requiredSize)
				return false;
		}

		return true;
	}

	/// <summary>
	/// Allocates a host-visible, host-coherent Vulkan buffer with the given usage flags.
	/// Used for engine and auto uniform buffers that are updated every frame via map/unmap.
	/// </summary>
	private bool CreateHostVisibleBuffer(ulong size, BufferUsageFlags usage, out Silk.NET.Vulkan.Buffer buffer, out DeviceMemory memory)
	{
		buffer = default;
		memory = default;
		size = Math.Max(size, 1UL);
		bool enableDeviceAddress = Renderer.IsDescriptorHeapDrawBindingActive;
		if (enableDeviceAddress)
			usage |= BufferUsageFlags.ShaderDeviceAddressBit;

		MemoryPropertyFlags props = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
		try
		{
			(buffer, memory) = Renderer.CreateBufferRaw(size, usage, props, enableDeviceAddress);
			Renderer.TrackMeshUniformBuffer(buffer, memory);
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

	#endregion // Uniform Buffer Allocation

	#region Uniform Buffer Updates

	/// <summary>
	/// Writes engine-uniform data into all active engine UBOs for the current frame.
	/// Called once per draw before descriptor binding.
	/// </summary>
	private void UpdateEngineUniformBuffersForDraw(int frameIndex, int drawUniformSlot, in PendingMeshDraw draw)
	{
		// Capture value-only CPU-direct state in the same bounded frame/timeline slot as
		// the UBOs. A later pass-aware capture refines the conservative pass bit.
		Renderer.TryCaptureCpuDirectDynamicData(this, frameIndex, drawUniformSlot, draw, passMask: 1u);

		if (_engineUniformBuffers.Count == 0)
			return;

		foreach (var pair in _engineUniformBuffers)
		{
			EngineUniformBuffer[] buffers = pair.Value;
			if (buffers.Length == 0)
				continue;

			int idx = ResolveUniformBufferIndex(frameIndex, drawUniformSlot, buffers.Length);
			EngineUniformBuffer buffer = buffers[idx];
			if (buffer.Buffer.Handle == 0)
				continue;

			TryWriteEngineUniform(pair.Key, draw, buffer);
		}
	}

	/// <summary>
	/// Writes auto-uniform block data into all active auto UBOs for the current frame.
	/// Auto uniforms are populated from engine state, program overrides, and material parameters.
	/// </summary>
	private void UpdateAutoUniformBuffersForDraw(
		int frameIndex,
		int drawUniformSlot,
		XRMaterial material,
		in PendingMeshDraw draw,
		EVulkanBindingFrequencyMask frequencyMask =
			EVulkanBindingFrequencyMask.All)
	{
		if (_program is null || _autoUniformBuffers.Count == 0)
		{
			LogGizmoAutoUniformBlocks(material, skipped: true);
			return;
		}

		LogGizmoAutoUniformBlocks(material, skipped: false);

		foreach (KeyValuePair<string, AutoUniformBlockInfo> pair in _program.AutoUniformBlockMap)
		{
			string name = pair.Key;
			AutoUniformBlockInfo block = pair.Value;
			if (!IncludesBindingFrequency(
					frequencyMask,
					block.Frequency))
				continue;
			if (!_autoUniformBuffers.TryGetValue(name, out AutoUniformBuffer[]? buffers) || buffers.Length == 0)
				continue;

			int idx = ResolveFrequencyOwnedAutoUniformBufferIndex(
				block,
				frameIndex,
				drawUniformSlot,
				material,
				draw,
				buffers.Length,
				out ulong ownerIdentity);
			VulkanFrequencyAutoUniformReservation? frequencyReservation = null;
			if (block.Frequency != EVulkanBindingFrequency.Unknown &&
				Renderer.MeshFrameDataArenaEnabled)
			{
				if (!Renderer.TryGetOrReserveFrequencyAutoUniformRange(
						_program,
						block,
						ownerIdentity,
						out frequencyReservation) ||
					!Renderer.TryGetMeshFrameDataArenaRange(
						frameIndex,
						frequencyReservation.Offset,
						block.Size,
						out var sharedBuffer,
						out var sharedMemory,
						out void* sharedMappedPtr))
				{
					RuntimeEngine.Rendering.Stats.Vulkan
						.RecordVulkanDynamicUniformExhaustion();
					continue;
				}

				buffers[idx] = new AutoUniformBuffer(
					sharedBuffer,
					sharedMemory,
					block.Size,
					sharedMappedPtr,
					frequencyReservation.Offset,
					ownsBuffer: false);
			}
			AutoUniformBuffer buffer = buffers[idx];
			if (buffer.Buffer.Handle == 0)
				continue;

			TryWriteAutoUniformBlock(
				block,
				buffer,
				idx,
				buffers.Length,
				frameIndex,
				material,
				draw,
				frequencyReservation);
		}
	}

	/// <summary>
	/// Clears an auto uniform buffer and writes each member from engine state,
	/// program overrides, and material parameters.
	/// </summary>
	private bool TryWriteAutoUniformBlock(
		AutoUniformBlockInfo block,
		AutoUniformBuffer buffer,
		int bufferIndex,
		int bufferCount,
		int frameIndex,
		XRMaterial material,
		in PendingMeshDraw draw,
		VulkanFrequencyAutoUniformReservation? frequencyReservation)
	{
		if (buffer.MappedPtr == null)
			return false;

		Span<byte> data = new(buffer.MappedPtr, (int)buffer.Size);
		EVulkanAutoUniformFallbackReason fallbackReason =
			EVulkanAutoUniformFallbackReason.BindingSnapshotIneligible;
		bool materialOwned =
			block.Frequency is EVulkanBindingFrequency.Unknown or
				EVulkanBindingFrequency.Material;
		ComputeDispatchSnapshot? bindingSnapshot =
			draw.ProgramBindingSnapshot;
		bool bindingSnapshotEligible =
			IsMaterialBindingSnapshotEligible(
				materialOwned,
				draw.ShadowUniformState.IsShadowPass,
				bindingSnapshot);
		if (bindingSnapshotEligible &&
			TryGetAutoUniformMaterialWritePlan(
				block,
				buffer.Size,
				material,
				bindingSnapshot,
				out AutoUniformMaterialWritePlan? plan,
				out fallbackReason) &&
			plan is not null)
		{
			int staticBytesCopied = 0;
			VulkanAutoUniformPublicationState[] publicationStates;
			int publicationStateIndex;
			if (frequencyReservation is not null)
			{
				publicationStates =
					frequencyReservation.PublicationStates;
				publicationStateIndex = Math.Clamp(
					frameIndex,
					0,
					publicationStates.Length - 1);
			}
			else
			{
				if (!_publishedAutoUniformMaterialWritePlans.TryGetValue(
						block.InstanceName,
						out publicationStates!) ||
					publicationStates.Length != bufferCount)
				{
					publicationStates =
						new VulkanAutoUniformPublicationState[bufferCount];
					_publishedAutoUniformMaterialWritePlans[
						block.InstanceName] = publicationStates;
				}
				publicationStateIndex = bufferIndex;
			}

			ref VulkanAutoUniformPublicationState publicationState =
				ref publicationStates[publicationStateIndex];
			bool hasMaterialOwnedStorage =
				block.Frequency is EVulkanBindingFrequency.Unknown or
					EVulkanBindingFrequency.Material;
			if (!publicationState.IsPlanPublished(plan))
			{
				if (hasMaterialOwnedStorage)
				{
					plan.StaticBytes.AsSpan().CopyTo(data);
					staticBytesCopied = plan.StaticBytes.Length;
				}
				publicationState.PublishPlan(plan);
			}
			if (hasMaterialOwnedStorage)
			{
				RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformFrequencyPublication(
					(int)EVulkanBindingFrequency.Material,
					published: staticBytesCopied > 0,
					staticBytesCopied);
			}

			int dynamicBytesCleared = 0;
			int dynamicOperationsWritten = 0;
			for (EVulkanBindingFrequency frequency =
					EVulkanBindingFrequency.Frame;
				 frequency < EVulkanBindingFrequency.Count;
				 frequency++)
			{
				VulkanAutoUniformFrequencyPlan frequencyPlan =
					plan.GetFrequencyPlan(frequency);
				ReadOnlySpan<VulkanAutoUniformBindingOperation> operations =
					frequencyPlan.Operations;
				if (operations.IsEmpty)
					continue;

				ulong generation = ComputeAutoUniformFrequencyGeneration(
					frequency,
					plan,
					draw);
				ReadOnlySpan<VulkanAutoUniformDirtyRange> dirtyRanges =
					frequencyPlan.DirtyRanges;
				if (!publicationState.TryBeginFrequencyPublication(
						frequency,
						generation,
						dirtyRanges,
						buffer.Size))
				{
					RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformFrequencyPublication(
						(int)frequency,
						published: false,
						publishedBytes: 0);
					continue;
				}

				int publishedBytes = 0;
				for (int rangeIndex = 0;
					 rangeIndex <
						publicationState.PendingDirtyRangeCount;
					 rangeIndex++)
				{
					VulkanAutoUniformDirtyRange dirtyRange =
						publicationState.GetPendingDirtyRange(
							rangeIndex);
					data.Slice(
						checked((int)dirtyRange.Offset),
						checked((int)dirtyRange.Size)).Clear();
					dynamicBytesCleared += checked((int)dirtyRange.Size);
					publishedBytes += checked((int)dirtyRange.Size);
				}

				for (int operationIndex = 0;
					 operationIndex < operations.Length;
					 operationIndex++)
				{
					VulkanAutoUniformBindingOperation operation =
						operations[operationIndex];
					if (!TryWriteAutoUniformOperation(
							data,
							operation,
							material,
							draw,
							out EVulkanAutoUniformFallbackReason operationFallbackReason))
					{
						WarnAutoUniformFallback(
							block,
							operationFallbackReason,
							operation.Member.Name);
						publicationState.Invalidate();
						RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformFallbackReason(
							operationFallbackReason);
						return WriteLegacyAutoUniformBlock(
							data,
							block,
							buffer.Size,
							material,
							draw);
					}
					dynamicOperationsWritten++;
				}

				publicationState.CompleteFrequencyPublication(
					frequency,
					generation);
				RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformFrequencyPublication(
					(int)frequency,
					published: true,
					publishedBytes);
			}
			if (!ValidateAutoUniformPayloadParity(
					data,
					block,
					buffer.Size,
					plan,
					material,
					draw,
					ref publicationState))
			{
				return true;
			}

			RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformFastWrite(
				staticBytesCopied,
				dynamicBytesCleared,
				dynamicOperationsWritten);

			return true;
		}

		RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformFallbackReason(
			fallbackReason);
		return WriteLegacyAutoUniformBlock(data, block, buffer.Size, material, draw);
	}

	private bool ValidateAutoUniformPayloadParity(
		Span<byte> packed,
		AutoUniformBlockInfo block,
		uint bufferSize,
		AutoUniformMaterialWritePlan plan,
		XRMaterial material,
		in PendingMeshDraw draw,
		ref VulkanAutoUniformPublicationState publicationState)
	{
		if (!XREnvironment.IsEnabled(
				XREngineEnvironmentVariables.VulkanAutoUniformParity))
		{
			return true;
		}

		byte[] rented = ArrayPool<byte>.Shared.Rent(
			checked((int)bufferSize));
		try
		{
			Span<byte> legacy = rented.AsSpan(0, checked((int)bufferSize));
			legacy.Clear();
			WriteLegacyAutoUniformBlockData(
				legacy,
				block,
				bufferSize,
				material,
				draw);
			if (!VulkanAutoUniformParityValidator.TryFindMismatch(
					legacy,
					packed,
					plan.Schema,
					out VulkanAutoUniformParityMismatch mismatch))
			{
				return true;
			}

			Debug.VulkanWarning(
				"[Vulkan.AutoUniformParity] mesh='{0}' program='{1}' " +
				"block='{2}' frequency={3} entry='{4}' offset={5} " +
				"legacy=0x{6:X2} packed=0x{7:X2}. " +
				"Using authoritative legacy bytes.",
				Mesh?.Name ?? "<unnamed>",
				_program?.Data?.Name ?? "<unavailable>",
				block.InstanceName,
				mismatch.Frequency,
				mismatch.SchemaEntry,
				mismatch.ByteOffset,
				mismatch.LegacyValue,
				mismatch.PackedValue);
			legacy.CopyTo(packed);
			publicationState.Invalidate();
			RuntimeEngine.Rendering.Stats.Vulkan
				.RecordVulkanAutoUniformFallbackReason(
					EVulkanAutoUniformFallbackReason.BindingSchemaMismatch);
			RuntimeEngine.Rendering.Stats.Vulkan
				.RecordVulkanAutoUniformLegacyWrite(
					checked((int)bufferSize),
					block.Members.Count);
			return false;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented);
		}
	}

	private bool WriteLegacyAutoUniformBlock(
		Span<byte> data,
		AutoUniformBlockInfo block,
		uint bufferSize,
		XRMaterial material,
		in PendingMeshDraw draw)
	{
		data.Clear();
		RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformLegacyWrite(
			(int)bufferSize,
			block.Members.Count);
		WriteLegacyAutoUniformBlockData(data, block, bufferSize, material, draw);
		return true;
	}

	private void WriteLegacyAutoUniformBlockData(
		Span<byte> data,
		AutoUniformBlockInfo block,
		uint bufferSize,
		XRMaterial material,
		in PendingMeshDraw draw)
	{
		for (int memberIndex = 0; memberIndex < block.Members.Count; memberIndex++)
		{
			AutoUniformMember member = block.Members[memberIndex];
			if (member.Offset > bufferSize ||
				member.Size > bufferSize - member.Offset)
				continue;

			TryWriteAutoUniformMember(data, member, material, draw);
		}
	}

	/// <summary>
	/// Compiles reflected members into a material byte template plus the small
	/// set of values that genuinely depend on draw or render-scope state. This
	/// removes the steady-state per-draw material name lookup and type dispatch
	/// without allowing callbacks or shadow bindings into the fast path.
	/// </summary>
	private bool TryGetAutoUniformMaterialWritePlan(
		AutoUniformBlockInfo block,
		uint bufferSize,
		XRMaterial material,
		ComputeDispatchSnapshot? bindingSnapshot,
		out AutoUniformMaterialWritePlan? plan,
		out EVulkanAutoUniformFallbackReason fallbackReason)
	{
		plan = null;
		if (_program is null)
		{
			fallbackReason = EVulkanAutoUniformFallbackReason.ProgramUnavailable;
			return false;
		}

		if (bufferSize == 0 || bufferSize > int.MaxValue)
		{
			fallbackReason = EVulkanAutoUniformFallbackReason.InvalidBufferSize;
			return false;
		}

		if (_program.BindingSchema is not { } programSchema ||
			!programSchema.TryGetAutoUniformBlock(
				block.InstanceName,
				out VulkanAutoUniformBindingSchema schema))
		{
			fallbackReason = EVulkanAutoUniformFallbackReason.BindingSchemaUnavailable;
			return false;
		}

		if (!schema.IsFastPathEligible)
		{
			fallbackReason = schema.FallbackKind;
			WarnAutoUniformFallback(
				block,
				fallbackReason,
				schema.FallbackReason);
			return false;
		}

		if (!ReferenceEquals(schema.Block, block) ||
			schema.Block.Size != bufferSize)
		{
			fallbackReason = EVulkanAutoUniformFallbackReason.BindingSchemaMismatch;
			return false;
		}

		fallbackReason = EVulkanAutoUniformFallbackReason.None;
		string blockName = block.InstanceName;
		ulong linkGeneration = _program.LinkGeneration;
		bool materialOwned =
			block.Frequency is EVulkanBindingFrequency.Unknown or
				EVulkanBindingFrequency.Material;
		ulong runtimeUniformNameSignature =
			materialOwned
				? bindingSnapshot?.RuntimeUniformNameSignature ?? 0UL
				: 0UL;
		ulong runtimeUniformPublicationLayoutSignature =
			materialOwned
				? bindingSnapshot
					?.RuntimeUniformPublicationLayoutSignature ?? 0UL
				: 0UL;
		ulong publicationLayoutSignature =
			schema.PublicationLayoutSignature;
		AutoUniformMaterialWritePlanCacheKey materialPlanKey = new(
			publicationLayoutSignature,
			material,
			runtimeUniformNameSignature,
			runtimeUniformPublicationLayoutSignature);
		VkMaterial? materialPlanOwner = materialOwned
			? Renderer.GetOrCreateAPIRenderObject(
				material,
				generateNow: true) as VkMaterial
			: null;
		bool planCacheHit = materialPlanOwner is not null
			? materialPlanOwner.TryGetAutoUniformMaterialWritePlan(
				materialPlanKey,
				out AutoUniformMaterialWritePlan? cached)
			: _program.TryGetAutoUniformMaterialWritePlan(
				blockName,
				publicationLayoutSignature,
				material,
				runtimeUniformNameSignature,
				runtimeUniformPublicationLayoutSignature,
				materialOwned,
				out cached);
		if (planCacheHit &&
			cached is not null &&
			cached.PublicationLayoutSignature ==
				publicationLayoutSignature &&
			(!materialOwned ||
			 (cached.MaterialLayoutVersion ==
					material.BindingLayoutVersion &&
			  cached.MaterialValueVersion ==
					material.BindingValueVersion)) &&
			cached.RuntimeUniformNameSignature ==
				runtimeUniformNameSignature &&
			cached.RuntimeUniformPublicationLayoutSignature ==
				runtimeUniformPublicationLayoutSignature &&
			cached.StaticBytes.Length == (int)bufferSize)
		{
			RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformPlanLookup(hit: true);
			plan = cached;
			return true;
		}
		RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformPlanLookup(hit: false);
		if (XREnvironment.IsEnabled(
				XREngineEnvironmentVariables.VulkanFrameDataReuseDiag))
		{
			Debug.VulkanEvery(
				$"Vulkan.AutoUniformPlanCacheMiss.{material.ID}.{blockName}",
				TimeSpan.FromSeconds(1),
				"[Vulkan.AutoUniformPlanCacheMiss] material='{0}' id={1} " +
				"block='{2}' program={3}/{4} layout={5} value={6} " +
				"runtimeNames=0x{7:X16} runtimeLayout=0x{8:X16}.",
				material.Name ?? "<unnamed>",
				material.ID,
				blockName,
				_program.BindingId,
				linkGeneration,
				material.BindingLayoutVersion,
				material.BindingValueVersion,
				runtimeUniformNameSignature,
				runtimeUniformPublicationLayoutSignature);
		}

		byte[] staticBytes = new byte[(int)bufferSize];
		List<VulkanAutoUniformBindingOperation> dynamicOperations = [];
		VulkanAutoUniformBindingOperation[] operations = schema.Operations;
		for (int operationIndex = 0; operationIndex < operations.Length; operationIndex++)
		{
			VulkanAutoUniformBindingOperation operation = operations[operationIndex];
			bool hasRuntimeOverride =
				operation.SourceKind ==
					EVulkanAutoUniformSourceKind.MaterialOrRuntime &&
				bindingSnapshot?.HasRuntimeUniform(
					operation.Member.Name) == true;
			if (operation.SourceKind ==
					EVulkanAutoUniformSourceKind.MaterialOrRuntime &&
				!hasRuntimeOverride)
			{
				AutoUniformMember member = operation.Member;
				ShaderVar? materialParameter =
					material.Parameter<ShaderVar>(member.Name);
				bool hasDeclaredDefault =
					VulkanAutoUniformBindingSchema.HasExplicitDefault(
						member);
				if (materialParameter is null && !hasDeclaredDefault)
				{
					// Loose GLSL uniforms default to zero. Preserve that
					// contract after rewriting them into a UBO instead of
					// treating an omitted optional material parameter as a
					// per-draw runtime dependency.
					continue;
				}

				if (TryWriteStaticMaterialAutoUniform(
						staticBytes,
						member,
						material))
				{
					continue;
				}

				fallbackReason =
					EVulkanAutoUniformFallbackReason
						.TypedMaterialOrRuntimeWriteFailed;
				return false;
			}

			if (hasRuntimeOverride)
			{
				EVulkanBindingFrequency runtimeFrequency =
					ResolveRuntimeOverrideFrequency(
						block.Frequency,
						bindingSnapshot!,
						operation.Member.Name);
				if (block.Frequency != EVulkanBindingFrequency.Unknown &&
					runtimeFrequency != block.Frequency)
				{
					fallbackReason =
						EVulkanAutoUniformFallbackReason.BindingSchemaMismatch;
					return false;
				}
				dynamicOperations.Add(operation with
				{
					Frequency = runtimeFrequency,
				});
			}
			else
			{
				dynamicOperations.Add(operation);
			}
		}

		plan = new AutoUniformMaterialWritePlan(
			schema,
			material.BindingLayoutVersion,
			material.BindingValueVersion,
			runtimeUniformNameSignature,
			runtimeUniformPublicationLayoutSignature,
			staticBytes,
			[.. dynamicOperations]);
		if (materialPlanOwner is not null)
		{
			materialPlanOwner.CacheAutoUniformMaterialWritePlan(
				materialPlanKey,
				plan);
		}
		else
		{
			_program.CacheAutoUniformMaterialWritePlan(
				blockName,
				publicationLayoutSignature,
				material,
				runtimeUniformNameSignature,
				runtimeUniformPublicationLayoutSignature,
				materialOwned,
				plan);
		}
		return true;
	}

	internal static bool IsMaterialBindingSnapshotEligible(
		bool materialOwned,
		bool shadowPass,
		ComputeDispatchSnapshot? snapshot)
		=> !materialOwned ||
		   (!shadowPass &&
			(snapshot is null ||
			 snapshot.AllowsMaterialBindingFastPath));

	internal static EVulkanBindingFrequency ResolveRuntimeOverrideFrequency(
		EVulkanBindingFrequency blockFrequency,
		ComputeDispatchSnapshot snapshot,
		string uniformName)
	{
		if (snapshot.TryGetRuntimeUniformPublication(
				uniformName,
				out VulkanRuntimeUniformPublication publication))
		{
			return publication.Frequency;
		}

		return snapshot.IsMutableLegacyUniform(uniformName) &&
			   blockFrequency != EVulkanBindingFrequency.Unknown
			? blockFrequency
			: EVulkanBindingFrequency.RuntimeCallback;
	}

	private void WarnAutoUniformFallback(
		AutoUniformBlockInfo block,
		EVulkanAutoUniformFallbackReason reason,
		string? detail)
	{
		if (!_autoUniformWarnings.Add(
				(block.InstanceName, reason, detail)))
		{
			return;
		}

		Debug.VulkanWarning(
			"[Vulkan.AutoUniformFallback] mesh='{0}' program='{1}' block='{2}' " +
			"frequency={3} reason={4} detail='{5}'.",
			Mesh?.Name ?? "<unnamed>",
			_program?.Data?.Name ?? "<unavailable>",
			block.InstanceName,
			block.Frequency,
			reason,
			detail ?? string.Empty);
	}

	private int ResolveFrequencyOwnedAutoUniformBufferIndex(
		AutoUniformBlockInfo block,
		int frameIndex,
		int drawUniformSlot,
		XRMaterial material,
		in PendingMeshDraw draw,
		int bufferCount,
		out ulong ownerIdentity)
	{
		ownerIdentity = 0;
		if (block.Frequency == EVulkanBindingFrequency.Unknown ||
			Renderer.IsDescriptorHeapDrawBindingActive)
		{
			return ResolveUniformBufferIndex(
				frameIndex,
				drawUniformSlot,
				bufferCount);
		}

		VulkanAutoUniformOwnerSlotTable table =
			GetOrCreateAutoUniformOwnerSlotTable(block.InstanceName);
		ownerIdentity = ComputeAutoUniformOwnerIdentity(
			block.Frequency,
			material,
			draw);
		int ownerSlot = table.ResolveAndPublish(
			frameIndex,
			drawUniformSlot,
			ownerIdentity);
		return ResolveUniformBufferIndex(
			frameIndex,
			ownerSlot,
			bufferCount);
	}

	private int ResolvePublishedAutoUniformBufferIndex(
		AutoUniformBlockInfo block,
		int frameIndex,
		int drawUniformSlot,
		int bufferCount)
	{
		if (block.Frequency == EVulkanBindingFrequency.Unknown ||
			Renderer.IsDescriptorHeapDrawBindingActive ||
			!_autoUniformOwnerSlotTables.TryGetValue(
				block.InstanceName,
				out VulkanAutoUniformOwnerSlotTable? table) ||
			table.FrameCount != UniformBufferFrameCount ||
			table.DrawSlotCapacity != UniformBufferSlotCount)
		{
			return ResolveUniformBufferIndex(
				frameIndex,
				drawUniformSlot,
				bufferCount);
		}

		int ownerSlot = table.ResolvePublished(
			frameIndex,
			drawUniformSlot);
		return ResolveUniformBufferIndex(
			frameIndex,
			ownerSlot,
			bufferCount);
	}

	private VulkanAutoUniformOwnerSlotTable GetOrCreateAutoUniformOwnerSlotTable(
		string blockName)
	{
		if (_autoUniformOwnerSlotTables.TryGetValue(
				blockName,
				out VulkanAutoUniformOwnerSlotTable? table) &&
			table.FrameCount == UniformBufferFrameCount &&
			table.DrawSlotCapacity == UniformBufferSlotCount)
		{
			return table;
		}

		table = new VulkanAutoUniformOwnerSlotTable(
			UniformBufferFrameCount,
			UniformBufferSlotCount);
		_autoUniformOwnerSlotTables[blockName] = table;
		return table;
	}

	private ulong ComputeAutoUniformOwnerIdentity(
		EVulkanBindingFrequency frequency,
		XRMaterial material,
		in PendingMeshDraw draw)
	{
		FrameOpSignatureHasher hash = new();
		hash.Add((byte)frequency);
		switch (frequency)
		{
			case EVulkanBindingFrequency.Frame:
				hash.Add(1u);
				break;
			case EVulkanBindingFrequency.View:
				hash.Add(
					draw.Camera is null
						? 0
						: RuntimeHelpers.GetHashCode(draw.Camera));
				hash.Add(
					draw.StereoRightEyeCamera is null
						? 0
						: RuntimeHelpers.GetHashCode(
							draw.StereoRightEyeCamera));
				hash.Add(draw.IsStereoPass);
				hash.Add(draw.UseUnjitteredProjection);
				break;
			case EVulkanBindingFrequency.Pass:
				hash.Add(draw.RenderAreaWidth);
				hash.Add(draw.RenderAreaHeight);
				if (draw.RenderAreaWidth <= 0 ||
					draw.RenderAreaHeight <= 0)
				{
					hash.Add(draw.Viewport.Width);
					hash.Add(MathF.Abs(draw.Viewport.Height));
				}
				hash.Add(unchecked(
					(uint)draw.ShadowUniformState.GetHashCode()));
				break;
			case EVulkanBindingFrequency.Material:
				hash.Add(RuntimeHelpers.GetHashCode(material));
				hash.Add(
					draw.ProgramBindingSnapshot
						?.RuntimeUniformNameSignature ?? 0UL);
				hash.Add(
					draw.ProgramBindingSnapshot
						?.RuntimeUniformPublicationLayoutSignature ?? 0UL);
				break;
			case EVulkanBindingFrequency.Object:
				hash.Add(RuntimeHelpers.GetHashCode(MeshRenderer));
				break;
			case EVulkanBindingFrequency.Instance:
				hash.Add(RuntimeHelpers.GetHashCode(MeshRenderer));
				hash.Add(draw.Instances);
				break;
			case EVulkanBindingFrequency.RuntimeCallback:
				hash.Add(
					draw.ProgramBindingSnapshot is null
						? 0
						: RuntimeHelpers.GetHashCode(
							draw.ProgramBindingSnapshot));
				hash.Add(RuntimeHelpers.GetHashCode(material));
				break;
			default:
				return 0;
		}

		return hash.ToHash();
	}

	internal bool TryGetReusableAutoUniformOwner(
		EVulkanBindingFrequency frequency,
		XRMaterial material,
		in PendingMeshDraw draw,
		out ulong ownerIdentity,
		out ulong publicationLayoutSignature,
		out ulong contentGeneration)
	{
		publicationLayoutSignature =
			_program?.BindingSchema
				?.GetFrequencyPublicationLayoutSignature(frequency) ?? 0UL;
		ownerIdentity = ComputeAutoUniformOwnerIdentity(
			frequency,
			material,
			draw);
		if (ownerIdentity == 0 || publicationLayoutSignature == 0)
		{
			contentGeneration = 0;
			return false;
		}

		ulong materialGeneration =
			frequency == EVulkanBindingFrequency.Material
				? ComputeMaterialPublicationGeneration(
					material.BindingLayoutVersion,
					material.BindingValueVersion,
					draw.ProgramBindingSnapshot
						?.RuntimeUniformNameSignature ?? 0UL,
					draw.ProgramBindingSnapshot
						?.MutableLegacyUniformValueSignature ?? 0UL)
				: 0UL;
		contentGeneration =
			draw.AutoUniformPublication.GetGeneration(
				frequency,
				materialGeneration);
		return true;
	}

	private ulong ComputeAutoUniformFrequencyGeneration(
		EVulkanBindingFrequency frequency,
		AutoUniformMaterialWritePlan plan,
		in PendingMeshDraw draw)
	{
		ulong materialGeneration = 0;
		if (frequency == EVulkanBindingFrequency.Material)
		{
			materialGeneration = ComputeMaterialPublicationGeneration(
				plan.MaterialLayoutVersion,
				plan.MaterialValueVersion,
				plan.RuntimeUniformNameSignature,
				draw.ProgramBindingSnapshot
					?.MutableLegacyUniformValueSignature ?? 0UL);
		}

		ulong contentGeneration =
			draw.AutoUniformPublication.GetGeneration(
			frequency,
			materialGeneration);
		XRMaterial? material =
			draw.MaterialOverride ?? MeshRenderer.Material;
		if (material is null)
			return contentGeneration;

		ulong ownerIdentity = ComputeAutoUniformOwnerIdentity(
			frequency,
			material,
			draw);
		if (ownerIdentity == 0)
			return contentGeneration;

		FrameOpSignatureHasher hash = new();
		hash.Add(ownerIdentity);
		hash.Add(contentGeneration);
		return hash.ToHash();
	}

	internal static ulong ComputeMaterialPublicationGeneration(
		ulong materialLayoutVersion,
		ulong materialValueVersion,
		ulong runtimeUniformNameSignature,
		ulong mutableLegacyUniformValueSignature)
	{
		FrameOpSignatureHasher materialHash = new();
		materialHash.Add(materialLayoutVersion);
		materialHash.Add(materialValueVersion);
		materialHash.Add(runtimeUniformNameSignature);
		materialHash.Add(mutableLegacyUniformValueSignature);
		return materialHash.ToHash();
	}

	private bool TryWriteStaticMaterialAutoUniform(
		Span<byte> data,
		AutoUniformMember member,
		XRMaterial material)
	{
		ShaderVar? parameter = material.Parameter<ShaderVar>(member.Name);
		if (parameter is not null)
		{
			return member.IsArray
				? TryWriteAutoUniformArray(data, member, parameter)
				: TryWriteMaterialUniformValue(data, member, parameter);
		}

		if (member.IsArray && member.DefaultArrayValues is { Count: > 0 })
			return TryWriteAutoUniformArrayDefaults(data, member);

		return member.DefaultValue is { } defaultValue &&
			TryWriteAutoUniformValue(data, member, defaultValue.Value, defaultValue.Type);
	}

	private bool TryWriteAutoUniformOperation(
		Span<byte> data,
		in VulkanAutoUniformBindingOperation operation,
		XRMaterial material,
		in PendingMeshDraw draw,
		out EVulkanAutoUniformFallbackReason fallbackReason)
	{
		RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformTypedOperation();
		AutoUniformMember member = operation.Member;
		switch (operation.SourceKind)
		{
			case EVulkanAutoUniformSourceKind.Engine:
				if (!TryResolveEngineUniformValue(
						operation.EngineUniform,
						draw,
						out EngineUniformValue engineValue,
						out EShaderVarType engineType))
				{
					fallbackReason =
						EVulkanAutoUniformFallbackReason.TypedEngineSourceUnavailable;
					return false;
				}

				bool wroteEngineValue = TryWriteAutoUniformValue(
					data,
					member,
					in engineValue,
					engineType);
				fallbackReason = wroteEngineValue
					? EVulkanAutoUniformFallbackReason.None
					: EVulkanAutoUniformFallbackReason.TypedEngineWriteFailed;
				return wroteEngineValue;

			case EVulkanAutoUniformSourceKind.TemporalViewProjection:
				bool wroteTemporalValue = TryWriteTemporalViewProjectionUniform(
					data,
					member,
					operation.TemporalSource,
					draw);
				fallbackReason = wroteTemporalValue
					? EVulkanAutoUniformFallbackReason.None
					: EVulkanAutoUniformFallbackReason.TypedTemporalWriteFailed;
				return wroteTemporalValue;

			case EVulkanAutoUniformSourceKind.MeshState:
				if (!TryResolveMeshStateUniformValue(
						operation.SpecialSource,
						draw,
						out EngineUniformValue meshValue,
						out EShaderVarType meshType))
				{
					fallbackReason =
						EVulkanAutoUniformFallbackReason.TypedMeshStateSourceUnavailable;
					return false;
				}

				bool wroteMeshValue = TryWriteAutoUniformValue(
					data,
					member,
					in meshValue,
					meshType);
				fallbackReason = wroteMeshValue
					? EVulkanAutoUniformFallbackReason.None
					: EVulkanAutoUniformFallbackReason.TypedMeshStateWriteFailed;
				return wroteMeshValue;

			case EVulkanAutoUniformSourceKind.MaterialOrRuntime:
				bool wroteMaterialOrRuntimeValue = TryWriteMaterialOrRuntimeAutoUniform(
					data,
					member,
					material,
					draw.ProgramBindingSnapshot);
				fallbackReason = wroteMaterialOrRuntimeValue
					? EVulkanAutoUniformFallbackReason.None
					: EVulkanAutoUniformFallbackReason.TypedMaterialOrRuntimeWriteFailed;
				return wroteMaterialOrRuntimeValue;

			case EVulkanAutoUniformSourceKind.StructSnapshot:
				bool wroteStructSnapshot =
					TryWriteStructUniformValue(
						data,
						member,
						member.Name,
						member.Offset,
						draw.ProgramBindingSnapshot);
				fallbackReason = wroteStructSnapshot
					? EVulkanAutoUniformFallbackReason.None
					: EVulkanAutoUniformFallbackReason.StructSnapshotRequired;
				return wroteStructSnapshot;

			default:
				fallbackReason = operation.FallbackKind;
				return false;
		}
	}

	private static bool IncludesBindingFrequency(
		EVulkanBindingFrequencyMask mask,
		EVulkanBindingFrequency frequency)
	{
		if (mask == EVulkanBindingFrequencyMask.All)
			return true;
		if (frequency is <= EVulkanBindingFrequency.Unknown or
			>= EVulkanBindingFrequency.Count)
			return false;

		EVulkanBindingFrequencyMask frequencyBit =
			(EVulkanBindingFrequencyMask)(
				1 << ((int)frequency - 1));
		return (mask & frequencyBit) != 0;
	}

	private bool TryWriteMaterialOrRuntimeAutoUniform(
		Span<byte> data,
		AutoUniformMember member,
		XRMaterial material,
		ComputeDispatchSnapshot? snapshot)
	{
		if (_program is not null &&
			_program.TryGetUniformValue(snapshot, member.Name, out ProgramUniformValue programValue))
		{
			return member.IsArray
				? TryWriteProgramUniformArray(data, member, member.Name, snapshot)
				: TryWriteProgramUniformValue(data, member, programValue);
		}

		if (member.IsArray &&
			TryWriteIndexedProgramUniformArray(data, member, member.Name, snapshot))
		{
			return true;
		}

		ShaderVar? parameter = material.Parameter<ShaderVar>(member.Name);
		if (parameter is not null)
		{
			return member.IsArray
				? TryWriteAutoUniformArray(data, member, parameter)
				: TryWriteMaterialUniformValue(data, member, parameter);
		}

		if (member.IsArray && member.DefaultArrayValues is { Count: > 0 })
			return TryWriteAutoUniformArrayDefaults(data, member);

		return member.DefaultValue is { } defaultValue &&
			TryWriteAutoUniformValue(data, member, defaultValue.Value, defaultValue.Type);
	}

	/// <summary>
	/// Attempts to write a single auto uniform member. Resolution priority:
	/// engine uniform value > program override > material parameter > array defaults > default value.
	/// </summary>
	private bool TryWriteAutoUniformMember(Span<byte> data, AutoUniformMember member, XRMaterial material, in PendingMeshDraw draw)
	{
		RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformReflectedNameLookup();
		if (member.StructMembers is { Count: > 0 })
			return TryWriteStructUniformValue(data, member, member.Name, member.Offset, draw.ProgramBindingSnapshot);

		bool wrote;
		if (TryWriteTemporalViewProjectionUniform(data, member, draw, out wrote))
			return wrote;

		if (TryResolveEngineUniformValue(member.Name, draw, out EngineUniformValue engineValue, out EShaderVarType engineType))
		{
			wrote = TryWriteAutoUniformValue(data, member, in engineValue, engineType);
			if (MaterialBindingDiagnosticsEnabled)
				LogMaterialAutoUniform(member, material, "engine", engineValue.ToDiagnosticObject(engineType), engineType, wrote);
			return wrote;
		}

		if (_program is not null &&
			_program.TryGetUniformValue(
				draw.ProgramBindingSnapshot,
				member.Name,
				out ProgramUniformValue programValue))
		{
			wrote = TryWriteProgramUniformValue(data, member, programValue);
			if (MaterialBindingDiagnosticsEnabled)
				LogMaterialAutoUniform(member, material, "program", programValue.Value, programValue.Type, wrote);
			return wrote;
		}

		if (member.IsArray &&
			TryWriteIndexedProgramUniformArray(
				data,
				member,
				member.Name,
				draw.ProgramBindingSnapshot))
			return true;

		ShaderVar? parameter = material.Parameter<ShaderVar>(member.Name);
		if (parameter is not null)
		{
			if (member.IsArray)
			{
				wrote = TryWriteAutoUniformArray(data, member, parameter);
				if (MaterialBindingDiagnosticsEnabled)
					LogMaterialAutoUniform(member, material, "material-array", parameter.GenericValue, parameter.TypeName, wrote);
				return wrote;
			}

			wrote = TryWriteMaterialUniformValue(data, member, parameter);
			if (MaterialBindingDiagnosticsEnabled)
				LogMaterialAutoUniform(member, material, "material", parameter.GenericValue, parameter.TypeName, wrote);
			return wrote;
		}

		if (member.IsArray && member.DefaultArrayValues is { Count: > 0 })
		{
			wrote = TryWriteAutoUniformArrayDefaults(data, member);
			if (MaterialBindingDiagnosticsEnabled)
				LogMaterialAutoUniform(member, material, "default-array", $"count={member.DefaultArrayValues.Count}", member.EngineType ?? EShaderVarType._float, wrote);
			return wrote;
		}

		if (member.DefaultValue is { } defaultValue)
		{
			wrote = TryWriteAutoUniformValue(data, member, defaultValue.Value, defaultValue.Type);
			if (MaterialBindingDiagnosticsEnabled)
				LogMaterialAutoUniform(member, material, "default", defaultValue.Value, defaultValue.Type, wrote);
			return wrote;
		}

		if (MaterialBindingDiagnosticsEnabled)
			LogMaterialAutoUniform(member, material, "missing", null, member.EngineType ?? EShaderVarType._float, false);
		return false;
	}

	private bool TryWriteMaterialUniformValue(Span<byte> data, AutoUniformMember member, ShaderVar parameter)
	{
		EngineUniformValue value;
		switch (parameter)
		{
			case ShaderFloat shaderFloat:
				value = shaderFloat.Value;
				break;
			case ShaderInt shaderInt:
				value = shaderInt.Value;
				break;
			case ShaderUInt shaderUInt:
				value = shaderUInt.Value;
				break;
			case ShaderVector2 shaderVector2:
				value = shaderVector2.Value;
				break;
			case ShaderVector3 shaderVector3:
				value = shaderVector3.Value;
				break;
			case ShaderVector4 shaderVector4:
				value = shaderVector4.Value;
				break;
			case ShaderMat4 shaderMatrix:
				value = shaderMatrix.Value;
				break;
			default:
				return TryWriteAutoUniformValue(data, member, parameter.GenericValue, parameter.TypeName);
		}

		return TryWriteAutoUniformValue(data, member, in value, parameter.TypeName);
	}

	private bool TryWriteTemporalViewProjectionUniform(
		Span<byte> data,
		AutoUniformMember member,
		in PendingMeshDraw draw,
		out bool wrote)
	{
		EVulkanTemporalUniformSource source = member.Name switch
		{
			"CurrViewProjection" => EVulkanTemporalUniformSource.CurrentViewProjection,
			"PrevViewProjection" => EVulkanTemporalUniformSource.PreviousViewProjection,
			"CurrViewProjectionStereo" => EVulkanTemporalUniformSource.CurrentStereoViewProjection,
			"PrevViewProjectionStereo" => EVulkanTemporalUniformSource.PreviousStereoViewProjection,
			_ => EVulkanTemporalUniformSource.None,
		};
		if (source == EVulkanTemporalUniformSource.None)
		{
			wrote = false;
			return false;
		}

		wrote = TryWriteTemporalViewProjectionUniform(data, member, source, draw);
		return true;
	}

	private bool TryWriteTemporalViewProjectionUniform(
		Span<byte> data,
		AutoUniformMember member,
		EVulkanTemporalUniformSource source,
		in PendingMeshDraw draw)
		=> source switch
		{
			EVulkanTemporalUniformSource.CurrentViewProjection =>
				TryWriteTemporalMatrix(
					data,
					member,
					draw.ViewProjectionMatrixUnjittered),
			EVulkanTemporalUniformSource.PreviousViewProjection =>
				TryWriteTemporalMatrix(
					data,
					member,
					draw.PreviousViewProjectionMatrixUnjittered),
			EVulkanTemporalUniformSource.CurrentStereoViewProjection =>
				TryWriteTemporalStereoViewProjectionUniform(
					data,
					member,
					draw.ViewProjectionMatrixUnjittered,
					draw.RightEyeViewProjectionMatrixUnjittered),
			EVulkanTemporalUniformSource.PreviousStereoViewProjection =>
				TryWriteTemporalStereoViewProjectionUniform(
					data,
					member,
					draw.PreviousViewProjectionMatrixUnjittered,
					draw.PreviousRightEyeViewProjectionMatrixUnjittered),
			_ => false,
		};

	private bool TryWriteTemporalStereoViewProjectionUniform(
		Span<byte> data,
		AutoUniformMember member,
		in Matrix4x4 left,
		in Matrix4x4 right)
	{
		if (!member.IsArray || member.ArrayLength < 2 || member.ArrayStride == 0)
			return false;

		AutoUniformMember element = member with
		{
			IsArray = false,
			ArrayLength = 0,
			ArrayStride = 0,
		};
		bool wroteLeft = TryWriteTemporalMatrix(data, element, left);
		element = element with { Offset = member.Offset + member.ArrayStride };
		bool wroteRight = TryWriteTemporalMatrix(data, element, right);
		return wroteLeft && wroteRight;
	}

	private static bool TryWriteTemporalMatrix(
		Span<byte> data,
		AutoUniformMember member,
		in Matrix4x4 matrix)
	{
		if (member.EngineType != EShaderVarType._mat4 || member.Offset + 64u > (uint)data.Length)
			return false;

		Unsafe.WriteUnaligned(ref data[(int)member.Offset], matrix);
		return true;
	}

	private void LogMaterialAutoUniform(
		AutoUniformMember member,
		XRMaterial material,
		string source,
		object? value,
		EShaderVarType type,
		bool wrote)
	{
		if (!MaterialBindingDiagnosticsEnabled || !IsMaterialAutoUniform(member.Name))
			return;

		Debug.MeshesWarningEvery(
			$"Vulkan.MaterialAutoUniform.{GetHashCode()}.{_program?.Data?.Name}.{material.Name}.{member.Name}",
			TimeSpan.FromSeconds(1),
			"[VkMaterialAutoUniform] program='{0}' mesh='{1}' material='{2}' member='{3}' type={4} source={5} wrote={6} offset={7} size={8} value={9}",
			_program?.Data?.Name ?? "<null>",
			Mesh?.Name ?? "<null>",
			material.Name ?? "<null>",
			member.Name,
			type,
			source,
			wrote,
			member.Offset,
			member.Size,
			FormatMaterialUniformDiagnosticValue(value));
	}

	private static bool IsMaterialAutoUniform(string name)
		=> name is "BaseColor" or "Opacity" or "Specular" or "Roughness" or "Metallic" or "Emission" or "AlphaCutoff"
		or "MatColor" or "LineWidth" or "ArrowHeadLengthPixels" or "ArrowHeadHalfWidthPixels"
		or "TextAtlasType" or "MsdfDistanceRange" or "MsdfDistanceRangeMiddle" or "MsdfFillBias" or "TextDebugMode" or "TextRenderLayer" or "TextRenderLayer_VTX"
		or "ModelMatrix" or "PrevModelMatrix" or "CurrViewProjection" or "PrevViewProjection"
		or "CurrViewProjectionStereo" or "PrevViewProjectionStereo";

	private bool IsGizmoDiagnosticProgram()
	{
		string? name = _program?.Data?.Name;
		return !string.IsNullOrWhiteSpace(name) &&
			(name.Contains("Gizmo", StringComparison.OrdinalIgnoreCase) ||
			 name.Contains("TransformTool", StringComparison.OrdinalIgnoreCase));
	}

	private void LogGizmoAutoUniformBlocks(XRMaterial material, bool skipped)
	{
		if (!MaterialBindingDiagnosticsEnabled || !IsGizmoDiagnosticProgram())
			return;

		Debug.MeshesWarningEvery(
			$"Vulkan.GizmoAutoUniformBlocks.{GetHashCode()}.{_program?.Data?.Name}.{material.Name}",
			TimeSpan.FromSeconds(1),
			"[VkGizmoAutoUniformBlocks] program='{0}' mesh='{1}' material='{2}' skipped={3} blockCount={4} bufferCount={5} blocks='{6}'",
			_program?.Data?.Name ?? "<null>",
			Mesh?.Name ?? "<null>",
			material.Name ?? "<null>",
			skipped,
			_program?.AutoUniformBlocks.Count ?? 0,
			_autoUniformBuffers.Count,
			FormatGizmoAutoUniformBlocks());
	}

    private string FormatGizmoAutoUniformBlocks()
		=> _program is null || _program.AutoUniformBlocks.Count == 0
            ? "<none>"
            : string.Join("; ", _program.AutoUniformBlocks.Select(pair =>
            $"{pair.Key}[{string.Join(",", pair.Value.Members.Select(static member => member.Name))}]"));

	private static string FormatMaterialUniformDiagnosticValue(object? value)
		=> value switch
		{
			null => "<null>",
			float f => f.ToString("G4", System.Globalization.CultureInfo.InvariantCulture),
			int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
			uint u => u.ToString(System.Globalization.CultureInfo.InvariantCulture),
			Vector2 v => $"({v.X:G4},{v.Y:G4})",
			Vector3 v => $"({v.X:G4},{v.Y:G4},{v.Z:G4})",
			Vector4 v => $"({v.X:G4},{v.Y:G4},{v.Z:G4},{v.W:G4})",
			Matrix4x4 m => FormatMatrixDiagnosticValue(in m),
			Matrix4x4[] matrices => string.Join(",", matrices.Select(static m => FormatMatrixDiagnosticValue(in m))),
			ColorF3 c => $"({c.R:G4},{c.G:G4},{c.B:G4})",
			ColorF4 c => $"({c.R:G4},{c.G:G4},{c.B:G4},{c.A:G4})",
			_ => value.ToString() ?? "<null>",
		};

	private static string FormatMatrixDiagnosticValue(in Matrix4x4 matrix)
		=> $"[{matrix.M11:G4},{matrix.M12:G4},{matrix.M13:G4},{matrix.M14:G4};" +
		   $"{matrix.M21:G4},{matrix.M22:G4},{matrix.M23:G4},{matrix.M24:G4};" +
		   $"{matrix.M31:G4},{matrix.M32:G4},{matrix.M33:G4},{matrix.M34:G4};" +
		   $"{matrix.M41:G4},{matrix.M42:G4},{matrix.M43:G4},{matrix.M44:G4}]";

	private bool TryWriteStructUniformValue(
		Span<byte> data,
		AutoUniformMember member,
		string uniformPrefix,
		uint baseOffset,
		ComputeDispatchSnapshot? snapshot)
	{
		if (member.StructMembers is not { Count: > 0 } fields)
			return false;

		bool wroteAny = false;
		for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
		{
			AutoUniformMember field = fields[fieldIndex];
			uint fieldOffset = baseOffset + field.Offset;
			string fieldName = StructUniformFieldNames.GetOrAdd(
				(uniformPrefix, field.Name),
				static key => $"{key.Prefix}.{key.Field}");
			AutoUniformMember absoluteField = field with { Offset = fieldOffset };

			if (field.StructMembers is { Count: > 0 })
			{
				if (field.IsArray)
					wroteAny |= TryWriteStructUniformArray(data, absoluteField, fieldName, snapshot);
				else
					wroteAny |= TryWriteStructUniformValue(data, absoluteField, fieldName, fieldOffset, snapshot);
				continue;
			}

			if (field.IsArray)
			{
				wroteAny |= TryWriteProgramUniformArray(data, absoluteField, fieldName, snapshot);
				continue;
			}

			if (_program is not null &&
				_program.TryGetUniformValue(snapshot, fieldName, out ProgramUniformValue fieldValue))
			{
				wroteAny |= TryWriteProgramUniformValue(data, absoluteField, fieldValue);
				continue;
			}

			if (field.DefaultValue is { } defaultValue)
				wroteAny |= TryWriteAutoUniformValue(data, absoluteField, defaultValue.Value, defaultValue.Type);
		}

		// Missing fields keep their zero-initialized UBO bytes, matching loose
		// GLSL struct-uniform defaults. A valid reflected struct is publishable
		// even when the current callback overrides no fields.
		return true;
	}

	private bool TryWriteStructUniformArray(
		Span<byte> data,
		AutoUniformMember member,
		string uniformPrefix,
		ComputeDispatchSnapshot? snapshot)
	{
		if (!member.IsArray || member.ArrayStride == 0 || member.ArrayLength == 0)
			return false;

		bool wroteAny = false;
		for (uint i = 0; i < member.ArrayLength; i++)
		{
			uint elementOffset = member.Offset + i * member.ArrayStride;
			AutoUniformMember element = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
			string elementName = IndexedUniformNames.GetOrAdd(
				(uniformPrefix, i),
				static key => $"{key.Prefix}[{key.Index}]");
			wroteAny |= TryWriteStructUniformValue(data, element, elementName, elementOffset, snapshot);
		}

		return true;
	}

    private bool TryWriteProgramUniformArray(
		Span<byte> data,
		AutoUniformMember member,
		string uniformName,
		ComputeDispatchSnapshot? snapshot)
		=> _program is not null && _program.TryGetUniformValue(snapshot, uniformName, out ProgramUniformValue programValue)
            ? TryWriteProgramUniformValue(data, member, programValue)
            : TryWriteIndexedProgramUniformArray(data, member, uniformName, snapshot);

    private bool TryWriteIndexedProgramUniformArray(
		Span<byte> data,
		AutoUniformMember member,
		string uniformName,
		ComputeDispatchSnapshot? snapshot)
	{
		if (_program is null || !member.IsArray || member.ArrayStride == 0 || member.ArrayLength == 0)
			return false;

		bool wroteAny = false;
		for (uint i = 0; i < member.ArrayLength; i++)
		{
			string elementName = IndexedUniformNames.GetOrAdd(
				(uniformName, i),
				static key => $"{key.Prefix}[{key.Index}]");
			if (!_program.TryGetUniformValue(snapshot, elementName, out ProgramUniformValue elementValue) || elementValue.IsArray)
				continue;

			uint elementOffset = member.Offset + i * member.ArrayStride;
			AutoUniformMember elementMember = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
			wroteAny |= TryWriteProgramUniformValue(data, elementMember, elementValue);
		}

		return wroteAny;
	}

	/// <summary>Writes a program uniform value into auto uniform buffer memory (scalar or array).</summary>
	private bool TryWriteProgramUniformValue(Span<byte> data, AutoUniformMember member, ProgramUniformValue value)
	{
		if (member.IsArray)
		{
			if (!value.IsArray || value.ReferenceValue is not Array array || member.ArrayStride == 0 || member.ArrayLength == 0)
				return false;

			if (TryWriteInlineProgramUniformArray(data, member, array, value.Type))
				return true;

			int max = Math.Min(array.Length, (int)member.ArrayLength);
			for (int i = 0; i < max; i++)
			{
				object? element = array.GetValue(i);
				if (element is null)
					continue;

				uint elementOffset = member.Offset + (uint)i * member.ArrayStride;
				AutoUniformMember elementMember = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
				TryWriteAutoUniformValue(data, elementMember, element, value.Type);
			}

			return true;
		}

		if (value.IsArray)
			return false;

		if (!value.HasInlineValue)
			return value.ReferenceValue is { } reference &&
				TryWriteAutoUniformValue(data, member, reference, value.Type);

		return TryWriteInlineProgramUniformValue(data, member, in value);
	}

	private bool TryWriteInlineProgramUniformArray(
		Span<byte> data,
		AutoUniformMember member,
		Array array,
		EShaderVarType valueType)
	{
		int max = Math.Min(array.Length, (int)member.ArrayLength);
		switch (array)
		{
			case float[] values when valueType == EShaderVarType._float:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case int[] values when valueType is EShaderVarType._int or EShaderVarType._bool:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case bool[] values when valueType == EShaderVarType._bool:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i] ? 1 : 0;
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case uint[] values when valueType == EShaderVarType._uint:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case Vector2[] values when valueType == EShaderVarType._vec2:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case Vector3[] values when valueType == EShaderVarType._vec3:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case Vector4[] values when valueType == EShaderVarType._vec4:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case Matrix4x4[] values when valueType == EShaderVarType._mat4:
				for (int i = 0; i < max; i++)
				{
					EngineUniformValue element = values[i];
					TryWriteInlineProgramUniformArrayElement(data, member, i, in element, valueType);
				}
				return true;
			case double[] values when valueType == EShaderVarType._double:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case DVector2[] values when valueType == EShaderVarType._dvec2:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case DVector3[] values when valueType == EShaderVarType._dvec3:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case DVector4[] values when valueType == EShaderVarType._dvec4:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case IVector2[] values when valueType == EShaderVarType._ivec2:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case IVector3[] values when valueType == EShaderVarType._ivec3:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case IVector4[] values when valueType == EShaderVarType._ivec4:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case UVector2[] values when valueType == EShaderVarType._uvec2:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case UVector3[] values when valueType == EShaderVarType._uvec3:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case UVector4[] values when valueType == EShaderVarType._uvec4:
				return TryWriteUnmanagedProgramUniformArray(data, member, values, valueType);
			case BoolVector2[] values when valueType == EShaderVarType._bvec2:
				for (int i = 0; i < max; i++)
				{
					int offset = (int)(member.Offset + (uint)i * member.ArrayStride);
					Unsafe.WriteUnaligned(ref data[offset], new IVector2(values[i].X ? 1 : 0, values[i].Y ? 1 : 0));
				}
				return true;
			case BoolVector3[] values when valueType == EShaderVarType._bvec3:
				for (int i = 0; i < max; i++)
				{
					int offset = (int)(member.Offset + (uint)i * member.ArrayStride);
					Unsafe.WriteUnaligned(ref data[offset], new IVector3(values[i].X ? 1 : 0, values[i].Y ? 1 : 0, values[i].Z ? 1 : 0));
				}
				return true;
			case BoolVector4[] values when valueType == EShaderVarType._bvec4:
				for (int i = 0; i < max; i++)
				{
					int offset = (int)(member.Offset + (uint)i * member.ArrayStride);
					Unsafe.WriteUnaligned(ref data[offset], new IVector4(values[i].X ? 1 : 0, values[i].Y ? 1 : 0, values[i].Z ? 1 : 0, values[i].W ? 1 : 0));
				}
				return true;
			default:
				return false;
		}
	}

	private static bool TryWriteUnmanagedProgramUniformArray<T>(
		Span<byte> data,
		AutoUniformMember member,
		T[] values,
		EShaderVarType valueType)
		where T : unmanaged
	{
		if (member.EngineType != valueType)
			return false;

		int max = Math.Min(values.Length, (int)member.ArrayLength);
		for (int i = 0; i < max; i++)
		{
			int offset = (int)(member.Offset + (uint)i * member.ArrayStride);
			Unsafe.WriteUnaligned(ref data[offset], values[i]);
		}
		return true;
	}

	private bool TryWriteInlineProgramUniformArrayElement(
		Span<byte> data,
		AutoUniformMember member,
		int index,
		in EngineUniformValue value,
		EShaderVarType valueType)
	{
		uint elementOffset = member.Offset + (uint)index * member.ArrayStride;
		AutoUniformMember elementMember = member with
		{
			Offset = elementOffset,
			IsArray = false,
			ArrayLength = 0,
			ArrayStride = 0
		};
		return TryWriteAutoUniformValue(data, elementMember, in value, valueType);
	}

	private static bool TryWriteInlineProgramUniformValue(
		Span<byte> data,
		AutoUniformMember member,
		in ProgramUniformValue value)
	{
		if (member.EngineType is not { } engineType || !AreCompatible(engineType, value.Type))
			return false;

		int offset = (int)member.Offset;
		switch (engineType)
		{
			case EShaderVarType._float when value.Type == EShaderVarType._float:
				Unsafe.WriteUnaligned(ref data[offset], value.Float);
				return true;
			case EShaderVarType._int when value.Type is EShaderVarType._int or EShaderVarType._bool:
				Unsafe.WriteUnaligned(ref data[offset], value.Int);
				return true;
			case EShaderVarType._uint when value.Type is EShaderVarType._uint or EShaderVarType._bool:
				Unsafe.WriteUnaligned(ref data[offset], value.Type == EShaderVarType._uint ? value.UInt : (uint)value.Int);
				return true;
			case EShaderVarType._bool when value.Type is EShaderVarType._int or EShaderVarType._bool:
				Unsafe.WriteUnaligned(ref data[offset], value.Int != 0 ? 1 : 0);
				return true;
			case EShaderVarType._bool when value.Type == EShaderVarType._uint:
				Unsafe.WriteUnaligned(ref data[offset], value.UInt != 0u ? 1 : 0);
				return true;
			case EShaderVarType._double when value.Type == EShaderVarType._double:
				Unsafe.WriteUnaligned(ref data[offset], value.Double);
				return true;
			case EShaderVarType._vec2 when value.Type == EShaderVarType._vec2:
				Unsafe.WriteUnaligned(ref data[offset], value.Vector2);
				return true;
			case EShaderVarType._vec3 when value.Type == EShaderVarType._vec3:
				Unsafe.WriteUnaligned(ref data[offset], value.Vector3);
				return true;
			case EShaderVarType._vec3 when value.Type == EShaderVarType._vec4:
				Unsafe.WriteUnaligned(ref data[offset], new Vector3(value.Vector4.X, value.Vector4.Y, value.Vector4.Z));
				return true;
			case EShaderVarType._vec4 when value.Type == EShaderVarType._vec3:
				Unsafe.WriteUnaligned(ref data[offset], new Vector4(value.Vector3, 0f));
				return true;
			case EShaderVarType._vec4 when value.Type == EShaderVarType._vec4:
				Unsafe.WriteUnaligned(ref data[offset], value.Vector4);
				return true;
			case EShaderVarType._mat4 when value.Type == EShaderVarType._mat4:
				Unsafe.WriteUnaligned(ref data[offset], value.Matrix4x4);
				return true;
			case EShaderVarType._dvec2 when value.Type == EShaderVarType._dvec2:
				Unsafe.WriteUnaligned(ref data[offset], new DVector2(value.DVector4.X, value.DVector4.Y));
				return true;
			case EShaderVarType._dvec3 when value.Type == EShaderVarType._dvec3:
			case EShaderVarType._dvec4 when value.Type == EShaderVarType._dvec4:
				Unsafe.WriteUnaligned(ref data[offset], value.DVector4);
				return true;
			case EShaderVarType._ivec2 when value.Type == EShaderVarType._ivec2:
				Unsafe.WriteUnaligned(ref data[offset], new IVector2(value.IVector4.X, value.IVector4.Y));
				return true;
			case EShaderVarType._ivec3 when value.Type == EShaderVarType._ivec3:
			case EShaderVarType._ivec4 when value.Type == EShaderVarType._ivec4:
				Unsafe.WriteUnaligned(ref data[offset], value.IVector4);
				return true;
			case EShaderVarType._uvec2 when value.Type == EShaderVarType._uvec2:
				Unsafe.WriteUnaligned(ref data[offset], new UVector2(value.UVector4.X, value.UVector4.Y));
				return true;
			case EShaderVarType._uvec3 when value.Type == EShaderVarType._uvec3:
			case EShaderVarType._uvec4 when value.Type == EShaderVarType._uvec4:
				Unsafe.WriteUnaligned(ref data[offset], value.UVector4);
				return true;
			default:
				return false;
		}
	}

	/// <summary>Writes an array-typed material parameter into auto uniform buffer memory.</summary>
	private bool TryWriteAutoUniformArray(Span<byte> data, AutoUniformMember member, ShaderVar parameter)
	{
		if (!member.IsArray || member.ArrayStride == 0 || member.ArrayLength == 0)
			return false;

		if (parameter.GenericValue is not IUniformableArray array)
			return false;

		var valuesProp = array.GetType().GetProperty("Values");
		if (valuesProp?.GetValue(array) is not Array values)
			return false;

		uint stride = member.ArrayStride;
		uint baseOffset = member.Offset;
		int max = (int)Math.Min((uint)values.Length, member.ArrayLength);

		for (int i = 0; i < max; i++)
		{
			if (values.GetValue(i) is not ShaderVar element)
				continue;

			uint elementOffset = baseOffset + (uint)i * stride;
			AutoUniformMember elementMember = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
			TryWriteAutoUniformValue(data, elementMember, element.GenericValue, element.TypeName);
		}

		return true;
	}

	/// <summary>Writes default array values into auto uniform buffer memory when no runtime data is available.</summary>
	private bool TryWriteAutoUniformArrayDefaults(Span<byte> data, AutoUniformMember member)
	{
		if (!member.IsArray || member.ArrayStride == 0 || member.ArrayLength == 0)
			return false;

		if (member.DefaultArrayValues is null || member.DefaultArrayValues.Count == 0)
			return false;

		uint stride = member.ArrayStride;
		uint baseOffset = member.Offset;
		int max = (int)Math.Min((uint)member.DefaultArrayValues.Count, member.ArrayLength);

		for (int i = 0; i < max; i++)
		{
			AutoUniformDefaultValue def = member.DefaultArrayValues[i];
			uint elementOffset = baseOffset + (uint)i * stride;
			AutoUniformMember elementMember = member with { Offset = elementOffset, IsArray = false, ArrayLength = 0, ArrayStride = 0 };
			TryWriteAutoUniformValue(data, elementMember, def.Value, def.Type);
		}

		return true;
	}

	/// <summary>
	/// Resolves an engine uniform value by name from the current rendering state.
	/// Handles matrices, camera properties, screen dimensions, UI bounds, etc.
	/// Engine-owned values come from the draw snapshot; UI bounds may still come
	/// from program-level overrides because they are authored per program.
	/// </summary>
	private bool TryResolveEngineUniformValue(
		string name,
		in PendingMeshDraw draw,
		out EngineUniformValue value,
		out EShaderVarType type)
	{
		string normalized = NormalizeEngineUniformName(name);
		if (TryResolveMeshStateSource(
				normalized,
				out EVulkanAutoUniformSpecialSource meshSource))
		{
			return TryResolveMeshStateUniformValue(
				meshSource,
				draw,
				out value,
				out type);
		}

		if (Enum.TryParse(
				normalized,
				ignoreCase: false,
				out EEngineUniform uniform))
		{
			return TryResolveEngineUniformValue(
				uniform,
				draw,
				out value,
				out type);
		}

		value = default;
		type = EShaderVarType._float;
		return false;
	}

	private static bool TryResolveMeshStateSource(
		string name,
		out EVulkanAutoUniformSpecialSource source)
	{
		source = name switch
		{
			TransformIdUniformName => EVulkanAutoUniformSpecialSource.TransformId,
			SkinPaletteBaseUniformName => EVulkanAutoUniformSpecialSource.SkinPaletteBase,
			SkinPaletteCountUniformName => EVulkanAutoUniformSpecialSource.SkinPaletteCount,
			SkinningInfluenceCapUniformName => EVulkanAutoUniformSpecialSource.SkinningInfluenceCap,
			BlendshapeActiveCountUniformName => EVulkanAutoUniformSpecialSource.BlendshapeActiveCount,
			BlendshapeWeightThresholdUniformName => EVulkanAutoUniformSpecialSource.BlendshapeWeightThreshold,
			UsePrecombinedBlendshapeDeltasUniformName => EVulkanAutoUniformSpecialSource.UsePrecombinedBlendshapeDeltas,
			_ => EVulkanAutoUniformSpecialSource.None,
		};
		return source != EVulkanAutoUniformSpecialSource.None;
	}

	private bool TryResolveMeshStateUniformValue(
		EVulkanAutoUniformSpecialSource source,
		in PendingMeshDraw draw,
		out EngineUniformValue value,
		out EShaderVarType type)
	{
		value = default;
		type = EShaderVarType._float;
		switch (source)
		{
			case EVulkanAutoUniformSpecialSource.TransformId:
				value = draw.TransformId;
				type = EShaderVarType._uint;
				return true;
			case EVulkanAutoUniformSpecialSource.SkinPaletteBase:
				value = draw.AutoUniformPublication.SkinPaletteBase;
				type = EShaderVarType._uint;
				return true;
			case EVulkanAutoUniformSpecialSource.SkinPaletteCount:
				value = draw.AutoUniformPublication.SkinPaletteCount;
				type = EShaderVarType._uint;
				return true;
			case EVulkanAutoUniformSpecialSource.SkinningInfluenceCap:
				value = draw.AutoUniformPublication.SkinningInfluenceCap;
				type = EShaderVarType._int;
				return true;
			case EVulkanAutoUniformSpecialSource.BlendshapeActiveCount:
				value = draw.AutoUniformPublication.BlendshapeActiveCount;
				type = EShaderVarType._int;
				return true;
			case EVulkanAutoUniformSpecialSource.BlendshapeWeightThreshold:
				value = draw.AutoUniformPublication.BlendshapeWeightThreshold;
				type = EShaderVarType._float;
				return true;
			case EVulkanAutoUniformSpecialSource.UsePrecombinedBlendshapeDeltas:
				value = RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass &&
					!RuntimeEngine.Rendering.State.IsVulkan &&
					draw.AutoUniformPublication.HasValidPrecombinedBlendshapeDeltas
						? 1
						: 0;
				type = EShaderVarType._int;
				return true;
			default:
				return false;
		}
	}

	private bool TryResolveEngineUniformValue(
		EEngineUniform uniform,
		in PendingMeshDraw draw,
		out EngineUniformValue value,
		out EShaderVarType type)
	{
		value = default;
		type = EShaderVarType._float;

		switch (uniform)
		{
			case EEngineUniform.UpdateDelta:
				Renderer.EnsureMaterialUniformFrameTime();
				value = Renderer._materialUniformUpdateDeltaLive;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.RenderTime:
				Renderer.EnsureMaterialUniformFrameTime();
				value = Renderer._materialUniformSecondsLive;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.EngineTime:
				Renderer.EnsureMaterialUniformFrameTime();
				value = Renderer._materialUniformSecondsLive;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.DeltaTime:
				Renderer.EnsureMaterialUniformFrameTime();
				value = Renderer._materialUniformDeltaSecondsLive;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.ModelMatrix:
				value = draw.ModelMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.PrevModelMatrix:
				value = draw.PreviousModelMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.RootInvModelMatrix:
				Matrix4x4.Invert(draw.ModelMatrix, out Matrix4x4 inverseModel);
				value = inverseModel;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.ViewMatrix:
			case EEngineUniform.LeftEyeViewMatrix:
				value = draw.ViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.PrevViewMatrix:
			case EEngineUniform.PrevLeftEyeViewMatrix:
				value = draw.PreviousViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.RightEyeViewMatrix:
				value = draw.RightEyeViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.PrevRightEyeViewMatrix:
				value = draw.PreviousRightEyeViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.InverseViewMatrix:
				value = draw.InverseViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.InverseProjMatrix:
				value = draw.InverseProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.ViewProjectionMatrix:
				value = draw.ViewProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.ProjMatrix:
				value = draw.ProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.PrevProjMatrix:
			case EEngineUniform.PrevLeftEyeProjMatrix:
				value = draw.PreviousProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.PrevRightEyeProjMatrix:
				value = draw.PreviousRightEyeProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.LeftEyeViewProjectionMatrix:
				value = draw.ViewProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.LeftEyeInverseViewMatrix:
				value = draw.InverseViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.LeftEyeInverseProjMatrix:
				value = draw.InverseProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.RightEyeInverseViewMatrix:
				value = draw.RightEyeInverseViewMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.RightEyeInverseProjMatrix:
				value = draw.RightEyeInverseProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.RightEyeViewProjectionMatrix:
				value = draw.RightEyeViewProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.LeftEyeProjMatrix:
				value = draw.ProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.RightEyeProjMatrix:
				value = draw.RightEyeProjectionMatrix;
				type = EShaderVarType._mat4;
				return true;
			case EEngineUniform.CameraPosition:
				value = ToVector4(draw.CameraPosition);
				type = EShaderVarType._vec4;
				return true;
			case EEngineUniform.CameraForward:
				value = ToVector4(draw.CameraForward);
				type = EShaderVarType._vec4;
				return true;
			case EEngineUniform.CameraUp:
				value = ToVector4(draw.CameraUp);
				type = EShaderVarType._vec4;
				return true;
			case EEngineUniform.CameraRight:
				value = ToVector4(draw.CameraRight);
				type = EShaderVarType._vec4;
				return true;
			case EEngineUniform.CameraNearZ:
				value = draw.AutoUniformPublication.CameraNearZ;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.CameraFarZ:
				value = draw.AutoUniformPublication.CameraFarZ;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.CameraFovX:
				value = draw.AutoUniformPublication.CameraFovX;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.CameraFovY:
				value = draw.AutoUniformPublication.CameraFovY;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.CameraAspect:
				value = draw.AutoUniformPublication.CameraAspect;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.DepthMode:
				value = (int)draw.AutoUniformPublication.CameraDepthMode;
				type = EShaderVarType._int;
				return true;
			case EEngineUniform.ClipSpaceYDirection:
				value = (int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection;
				type = EShaderVarType._int;
				return true;
			case EEngineUniform.ClipDepthRange:
				value = (int)RuntimeEngine.Rendering.EffectiveClipDepthRange;
				type = EShaderVarType._int;
				return true;
			case EEngineUniform.FramebufferTextureYDirection:
				value = (int)RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan);
				type = EShaderVarType._int;
				return true;
			case EEngineUniform.ScreenWidth:
			case EEngineUniform.ScreenHeight:
				// Resolve from the render-area snapshotted at enqueue time. Reading the live
				// RuntimeEngine.Rendering.State.RenderArea here (deferred record time) yields
				// 0 because the pipeline render-region stack has already been popped, which
				// would collapse the debug-line geometry-shader viewport to (1,1) and
				// explode every line into a screen-spanning quad.
				float screenW = draw.RenderAreaWidth;
				float screenH = draw.RenderAreaHeight;
				if (screenW <= 0f || screenH <= 0f)
				{
					screenW = draw.Viewport.Width;
					screenH = MathF.Abs(draw.Viewport.Height);
				}
				if (screenW <= 0f || screenH <= 0f)
				{
					var area = RuntimeEngine.Rendering.State.RenderArea;
					screenW = area.Width;
					screenH = area.Height;
				}
				value = uniform == EEngineUniform.ScreenWidth ? screenW : screenH;
				type = EShaderVarType._float;
				return true;
			case EEngineUniform.ScreenOrigin:
				value = new Vector2(0f, 0f);
				type = EShaderVarType._vec2;
				return true;
			case EEngineUniform.BillboardMode:
				value = (int)draw.BillboardMode;
				type = EShaderVarType._int;
				return true;
			case EEngineUniform.VRMode:
				value = draw.IsStereoPass ? 1 : 0;
				type = EShaderVarType._int;
				return true;
            case EEngineUniform.UIXYWH:
                if (_program is not null &&
                    _program.TryGetUniformValue(
                        draw.ProgramBindingSnapshot,
                        nameof(EEngineUniform.UIXYWH),
                        out ProgramUniformValue bounds))
				{
					value = EngineUniformValue.FromProgramValue(in bounds);
					type = bounds.Type;
					return true;
				}
				value = Vector4.Zero;
				type = EShaderVarType._vec4;
				return true;
			case EEngineUniform.UIWidth:
			case EEngineUniform.UIHeight:
			case EEngineUniform.UIX:
            case EEngineUniform.UIY:
                if (_program is not null &&
                    _program.TryGetUniformValue(
                        draw.ProgramBindingSnapshot,
                        uniform.ToStringFast(),
                        out ProgramUniformValue uiScalar))
				{
					value = EngineUniformValue.FromProgramValue(in uiScalar);
					type = uiScalar.Type;
					return true;
				}
                if (_program is not null &&
                    _program.TryGetUniformValue(
                        draw.ProgramBindingSnapshot,
                        nameof(EEngineUniform.UIXYWH),
                        out ProgramUniformValue uiBounds) &&
					uiBounds.HasInlineValue && uiBounds.Type == EShaderVarType._vec4)
				{
					Vector4 b = uiBounds.Vector4;
					value = uniform switch
					{
						EEngineUniform.UIX => b.X,
						EEngineUniform.UIY => b.Y,
						EEngineUniform.UIWidth => b.Z,
						EEngineUniform.UIHeight => b.W,
						_ => 0f
					};
					type = EShaderVarType._float;
					return true;
				}
				value = 0f;
				type = EShaderVarType._float;
				return true;
		}

		return false;
	}

	#endregion // Uniform Buffer Updates

	#region Uniform Data Writing Helpers

	/// <summary>
	/// Writes a typed value into auto uniform buffer memory at the member's offset.
	/// Supports float, int, uint, bool, vec2–vec4, ivec2–ivec4, uvec2–uvec4, and mat4.
	/// </summary>
	private bool TryWriteAutoUniformValue(Span<byte> data, AutoUniformMember member, object value, EShaderVarType valueType)
	{
		RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAutoUniformGenericConversion();
		if (member.EngineType is null)
			return false;

		if (!AreCompatible(member.EngineType.Value, valueType))
			return false;

		uint offset = member.Offset;
		switch (member.EngineType.Value)
		{
			case EShaderVarType._float:
				return TryWriteScalar(data, offset, value, Convert.ToSingle);
			case EShaderVarType._int:
				return TryWriteScalar(data, offset, value, Convert.ToInt32);
			case EShaderVarType._uint:
				return TryWriteScalar(data, offset, value, Convert.ToUInt32);
			case EShaderVarType._bool:
				return TryWriteScalar(data, offset, value, v => Convert.ToBoolean(v) ? 1 : 0);
			case EShaderVarType._vec2:
				return TryWriteVector2(data, offset, value);
			case EShaderVarType._vec3:
				return TryWriteVector3(data, offset, value);
			case EShaderVarType._vec4:
				return TryWriteVector4(data, offset, value);
			case EShaderVarType._ivec2:
				return TryWriteIVector2(data, offset, value);
			case EShaderVarType._ivec3:
				return TryWriteIVector3(data, offset, value);
			case EShaderVarType._ivec4:
				return TryWriteIVector4(data, offset, value);
			case EShaderVarType._uvec2:
				return TryWriteUVector2(data, offset, value);
			case EShaderVarType._uvec3:
				return TryWriteUVector3(data, offset, value);
			case EShaderVarType._uvec4:
				return TryWriteUVector4(data, offset, value);
			case EShaderVarType._mat4:
				return TryWriteMatrix4(data, offset, value);
			default:
				return false;
		}
	}

	private bool TryWriteAutoUniformValue(
		Span<byte> data,
		AutoUniformMember member,
		in EngineUniformValue value,
		EShaderVarType valueType)
	{
		if (value.Reference is { } reference)
			return TryWriteAutoUniformValue(data, member, reference, valueType);

		if (member.EngineType is not { } engineType || !AreCompatible(engineType, valueType))
			return false;

		int offset = (int)member.Offset;
		switch (engineType)
		{
			case EShaderVarType._float:
				Unsafe.WriteUnaligned(ref data[offset], value.Float);
				return true;
			case EShaderVarType._int:
				Unsafe.WriteUnaligned(ref data[offset], value.Int);
				return true;
			case EShaderVarType._uint:
				Unsafe.WriteUnaligned(ref data[offset], value.UInt);
				return true;
			case EShaderVarType._bool:
				int booleanValue = valueType == EShaderVarType._uint
					? value.UInt != 0u ? 1 : 0
					: value.Int != 0 ? 1 : 0;
				Unsafe.WriteUnaligned(ref data[offset], booleanValue);
				return true;
			case EShaderVarType._vec2:
				Unsafe.WriteUnaligned(ref data[offset], value.Vector2);
				return true;
			case EShaderVarType._vec3:
				Unsafe.WriteUnaligned(ref data[offset], new Vector4(value.Vector3, 0f));
				return true;
			case EShaderVarType._vec4:
				Unsafe.WriteUnaligned(ref data[offset], value.Vector4);
				return true;
			case EShaderVarType._mat4:
				Unsafe.WriteUnaligned(ref data[offset], value.Matrix4x4);
				return true;
			default:
				return false;
		}
	}

    /// <summary>
    /// Checks whether two shader variable types are compatible for writing.
    /// Allows common promotions (vec3↔vec4, int↔bool, uint↔bool).
    /// </summary>
    private static bool AreCompatible(EShaderVarType expected, EShaderVarType actual)
		=> expected == actual || (expected, actual) switch
		{
			(EShaderVarType._vec4, EShaderVarType._vec3) => true,
			(EShaderVarType._vec3, EShaderVarType._vec4) => true,
			(EShaderVarType._int, EShaderVarType._bool) => true,
			(EShaderVarType._uint, EShaderVarType._bool) => true,
			(EShaderVarType._bool, EShaderVarType._int) => true,
			(EShaderVarType._bool, EShaderVarType._uint) => true,
			_ => false
		};

    // ── Scalar and Vector Write Helpers ───────────────────────────────────
    // Each helper writes a specific type into a byte span at the given offset.
    // std140 aligns vec3 members to 16 bytes but still lets the next scalar use
    // the fourth lane, so vec3 writes must only touch xyz.

    private static bool TryWriteScalar<T>(Span<byte> data, uint offset, object value, Func<object, T> converter) where T : unmanaged
	{
		if (value is null || value is Array)
			return false;

		if (value is T typed)
		{
			Unsafe.WriteUnaligned(ref data[(int)offset], typed);
			return true;
		}

		if (value is not IConvertible)
			return false;

		try
		{
			T converted = converter(value);
			Unsafe.WriteUnaligned(ref data[(int)offset], converted);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryWriteVector2(Span<byte> data, uint offset, object value)
	{
		if (value is Vector2 v)
		{
			Unsafe.WriteUnaligned(ref data[(int)offset], v);
			return true;
		}
		return false;
	}

	private static bool TryWriteVector3(Span<byte> data, uint offset, object value)
	{
		if (TryConvertVector3(value, out Vector3 v3))
		{
			Unsafe.WriteUnaligned(ref data[(int)offset], v3);
			return true;
		}
		if (TryConvertVector4(value, out Vector4 v4b))
		{
			Vector3 v3b = new(v4b.X, v4b.Y, v4b.Z);
			Unsafe.WriteUnaligned(ref data[(int)offset], v3b);
			return true;
		}
		return false;
	}

	private static bool TryWriteVector4(Span<byte> data, uint offset, object value)
	{
		if (TryConvertVector4(value, out Vector4 v))
		{
			Unsafe.WriteUnaligned(ref data[(int)offset], v);
			return true;
		}
		if (TryConvertVector3(value, out Vector3 v3))
		{
			Vector4 v4 = new(v3, 0f);
			Unsafe.WriteUnaligned(ref data[(int)offset], v4);
			return true;
		}
		return false;
	}

	private static bool TryConvertVector3(object value, out Vector3 vector)
	{
		switch (value)
		{
			case Vector3 v:
				vector = v;
				return true;
			case Vector4 v:
				vector = new Vector3(v.X, v.Y, v.Z);
				return true;
			case ColorF3 c:
				vector = new Vector3(c.R, c.G, c.B);
				return true;
			case ColorF4 c:
				vector = new Vector3(c.R, c.G, c.B);
				return true;
			default:
				vector = default;
				return false;
		}
	}

	private static bool TryConvertVector4(object value, out Vector4 vector)
	{
		switch (value)
		{
			case Vector4 v:
				vector = v;
				return true;
			case Vector3 v:
				vector = new Vector4(v, 0f);
				return true;
			case ColorF4 c:
				vector = new Vector4(c.R, c.G, c.B, c.A);
				return true;
			case ColorF3 c:
				vector = new Vector4(c.R, c.G, c.B, 0f);
				return true;
			default:
				vector = default;
				return false;
		}
	}

	private static bool TryWriteIVector2(Span<byte> data, uint offset, object value)
	{
		if (value is IVector2 v)
		{
			Unsafe.WriteUnaligned(ref data[(int)offset], v);
			return true;
		}
		return false;
	}

	private static bool TryWriteIVector3(Span<byte> data, uint offset, object value)
	{
		if (value is IVector3 v3)
		{
			Unsafe.WriteUnaligned(ref data[(int)offset], v3);
			return true;
		}
		if (value is IVector4 v4b)
		{
			IVector3 v3b = new(v4b.X, v4b.Y, v4b.Z);
			Unsafe.WriteUnaligned(ref data[(int)offset], v3b);
			return true;
		}
		return false;
	}

	private static bool TryWriteIVector4(Span<byte> data, uint offset, object value)
	{
		if (value is IVector4 v)
		{
			Unsafe.WriteUnaligned(ref data[(int)offset], v);
			return true;
		}
		return false;
	}

	private static bool TryWriteUVector2(Span<byte> data, uint offset, object value)
	{
		if (value is UVector2 v)
		{
			Unsafe.WriteUnaligned(ref data[(int)offset], v);
			return true;
		}
		return false;
	}

	private static bool TryWriteUVector3(Span<byte> data, uint offset, object value)
	{
		if (value is UVector3 v3)
		{
			Unsafe.WriteUnaligned(ref data[(int)offset], v3);
			return true;
		}
		if (value is UVector4 v4b)
		{
			UVector3 v3b = new(v4b.X, v4b.Y, v4b.Z);
			Unsafe.WriteUnaligned(ref data[(int)offset], v3b);
			return true;
		}
		return false;
	}

	private static bool TryWriteUVector4(Span<byte> data, uint offset, object value)
	{
		if (value is UVector4 v)
		{
			Unsafe.WriteUnaligned(ref data[(int)offset], v);
			return true;
		}
		return false;
	}

	private static bool TryWriteMatrix4(Span<byte> data, uint offset, object value)
	{
		if (value is Matrix4x4 m)
		{
			Unsafe.WriteUnaligned(ref data[(int)offset], m);
			return true;
		}
		return false;
	}

	#endregion // Uniform Data Writing Helpers

	#region Engine Uniform Upload

	/// <summary>
	/// Resolves and uploads a named engine uniform to a host-visible UBO.
	/// This is the legacy per-binding path; auto uniform blocks use the
	/// TryWriteAutoUniformBlock path instead.
	/// </summary>
	private bool TryWriteEngineUniform(string name, in PendingMeshDraw draw, EngineUniformBuffer buffer)
	{
		string normalized = NormalizeEngineUniformName(name);
		if (normalized.Equals(FallbackDescriptorUniformName, StringComparison.Ordinal))
			return ClearEngineUniformBuffer(buffer);

		XRCamera? camera = draw.Camera;
		bool stereoPass = draw.IsStereoPass;

		// Camera matrices/vectors come from the draw snapshot captured at enqueue time.
		// Reading live camera state here can be stale because the pipeline camera stack
		// has already been popped.
		Matrix4x4 viewMatrix = draw.ViewMatrix;
		Matrix4x4 inverseViewMatrix = draw.InverseViewMatrix;
		Matrix4x4 projMatrix = draw.ProjectionMatrix;
		Matrix4x4 inverseProjMatrix = draw.InverseProjectionMatrix;
		Matrix4x4 rightEyeViewMatrix = draw.RightEyeViewMatrix;
		Matrix4x4 rightEyeInverseProjMatrix = draw.RightEyeInverseProjectionMatrix;
		Matrix4x4 rightEyeProjMatrix = draw.RightEyeProjectionMatrix;

		switch (normalized)
		{
			case nameof(EEngineUniform.UpdateDelta):
				Renderer.EnsureMaterialUniformFrameTime();
				return UploadUniform(buffer, Renderer._materialUniformUpdateDeltaLive);
			case nameof(EEngineUniform.RenderTime):
				Renderer.EnsureMaterialUniformFrameTime();
				return UploadUniform(buffer, Renderer._materialUniformSecondsLive);
			case nameof(EEngineUniform.EngineTime):
				Renderer.EnsureMaterialUniformFrameTime();
				return UploadUniform(buffer, Renderer._materialUniformSecondsLive);
			case nameof(EEngineUniform.DeltaTime):
				Renderer.EnsureMaterialUniformFrameTime();
				return UploadUniform(buffer, Renderer._materialUniformDeltaSecondsLive);
			case TransformIdUniformName:
				return UploadUniform(buffer, draw.TransformId);
			case SkinPaletteBaseUniformName:
				return UploadUniform(buffer, MeshRenderer.ActiveSkinPaletteBase);
			case SkinPaletteCountUniformName:
				return UploadUniform(buffer, MeshRenderer.ActiveSkinPaletteCount);
			case SkinningInfluenceCapUniformName:
				return UploadUniform(buffer, MeshRenderer.ActiveSkinningInfluenceCap);
			case BlendshapeActiveCountUniformName:
				return UploadUniform(buffer, MeshRenderer.ActiveBlendshapeCount);
			case BlendshapeWeightThresholdUniformName:
				return UploadUniform(buffer, MeshRenderer.BlendshapeActiveWeightThreshold);
			case UsePrecombinedBlendshapeDeltasUniformName:
				return UploadUniform(
					buffer,
					RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass
					&& !RuntimeEngine.Rendering.State.IsVulkan
					&& MeshRenderer.HasValidPrecombinedBlendshapeDeltas
						? 1
						: 0);
			case nameof(EEngineUniform.ModelMatrix):
				return UploadUniform(buffer, draw.ModelMatrix);
			case nameof(EEngineUniform.PrevModelMatrix):
				return UploadUniform(buffer, draw.PreviousModelMatrix);
			case nameof(EEngineUniform.RootInvModelMatrix):
				Matrix4x4.Invert(draw.ModelMatrix, out Matrix4x4 inverseModel);
				return UploadUniform(buffer, inverseModel);
			case nameof(EEngineUniform.ViewMatrix):
			case nameof(EEngineUniform.LeftEyeViewMatrix):
				return UploadUniform(buffer, viewMatrix);
			case nameof(EEngineUniform.PrevViewMatrix):
			case nameof(EEngineUniform.PrevLeftEyeViewMatrix):
				return UploadUniform(buffer, draw.PreviousViewMatrix);
			case nameof(EEngineUniform.RightEyeViewMatrix):
				return UploadUniform(buffer, rightEyeViewMatrix);
			case nameof(EEngineUniform.PrevRightEyeViewMatrix):
				return UploadUniform(buffer, draw.PreviousRightEyeViewMatrix);
			case nameof(EEngineUniform.InverseViewMatrix):
				return UploadUniform(buffer, inverseViewMatrix);
			case nameof(EEngineUniform.InverseProjMatrix):
				return UploadUniform(buffer, inverseProjMatrix);
			case nameof(EEngineUniform.ViewProjectionMatrix):
				return UploadUniform(buffer, draw.ViewProjectionMatrix);
			case nameof(EEngineUniform.ProjMatrix):
				return UploadUniform(buffer, projMatrix);
			case nameof(EEngineUniform.PrevProjMatrix):
			case nameof(EEngineUniform.PrevLeftEyeProjMatrix):
				return UploadUniform(buffer, draw.PreviousProjectionMatrix);
			case nameof(EEngineUniform.PrevRightEyeProjMatrix):
				return UploadUniform(buffer, draw.PreviousRightEyeProjectionMatrix);
			case nameof(EEngineUniform.LeftEyeViewProjectionMatrix):
				return UploadUniform(buffer, draw.ViewProjectionMatrix);
			case nameof(EEngineUniform.LeftEyeInverseViewMatrix):
				return UploadUniform(buffer, inverseViewMatrix);
			case nameof(EEngineUniform.LeftEyeInverseProjMatrix):
				return UploadUniform(buffer, inverseProjMatrix);
			case nameof(EEngineUniform.RightEyeInverseViewMatrix):
				return UploadUniform(buffer, draw.RightEyeInverseViewMatrix);
			case nameof(EEngineUniform.RightEyeInverseProjMatrix):
				return UploadUniform(buffer, rightEyeInverseProjMatrix);
			case nameof(EEngineUniform.RightEyeViewProjectionMatrix):
				return UploadUniform(buffer, draw.RightEyeViewProjectionMatrix);
			case nameof(EEngineUniform.LeftEyeProjMatrix):
				return UploadUniform(buffer, projMatrix);
			case nameof(EEngineUniform.RightEyeProjMatrix):
				return UploadUniform(buffer, rightEyeProjMatrix);
			case nameof(EEngineUniform.CameraPosition):
				return UploadUniform(buffer, ToVector4(draw.CameraPosition));
			case nameof(EEngineUniform.CameraForward):
				return UploadUniform(buffer, ToVector4(draw.CameraForward));
			case nameof(EEngineUniform.CameraUp):
				return UploadUniform(buffer, ToVector4(draw.CameraUp));
			case nameof(EEngineUniform.CameraRight):
				return UploadUniform(buffer, ToVector4(draw.CameraRight));
			case nameof(EEngineUniform.CameraNearZ):
				return UploadUniform(buffer, camera?.NearZ ?? 0f);
			case nameof(EEngineUniform.CameraFarZ):
				return UploadUniform(buffer, camera?.FarZ ?? 0f);
			case nameof(EEngineUniform.CameraFovX):
				return UploadUniform(buffer, camera?.Parameters is XRPerspectiveCameraParameters persp ? persp.HorizontalFieldOfView : 0f);
			case nameof(EEngineUniform.CameraFovY):
				return UploadUniform(buffer, camera?.Parameters is XRPerspectiveCameraParameters perspY ? perspY.VerticalFieldOfView : 0f);
			case nameof(EEngineUniform.CameraAspect):
				return UploadUniform(buffer, camera?.Parameters is XRPerspectiveCameraParameters perspA ? perspA.AspectRatio : 0f);
			case nameof(EEngineUniform.DepthMode):
				return UploadUniform(buffer, (int)(camera?.DepthMode ?? XRCamera.EDepthMode.Normal));
			case nameof(EEngineUniform.ClipSpaceYDirection):
				return UploadUniform(buffer, (int)RuntimeEngine.Rendering.Settings.ClipSpaceYDirection);
			case nameof(EEngineUniform.ClipDepthRange):
				return UploadUniform(buffer, (int)RuntimeEngine.Rendering.EffectiveClipDepthRange);
			case nameof(EEngineUniform.FramebufferTextureYDirection):
				return UploadUniform(buffer, (int)RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan));
			case nameof(EEngineUniform.ScreenWidth):
			case nameof(EEngineUniform.ScreenHeight):
				// Prefer the enqueue-time render-area snapshot; the live RenderArea is empty
				// at deferred record time (see the matching note in TryResolveEngineUniformValue).
				float screenW = draw.RenderAreaWidth;
				float screenH = draw.RenderAreaHeight;
				if (screenW <= 0f || screenH <= 0f)
				{
					screenW = draw.Viewport.Width;
					screenH = MathF.Abs(draw.Viewport.Height);
				}
				if (screenW <= 0f || screenH <= 0f)
				{
					var area = RuntimeEngine.Rendering.State.RenderArea;
					screenW = area.Width;
					screenH = area.Height;
				}
				return UploadUniform(buffer, normalized.Equals(nameof(EEngineUniform.ScreenWidth), StringComparison.Ordinal) ? screenW : screenH);
			case nameof(EEngineUniform.ScreenOrigin):
				return UploadUniform(buffer, new Vector2(0f, 0f));
			case nameof(EEngineUniform.BillboardMode):
				return UploadUniform(buffer, (int)draw.BillboardMode);
			case nameof(EEngineUniform.VRMode):
				return UploadUniform(buffer, stereoPass ? 1 : 0);
			case nameof(EEngineUniform.UIXYWH):
				if (_program is not null &&
					_program.TryGetUniformValue(
						draw.ProgramBindingSnapshot,
						nameof(EEngineUniform.UIXYWH),
						out ProgramUniformValue uiBounds))
					return UploadProgramUniform(buffer, uiBounds);
				return UploadUniform(buffer, Vector4.Zero);
			case nameof(EEngineUniform.UIX):
			case nameof(EEngineUniform.UIY):
			case nameof(EEngineUniform.UIWidth):
			case nameof(EEngineUniform.UIHeight):
				if (_program is not null &&
					_program.TryGetUniformValue(
						draw.ProgramBindingSnapshot,
						normalized,
						out ProgramUniformValue uiScalar))
					return UploadProgramUniform(buffer, uiScalar);
				if (_program is not null &&
					_program.TryGetUniformValue(
						draw.ProgramBindingSnapshot,
						nameof(EEngineUniform.UIXYWH),
						out ProgramUniformValue packedBounds) &&
					packedBounds.TryGetVector4(out Vector4 b))
				{
					float scalar = normalized switch
					{
						nameof(EEngineUniform.UIX) => b.X,
						nameof(EEngineUniform.UIY) => b.Y,
						nameof(EEngineUniform.UIWidth) => b.Z,
						nameof(EEngineUniform.UIHeight) => b.W,
						_ => 0f
					};
					return UploadUniform(buffer, scalar);
				}
				return UploadUniform(buffer, 0f);
		}

		if (_engineUniformWarnings.Add(normalized))
			Debug.VulkanWarning($"Unhandled engine uniform '{normalized}' for Vulkan descriptors.");

		return false;
	}

    private static readonly ConcurrentDictionary<string, string> NormalizedEngineUniformNames =
        new(StringComparer.Ordinal);

    /// <summary>Strips and caches the vertex-stage suffix ("_VTX") from engine uniform names.</summary>
    private static string NormalizeEngineUniformName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return string.Empty;

		return name.EndsWith(VertexUniformSuffix, StringComparison.Ordinal)
			? NormalizedEngineUniformNames.GetOrAdd(
				name,
				static uniformName => uniformName[..^VertexUniformSuffix.Length])
			: name;
	}

    /// <summary>
    /// Returns the byte size of a named engine uniform.
    /// Returns 0 for unrecognized names (e.g. user-defined uniforms).
    /// </summary>
    private static uint GetEngineUniformSize(string name)
	{
		string normalized = NormalizeEngineUniformName(name);
		return normalized switch
		{
			nameof(EEngineUniform.ModelMatrix) or nameof(EEngineUniform.PrevModelMatrix) or nameof(EEngineUniform.ViewMatrix) or nameof(EEngineUniform.LeftEyeViewMatrix) or nameof(EEngineUniform.RightEyeViewMatrix) or nameof(EEngineUniform.InverseViewMatrix) or nameof(EEngineUniform.InverseProjMatrix) or nameof(EEngineUniform.ProjMatrix) or nameof(EEngineUniform.ViewProjectionMatrix) or nameof(EEngineUniform.LeftEyeViewProjectionMatrix) or nameof(EEngineUniform.RightEyeViewProjectionMatrix) or nameof(EEngineUniform.LeftEyeInverseViewMatrix) or nameof(EEngineUniform.RightEyeInverseViewMatrix) or nameof(EEngineUniform.LeftEyeInverseProjMatrix) or nameof(EEngineUniform.RightEyeInverseProjMatrix) or nameof(EEngineUniform.LeftEyeProjMatrix) or nameof(EEngineUniform.RightEyeProjMatrix) or nameof(EEngineUniform.PrevViewMatrix) or nameof(EEngineUniform.PrevLeftEyeViewMatrix) or nameof(EEngineUniform.PrevRightEyeViewMatrix) or nameof(EEngineUniform.PrevProjMatrix) or nameof(EEngineUniform.PrevLeftEyeProjMatrix) or nameof(EEngineUniform.PrevRightEyeProjMatrix) or nameof(EEngineUniform.RootInvModelMatrix) => (uint)Unsafe.SizeOf<Matrix4x4>(),
			nameof(EEngineUniform.CameraPosition) or nameof(EEngineUniform.CameraForward) or nameof(EEngineUniform.CameraUp) or nameof(EEngineUniform.CameraRight) => 16u,
			nameof(EEngineUniform.CameraNearZ) or nameof(EEngineUniform.CameraFarZ) or nameof(EEngineUniform.CameraFovX) or nameof(EEngineUniform.CameraFovY) or nameof(EEngineUniform.CameraAspect) or nameof(EEngineUniform.ScreenWidth) or nameof(EEngineUniform.ScreenHeight) or nameof(EEngineUniform.UpdateDelta) or nameof(EEngineUniform.RenderTime) or nameof(EEngineUniform.EngineTime) or nameof(EEngineUniform.DeltaTime) or nameof(EEngineUniform.DepthMode) or nameof(EEngineUniform.ClipSpaceYDirection) or nameof(EEngineUniform.ClipDepthRange) or nameof(EEngineUniform.FramebufferTextureYDirection) or nameof(EEngineUniform.UIX) or nameof(EEngineUniform.UIY) or nameof(EEngineUniform.UIWidth) or nameof(EEngineUniform.UIHeight) or TransformIdUniformName or SkinPaletteBaseUniformName or SkinPaletteCountUniformName or SkinningInfluenceCapUniformName or BlendshapeActiveCountUniformName or BlendshapeWeightThresholdUniformName or UsePrecombinedBlendshapeDeltasUniformName => 4u,
			nameof(EEngineUniform.ScreenOrigin) => 8u,
			nameof(EEngineUniform.BillboardMode) or nameof(EEngineUniform.VRMode) => 4u,
			nameof(EEngineUniform.UIXYWH) => 16u,
			_ => 0u,
		};
	}

	/// <summary>Converts a Vector3 to Vector4 with W=0 for shader upload.</summary>
	private static Vector4 ToVector4(in Vector3 v) => new(v, 0f);

	/// <summary>Maps and uploads a single unmanaged value to a host-visible UBO.</summary>
	private bool UploadUniform<T>(EngineUniformBuffer buffer, in T value) where T : unmanaged
	{
		if (buffer.MappedPtr == null)
			return false;

		uint size = (uint)Unsafe.SizeOf<T>();
		uint copySize = Math.Min(buffer.Size, size);

		T localValue = value;
		Unsafe.CopyBlock(buffer.MappedPtr, Unsafe.AsPointer(ref localValue), copySize);
		return true;
	}

	private bool ClearEngineUniformBuffer(EngineUniformBuffer buffer)
	{
		if (buffer.MappedPtr == null)
			return false;

		new Span<byte>(buffer.MappedPtr, (int)buffer.Size).Clear();
		return true;
	}

	/// <summary>Uploads an inline program uniform value to a host-visible UBO without boxing.</summary>
	private bool UploadProgramUniform(EngineUniformBuffer buffer, ProgramUniformValue value)
	{
		if (!value.HasInlineValue)
			return value.ReferenceValue is { } reference &&
				UploadReferencedProgramUniform(buffer, reference, value.Type);

		return value.Type switch
		{
			EShaderVarType._float => UploadUniform(buffer, value.Float),
			EShaderVarType._int or EShaderVarType._bool => UploadUniform(buffer, value.Int),
			EShaderVarType._uint => UploadUniform(buffer, value.UInt),
			EShaderVarType._vec2 => UploadUniform(buffer, value.Vector2),
			EShaderVarType._vec3 => UploadUniform(buffer, new Vector4(value.Vector3, 0f)),
			EShaderVarType._vec4 => UploadUniform(buffer, value.Vector4),
			EShaderVarType._ivec2 => UploadUniform(buffer, new IVector2(value.IVector4.X, value.IVector4.Y)),
			EShaderVarType._ivec3 => UploadUniform(buffer, value.IVector4),
			EShaderVarType._ivec4 => UploadUniform(buffer, value.IVector4),
			EShaderVarType._uvec2 => UploadUniform(buffer, new UVector2(value.UVector4.X, value.UVector4.Y)),
			EShaderVarType._uvec3 => UploadUniform(buffer, value.UVector4),
			EShaderVarType._uvec4 => UploadUniform(buffer, value.UVector4),
			EShaderVarType._mat4 => UploadUniform(buffer, value.Matrix4x4),
			_ => false
		};
	}

	private bool UploadReferencedProgramUniform(
		EngineUniformBuffer buffer,
		object value,
		EShaderVarType type)
		=> type switch
		{
			EShaderVarType._float => UploadUniform(buffer, Convert.ToSingle(value)),
			EShaderVarType._int => UploadUniform(buffer, Convert.ToInt32(value)),
			EShaderVarType._uint => UploadUniform(buffer, Convert.ToUInt32(value)),
			EShaderVarType._bool => UploadUniform(buffer, Convert.ToBoolean(value) ? 1 : 0),
			EShaderVarType._vec2 when value is Vector2 v2 => UploadUniform(buffer, v2),
			EShaderVarType._vec3 when TryConvertVector3(value, out Vector3 v3) => UploadUniform(buffer, new Vector4(v3, 0f)),
			EShaderVarType._vec4 when TryConvertVector4(value, out Vector4 v4) => UploadUniform(buffer, v4),
			EShaderVarType._ivec2 when value is IVector2 iv2 => UploadUniform(buffer, iv2),
			EShaderVarType._ivec3 when value is IVector3 iv3 => UploadUniform(buffer, new IVector4(iv3.X, iv3.Y, iv3.Z, 0)),
			EShaderVarType._ivec4 when value is IVector4 iv4 => UploadUniform(buffer, iv4),
			EShaderVarType._uvec2 when value is UVector2 uv2 => UploadUniform(buffer, uv2),
			EShaderVarType._uvec3 when value is UVector3 uv3 => UploadUniform(buffer, new UVector4(uv3.X, uv3.Y, uv3.Z, 0)),
			EShaderVarType._uvec4 when value is UVector4 uv4 => UploadUniform(buffer, uv4),
			EShaderVarType._mat4 when value is Matrix4x4 mat => UploadUniform(buffer, mat),
			_ => false
		};

	#endregion // Engine Uniform Upload
}
