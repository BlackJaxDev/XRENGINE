// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.Cleanup.cs  – partial class: Resource Cleanup & Format Conversion
//
// Destroys per-frame engine and auto uniform buffers, descriptor pools, and
// provides Vulkan format / index-type conversion utilities.
// ──────────────────────────────────────────────────────────────────────────────

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
					{
						foreach (EngineUniformBuffer buf in toDestroy)
						{
							Renderer.DestroyTrackedMeshUniformBuffer(buf.Buffer, buf.Memory);
						}
					}

					_engineUniformBuffers.Remove(singleName);
					return;
				}

				foreach (EngineUniformBuffer[] buffers in _engineUniformBuffers.Values)
				{
					foreach (EngineUniformBuffer buf in buffers)
					{
						Renderer.DestroyTrackedMeshUniformBuffer(buf.Buffer, buf.Memory);
					}
				}

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
					{
						foreach (AutoUniformBuffer buf in toDestroy)
						{
							Renderer.DestroyTrackedMeshUniformBuffer(buf.Buffer, buf.Memory);
						}
					}

				_autoUniformBuffers.Remove(singleName);
				return;
			}

			foreach (AutoUniformBuffer[] buffers in _autoUniformBuffers.Values)
			{
				foreach (AutoUniformBuffer buf in buffers)
				{
					Renderer.DestroyTrackedMeshUniformBuffer(buf.Buffer, buf.Memory);
				}
			}

			_autoUniformBuffers.Clear();
		}

		/// <summary>
		/// Destroys all descriptor sets, the descriptor pool, and all uniform buffers.
		/// Called when pipelines are torn down or the renderer is unlinked.
		/// </summary>
		private void DestroyDescriptors()
		{
			if (_descriptorSets is not null)
				_descriptorSets = null;
			_descriptorSchemaFingerprint = 0;

			DestroyEngineUniformBuffers();
			DestroyAutoUniformBuffers();

			if (_descriptorPool.Handle != 0)
			{
				Api!.DestroyDescriptorPool(Device, _descriptorPool, null);
				_descriptorPool = default;
			}
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
		private static Format ToFormat(EComponentType type, uint count, bool integral)
		{
			return (type, count, integral) switch
			{
				(EComponentType.SByte, 1, _) => Format.R8Sint,
				(EComponentType.SByte, 2, _) => Format.R8G8Sint,
				(EComponentType.SByte, 3, _) => Format.R8G8B8Sint,
				(EComponentType.SByte, 4, _) => Format.R8G8B8A8Sint,
				(EComponentType.Byte, 1, _) => Format.R8Uint,
				(EComponentType.Byte, 2, _) => Format.R8G8Uint,
				(EComponentType.Byte, 3, _) => Format.R8G8B8Uint,
				(EComponentType.Byte, 4, _) => Format.R8G8B8A8Uint,
				(EComponentType.Short, 1, true) => Format.R16Sint,
				(EComponentType.Short, 2, true) => Format.R16G16Sint,
				(EComponentType.Short, 3, true) => Format.R16G16B16Sint,
				(EComponentType.Short, 4, true) => Format.R16G16B16A16Sint,
				(EComponentType.UShort, 1, _) => Format.R16Uint,
				(EComponentType.UShort, 2, _) => Format.R16G16Uint,
				(EComponentType.UShort, 3, _) => Format.R16G16B16Uint,
				(EComponentType.UShort, 4, _) => Format.R16G16B16A16Uint,
				(EComponentType.Int, 1, _) => Format.R32Sint,
				(EComponentType.Int, 2, _) => Format.R32G32Sint,
				(EComponentType.Int, 3, _) => Format.R32G32B32Sint,
				(EComponentType.Int, 4, _) => Format.R32G32B32A32Sint,
				(EComponentType.UInt, 1, _) => Format.R32Uint,
				(EComponentType.UInt, 2, _) => Format.R32G32Uint,
				(EComponentType.UInt, 3, _) => Format.R32G32B32Uint,
				(EComponentType.UInt, 4, _) => Format.R32G32B32A32Uint,
				(EComponentType.Double, 2, _) => Format.R64G64Sfloat,
				(EComponentType.Double, 3, _) => Format.R64G64B64Sfloat,
				(EComponentType.Double, 4, _) => Format.R64G64B64A64Sfloat,
				_ => count switch
				{
					1 => Format.R32Sfloat,
					2 => Format.R32G32Sfloat,
					3 => Format.R32G32B32Sfloat,
					4 => Format.R32G32B32A32Sfloat,
					_ => Format.Undefined
				}
			};
		}

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
