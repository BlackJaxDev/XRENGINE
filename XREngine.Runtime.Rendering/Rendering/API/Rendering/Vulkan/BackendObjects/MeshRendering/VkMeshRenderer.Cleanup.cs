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

public unsafe partial class VulkanRenderer
{
	public partial class VkMeshRenderer
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
				return;
			}

			foreach (AutoUniformBuffer[] buffers in _autoUniformBuffers.Values)
				DestroyAutoUniformBufferArray(buffers);

			_autoUniformBuffers.Clear();
		}

		private void DestroyEngineUniformBufferArray(EngineUniformBuffer[] buffers)
		{
			foreach (EngineUniformBuffer buf in buffers)
			{
				if (buf.OwnsBuffer)
					DestroyMappedUniformBuffer(buf.Buffer, buf.Memory, buf.MappedPtr);
			}
		}

		private void DestroyAutoUniformBufferArray(AutoUniformBuffer[] buffers)
		{
			foreach (AutoUniformBuffer buf in buffers)
			{
				if (buf.OwnsBuffer)
					DestroyMappedUniformBuffer(buf.Buffer, buf.Memory, buf.MappedPtr);
			}
		}

		private void DestroyMappedUniformBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory, void* mappedPtr)
		{
			if (mappedPtr != null)
				Renderer.UnmapBufferMemory(buffer, memory);

			Renderer.DestroyTrackedMeshUniformBuffer(buffer, memory);
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
			if (!Renderer.ReleaseSharedMeshDescriptorAllocation(key, allocation))
				return;

			ReleaseDescriptorOwnershipTelemetry(allocation);
			ReleaseDescriptorAllocationResources(allocation, destroyPoolImmediately);
		}

		private void ReleaseDescriptorPool(DescriptorPool descriptorPool, bool destroyImmediately = false)
		{
			if (descriptorPool.Handle == 0)
				return;

			if (destroyImmediately)
			{
				Renderer.RetireDescriptorPool(descriptorPool);
				return;
			}

			Renderer.RetireDescriptorPool(descriptorPool);
		}

		private void ReleaseDescriptorAllocationResources(
			DescriptorAllocation allocation,
			bool destroyPoolImmediately = false)
		{
			if (allocation.PoolSlabLease is not null)
			{
				Renderer.ReleaseMeshDescriptorPoolSlab(
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
}
