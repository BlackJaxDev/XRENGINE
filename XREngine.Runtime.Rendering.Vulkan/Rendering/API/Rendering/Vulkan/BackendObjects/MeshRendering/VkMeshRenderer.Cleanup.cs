// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.Cleanup.cs  – partial class: Resource Cleanup & Format Conversion
//
// Destroys per-frame engine and auto uniform buffers, descriptor pools, and
// provides Vulkan format / index-type conversion utilities.
// ──────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	#region Resource Cleanup

	/// <summary>
	/// Destroys per-frame engine uniform buffers. If <paramref name="singleName"/> is
	/// provided, only that buffer set is destroyed; otherwise all are cleared.
	/// </summary>
	private void DestroyEngineUniformBuffers(string? singleName = null)
	{
		if (singleName is not null)
		{
			if (_engineUniformBuffers.TryGetValue(singleName, out EngineUniformBuffer[]? toDestroy))
				DestroyEngineUniformBufferArray(toDestroy);

			_engineUniformBuffers.Remove(singleName);
			return;
		}

		foreach (EngineUniformBuffer[] buffers in _engineUniformBuffers.Values)
			DestroyEngineUniformBufferArray(buffers);

		_engineUniformBuffers.Clear();
	}

	/// <summary>
	/// Destroys per-frame auto uniform buffers. If <paramref name="singleName"/> is
	/// provided, only that buffer set is destroyed; otherwise all are cleared.
	/// </summary>
	private void DestroyAutoUniformBuffers(string? singleName = null)
	{
		if (singleName is not null)
		{
			if (_autoUniformBuffers.TryGetValue(singleName, out AutoUniformBuffer[]? toDestroy))
				DestroyAutoUniformBufferArray(toDestroy);

			_autoUniformBuffers.Remove(singleName);
			_autoUniformOwnerSlotTables.Remove(singleName);
			_publishedAutoUniformMaterialWritePlans.Remove(singleName);
			return;
		}

		foreach (AutoUniformBuffer[] buffers in _autoUniformBuffers.Values)
			DestroyAutoUniformBufferArray(buffers);

		_autoUniformBuffers.Clear();
		_autoUniformOwnerSlotTables.Clear();
		_publishedAutoUniformMaterialWritePlans.Clear();
	}

	private void DestroyEngineUniformBufferArray(EngineUniformBuffer[] buffers)
	{
		foreach (EngineUniformBuffer buf in buffers)
		{
			if (buf.OwnsBuffer)
				DestroyMappedUniformBuffer(buf.Buffer, buf.Memory);
		}
	}

	private void DestroyAutoUniformBufferArray(AutoUniformBuffer[] buffers)
	{
		foreach (AutoUniformBuffer buf in buffers)
		{
			if (buf.OwnsBuffer)
				DestroyMappedUniformBuffer(buf.Buffer, buf.Memory);
		}
	}

	private void DestroyMappedUniformBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
	{
		BackendContext.Resources.Buffers.Destroy(BackendContext, buffer, memory, "VkMeshRenderer.UniformBuffer");
	}

	/// <summary>
	/// Destroys all descriptor sets, the descriptor pool, and all uniform buffers.
	/// Called when pipelines are torn down or the renderer is unlinked.
	/// </summary>
	private void DestroyDescriptors()
	{
		ReleaseDescriptorAllocation();
		DestroyEngineUniformBuffers();
		DestroyAutoUniformBuffers();
	}

	internal void ReleaseDescriptorReferencesForPhysicalResourceDestruction()
	{
		ReleaseDescriptorAllocation();
		_descriptorDirty = true;
	}

	/// <summary>
	/// Drops only immutable descriptor allocations whose locally-owned set tier
	/// still names a superseded native buffer generation. Shared material tiers
	/// are intentionally excluded because this renderer does not own them.
	/// </summary>
	internal int ReleaseSupersededDescriptorAllocations(
		ReadOnlySpan<VulkanDescriptorSetGenerationReference> affectedSets)
	{
		int detachedCount = 0;
		lock (_recordDrawSync)
		{
			List<DescriptorAllocationKey>? keysToRelease = null;
			if (!affectedSets.IsEmpty)
			{
				foreach (KeyValuePair<DescriptorAllocationKey, DescriptorAllocation> pair in _descriptorAllocations)
				{
					if (AllocationOwnsAffectedDescriptorSet(pair.Value, affectedSets))
						(keysToRelease ??= []).Add(pair.Key);
				}
			}

			if (keysToRelease is not null)
			{
				for (int index = 0; index < keysToRelease.Count; index++)
				{
					DescriptorAllocationKey key = keysToRelease[index];
					if (!_descriptorAllocations.Remove(key, out DescriptorAllocation? allocation))
						continue;

					bool wasActive = ReferenceEquals(_activeDescriptorAllocation, allocation);
					RemoveDescriptorDrawSlotLookupEntries(allocation);
					RemoveDescriptorOwnerLookupEntries(allocation);
					if (BackendContext.Resources.Descriptors.ReleaseSharedMeshDescriptorAllocation(key, allocation))
						_pendingSupersededDescriptorAllocationRetirements.Enqueue((key, allocation));
					if (wasActive)
						ClearActiveDescriptorAllocation();
					detachedCount++;
				}
			}

			if (detachedCount != 0)
				_descriptorDirty = true;
		}

		DrainPendingSupersededDescriptorAllocationRetirements();
		return detachedCount;
	}

	private void DrainPendingSupersededDescriptorAllocationRetirements()
	{
		while (true)
		{
			(DescriptorAllocationKey Key, DescriptorAllocation Allocation) pending;
			lock (_recordDrawSync)
			{
				if (_pendingSupersededDescriptorAllocationRetirements.Count == 0)
					return;

				pending = _pendingSupersededDescriptorAllocationRetirements.Peek();
			}

			// Keep the detached allocation queued if normal lifetime retirement throws.
			ReleaseDescriptorAllocationResources(pending.Allocation);
			ReleaseDescriptorOwnershipTelemetry(pending.Allocation);
			lock (_recordDrawSync)
			{
				if (_pendingSupersededDescriptorAllocationRetirements.Count != 0 &&
					ReferenceEquals(
						_pendingSupersededDescriptorAllocationRetirements.Peek().Allocation,
						pending.Allocation))
				{
					_pendingSupersededDescriptorAllocationRetirements.Dequeue();
				}
			}
		}
	}

	private static bool AllocationOwnsAffectedDescriptorSet(
		DescriptorAllocation allocation,
		ReadOnlySpan<VulkanDescriptorSetGenerationReference> affectedSets)
	{
		for (int frameSlot = 0; frameSlot < allocation.Sets.Length; frameSlot++)
		{
			DescriptorSet[] sets = allocation.Sets[frameSlot];
			for (int setIndex = 0; setIndex < sets.Length; setIndex++)
			{
				if (setIndex >= 32 || (allocation.ActiveSetMask & (1u << setIndex)) == 0)
					continue;

				ulong handle = sets[setIndex].Handle;
				for (int affectedIndex = 0; affectedIndex < affectedSets.Length; affectedIndex++)
				{
					if (affectedSets[affectedIndex].Set.Handle == handle)
						return true;
				}
			}
		}

		return false;
	}

	private void RemoveDescriptorDrawSlotLookupEntries(DescriptorAllocation allocation)
	{
		while (true)
		{
			int keyToRemove = default;
			bool found = false;
			foreach (KeyValuePair<int, DescriptorAllocation> pair in _descriptorAllocationsByDrawSlot)
			{
				if (!ReferenceEquals(pair.Value, allocation))
					continue;

				keyToRemove = pair.Key;
				found = true;
				break;
			}
			if (!found)
				return;

			_descriptorAllocationsByDrawSlot.Remove(keyToRemove);
		}
	}

	private void ClearActiveDescriptorAllocation()
	{
		_activeDescriptorAllocation = null;
		_descriptorSets = null;
		_descriptorSchemaFingerprint = 0;
		_descriptorResourceFingerprint = 0;
		_descriptorResourceFingerprintDetails = string.Empty;
		_descriptorPool = default;
	}

	private void ReleaseDescriptorAllocation(bool destroyPoolImmediately = false)
	{
		ulong activePoolHandle = _descriptorPool.Handle;
		bool activePoolReleased = activePoolHandle == 0;

		foreach (KeyValuePair<DescriptorAllocationKey, DescriptorAllocation> pair in _descriptorAllocations)
		{
			DescriptorAllocation allocation = pair.Value;
			if (allocation.Pool.Handle == activePoolHandle)
				activePoolReleased = true;

			ReleaseDescriptorAllocationReference(pair.Key, allocation, destroyPoolImmediately);
		}

        _descriptorAllocations.Clear();
        _descriptorAllocationsByDrawSlot.Clear();
        _descriptorAllocationsByOwner.Clear();

		if (!activePoolReleased && _descriptorPool.Handle != 0)
			ReleaseDescriptorPool(_descriptorPool, destroyPoolImmediately);

		_activeDescriptorAllocation = null;
		_descriptorSets = null;

		_descriptorSchemaFingerprint = 0;
		_descriptorResourceFingerprint = 0;
		_descriptorResourceFingerprintDetails = string.Empty;
		_descriptorPool = default;
	}

	private void ReleaseDescriptorAllocationReference(
		in DescriptorAllocationKey key,
		DescriptorAllocation allocation,
		bool destroyPoolImmediately = false)
	{
		RemoveDescriptorOwnerLookupEntries(allocation);
		if (!BackendContext.Resources.Descriptors.ReleaseSharedMeshDescriptorAllocation(key, allocation))
			return;

		ReleaseDescriptorOwnershipTelemetry(allocation);
		ReleaseDescriptorAllocationResources(allocation, destroyPoolImmediately);
	}

	private void RemoveDescriptorOwnerLookupEntries(
		DescriptorAllocation allocation)
	{
		while (true)
		{
			DescriptorOwnerLookupKey keyToRemove = default;
			bool found = false;
			foreach (KeyValuePair<DescriptorOwnerLookupKey, DescriptorAllocation> pair
				in _descriptorAllocationsByOwner)
			{
				if (!ReferenceEquals(pair.Value, allocation))
					continue;

				keyToRemove = pair.Key;
				found = true;
				break;
			}

			if (!found)
				return;

			_descriptorAllocationsByOwner.Remove(keyToRemove);
		}
	}

	private void ReleaseDescriptorPool(DescriptorPool descriptorPool, bool destroyImmediately = false)
	{
		if (descriptorPool.Handle == 0)
			return;

		if (destroyImmediately)
		{
			BackendContext.Resources.DescriptorLifetime.RetireDescriptorPool(descriptorPool);
			return;
		}

		BackendContext.Resources.DescriptorLifetime.RetireDescriptorPool(descriptorPool);
	}

	private void ReleaseDescriptorAllocationResources(
		DescriptorAllocation allocation,
		bool destroyPoolImmediately = false)
	{
		if (allocation.PoolSlabLease is not null)
		{
			BackendContext.Resources.DescriptorLifetime.ReleaseMeshDescriptorPoolSlab(
				allocation.PoolSlabLease,
				allocation.Sets,
				allocation.ActiveSetMask);
			allocation.PoolSlabLease = null;
			return;
		}

		ReleaseDescriptorPool(allocation.Pool, destroyPoolImmediately);
	}

	/// <summary>Emits a Vulkan warning message only on the first occurrence of a given message.</summary>
	private void WarnOnce(string message)
	{
		if (_descriptorWarnings.Add(message))
			Debug.VulkanWarning(message);
	}

    #endregion // Resource Cleanup

    #region Format Conversion Utilities

    /// <summary>
    /// Converts engine component type, count, and integral flag to a Vulkan
    /// <see cref="Format"/>. Defaults to R32 float formats for unrecognized types.
    /// </summary>
    private static Format ToFormat(EComponentType type, uint count, bool integral, bool normalized = false)
		=> (type, count, integral, normalized) switch
		{
			(EComponentType.SByte, 1, false, true) => Format.R8SNorm,
			(EComponentType.SByte, 2, false, true) => Format.R8G8SNorm,
			(EComponentType.SByte, 3, false, true) => Format.R8G8B8SNorm,
			(EComponentType.SByte, 4, false, true) => Format.R8G8B8A8SNorm,
			(EComponentType.Byte, 1, false, true) => Format.R8Unorm,
			(EComponentType.Byte, 2, false, true) => Format.R8G8Unorm,
			(EComponentType.Byte, 3, false, true) => Format.R8G8B8Unorm,
			(EComponentType.Byte, 4, false, true) => Format.R8G8B8A8Unorm,
			(EComponentType.Short, 1, false, true) => Format.R16SNorm,
			(EComponentType.Short, 2, false, true) => Format.R16G16SNorm,
			(EComponentType.Short, 3, false, true) => Format.R16G16B16SNorm,
			(EComponentType.Short, 4, false, true) => Format.R16G16B16A16SNorm,
			(EComponentType.UShort, 1, false, true) => Format.R16Unorm,
			(EComponentType.UShort, 2, false, true) => Format.R16G16Unorm,
			(EComponentType.UShort, 3, false, true) => Format.R16G16B16Unorm,
			(EComponentType.UShort, 4, false, true) => Format.R16G16B16A16Unorm,
			(EComponentType.SByte, 1, _, _) => Format.R8Sint,
			(EComponentType.SByte, 2, _, _) => Format.R8G8Sint,
			(EComponentType.SByte, 3, _, _) => Format.R8G8B8Sint,
			(EComponentType.SByte, 4, _, _) => Format.R8G8B8A8Sint,
			(EComponentType.Byte, 1, _, _) => Format.R8Uint,
			(EComponentType.Byte, 2, _, _) => Format.R8G8Uint,
			(EComponentType.Byte, 3, _, _) => Format.R8G8B8Uint,
			(EComponentType.Byte, 4, _, _) => Format.R8G8B8A8Uint,
			(EComponentType.Short, 1, true, _) => Format.R16Sint,
			(EComponentType.Short, 2, true, _) => Format.R16G16Sint,
			(EComponentType.Short, 3, true, _) => Format.R16G16B16Sint,
			(EComponentType.Short, 4, true, _) => Format.R16G16B16A16Sint,
			(EComponentType.UShort, 1, _, _) => Format.R16Uint,
			(EComponentType.UShort, 2, _, _) => Format.R16G16Uint,
			(EComponentType.UShort, 3, _, _) => Format.R16G16B16Uint,
			(EComponentType.UShort, 4, _, _) => Format.R16G16B16A16Uint,
			(EComponentType.Int, 1, _, _) => Format.R32Sint,
			(EComponentType.Int, 2, _, _) => Format.R32G32Sint,
			(EComponentType.Int, 3, _, _) => Format.R32G32B32Sint,
			(EComponentType.Int, 4, _, _) => Format.R32G32B32A32Sint,
			(EComponentType.UInt, 1, _, _) => Format.R32Uint,
			(EComponentType.UInt, 2, _, _) => Format.R32G32Uint,
			(EComponentType.UInt, 3, _, _) => Format.R32G32B32Uint,
			(EComponentType.UInt, 4, _, _) => Format.R32G32B32A32Uint,
			(EComponentType.Double, 2, _, _) => Format.R64G64Sfloat,
			(EComponentType.Double, 3, _, _) => Format.R64G64B64Sfloat,
			(EComponentType.Double, 4, _, _) => Format.R64G64B64A64Sfloat,
			_ => count switch
			{
				1 => Format.R32Sfloat,
				2 => Format.R32G32Sfloat,
				3 => Format.R32G32B32Sfloat,
				4 => Format.R32G32B32A32Sfloat,
				_ => Format.Undefined
			}
		};

    /// <summary>
    /// Converts engine <see cref="IndexSize"/> to Vulkan <see cref="IndexType"/>.
    /// Byte-sized indices require the VK_EXT_index_type_uint8 extension.
    /// </summary>
    private static IndexType ToVkIndexType(IndexSize size)
		=> size switch
		{
			IndexSize.Byte => IndexType.Uint8Ext,
			IndexSize.TwoBytes => IndexType.Uint16,
			IndexSize.FourBytes => IndexType.Uint32,
			_ => IndexType.Uint16
		};

	#endregion // Format Conversion Utilities
}
