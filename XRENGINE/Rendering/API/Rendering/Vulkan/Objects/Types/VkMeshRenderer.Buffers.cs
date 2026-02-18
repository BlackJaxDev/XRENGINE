// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.Buffers.cs  – partial class: Buffer & Material Resolution
//
// Gathers GPU data buffers from mesh/renderer, resolves index buffers, and
// determines the effective material for each draw call.
// ──────────────────────────────────────────────────────────────────────────────

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
	public partial class VkMeshRenderer
	{
		#region Buffer Management

		/// <summary>
		/// Gathers all named GPU data buffers from both the Mesh and the MeshRenderer,
		/// converting them to Vulkan-side VkDataBuffer wrappers. MeshRenderer buffers
		/// take priority over mesh buffers for the same name.
		/// </summary>
		private void CollectBuffers()
		{
			_bufferCache.Clear();

			var meshBuffers = Mesh?.Buffers as IEventDictionary<string, XRDataBuffer>;
			if (meshBuffers is not null)
			{
				foreach (var pair in meshBuffers)
				{
					if (Renderer.GenericToAPI<VkDataBuffer>(pair.Value) is { } vkBuffer)
						_bufferCache[pair.Key] = vkBuffer;
				}
			}

			var rendererBuffers = MeshRenderer.Buffers as IEventDictionary<string, XRDataBuffer>;
			if (rendererBuffers is not null)
			{
				foreach (var pair in rendererBuffers)
				{
					if (Renderer.GenericToAPI<VkDataBuffer>(pair.Value) is { } vkBuffer)
						_bufferCache[pair.Key] = vkBuffer;
				}
			}

			_buffersDirty = true;
			_descriptorDirty = true;
		}

		/// <summary>
		/// Lazily generates (uploads) all cached buffers and resolves index buffers
		/// for triangles, lines, and points. No-ops if buffers are already up-to-date.
		/// </summary>
		private void EnsureBuffers()
		{
			if (!_buffersDirty)
				return;

			foreach (var buffer in _bufferCache.Values)
				buffer.Generate();

			if (Mesh is not null)
			{
				var tri = Mesh.GetIndexBuffer(EPrimitiveType.Triangles, out _triangleIndexSize);
				_triangleIndexBuffer = tri is not null ? Renderer.GenericToAPI<VkDataBuffer>(tri) : null;
				_triangleIndexBuffer?.Generate();

				var line = Mesh.GetIndexBuffer(EPrimitiveType.Lines, out _lineIndexSize);
				_lineIndexBuffer = line is not null ? Renderer.GenericToAPI<VkDataBuffer>(line) : null;
				_lineIndexBuffer?.Generate();

				var point = Mesh.GetIndexBuffer(EPrimitiveType.Points, out _pointIndexSize);
				_pointIndexBuffer = point is not null ? Renderer.GenericToAPI<VkDataBuffer>(point) : null;
				_pointIndexBuffer?.Generate();
			}

			_buffersDirty = false;
		}

		/// <summary>
		/// Overrides the triangle index buffer binding used for indexed draws.
		/// This is used by indirect-renderer atlas sync paths where indices are provided externally.
		/// </summary>
		internal void SetTriangleIndexBuffer(VkDataBuffer? buffer, IndexSize elementType)
		{
			_triangleIndexBuffer = buffer;
			_triangleIndexSize = elementType;
			_triangleIndexBuffer?.Generate();
		}

		/// <summary>
		/// Returns the first available index buffer in primitive priority order:
		/// triangles, then lines, then points.
		/// </summary>
		internal bool TryGetPrimaryIndexBufferInfo(out IndexSize indexElementSize, out uint indexCount)
		{
			EnsureBuffers();

			if (HasIndexData(_triangleIndexBuffer))
			{
				indexElementSize = _triangleIndexSize;
				indexCount = _triangleIndexBuffer!.Data.ElementCount;
				return true;
			}

			if (HasIndexData(_lineIndexBuffer))
			{
				indexElementSize = _lineIndexSize;
				indexCount = _lineIndexBuffer!.Data.ElementCount;
				return true;
			}

			if (HasIndexData(_pointIndexBuffer))
			{
				indexElementSize = _pointIndexSize;
				indexCount = _pointIndexBuffer!.Data.ElementCount;
				return true;
			}

			indexElementSize = IndexSize.FourBytes;
			indexCount = 0;
			return false;
		}

		internal bool TryGetPrimaryIndexBinding(out VkBufferHandle handle, out IndexType indexType, out uint indexCount)
		{
			EnsureBuffers();

			if (TryResolveIndexBinding(_triangleIndexBuffer, _triangleIndexSize, out handle, out indexType, out indexCount))
				return true;

			if (TryResolveIndexBinding(_lineIndexBuffer, _lineIndexSize, out handle, out indexType, out indexCount))
				return true;

			if (TryResolveIndexBinding(_pointIndexBuffer, _pointIndexSize, out handle, out indexType, out indexCount))
				return true;

			handle = default;
			indexType = IndexType.Uint32;
			indexCount = 0;
			return false;
		}

		private static bool TryResolveIndexBinding(VkDataBuffer? buffer, IndexSize size, out VkBufferHandle handle, out IndexType indexType, out uint indexCount)
		{
			handle = default;
			indexType = IndexType.Uint32;
			indexCount = 0;

			if (!HasIndexData(buffer))
				return false;

			buffer!.Generate();
			if (buffer.BufferHandle is not { } bufferHandle)
				return false;

			handle = bufferHandle;
			indexType = ToVkIndexType(size);
			indexCount = buffer.Data.ElementCount;
			return true;
		}

		/// <summary>Checks whether an index buffer has valid, non-empty data.</summary>
		private static bool HasIndexData(VkDataBuffer? buffer)
			=> buffer is not null && buffer.Data.ElementCount > 0;

		#endregion // Buffer Management

		#region Material Resolution

		/// <summary>
		/// Resolves the effective material for a draw call by checking overrides
		/// in priority order: global override > pipeline override > local override >
		/// MeshRenderer.Material > pipeline invalid material > fallback.
		/// </summary>
		private XRMaterial ResolveMaterial(XRMaterial? localOverride)
		{
			var renderState = Engine.Rendering.State.RenderingPipelineState;
			var globalMaterialOverride = renderState?.GlobalMaterialOverride;
			var pipelineOverride = renderState?.OverrideMaterial;

			return (globalMaterialOverride
					?? pipelineOverride
					?? localOverride
					?? MeshRenderer.Material
					?? Engine.Rendering.State.CurrentRenderingPipeline?.InvalidMaterial
					?? XRMaterial.InvalidMaterial)
				   ?? new XRMaterial();
		}

		#endregion // Material Resolution
	}
}
