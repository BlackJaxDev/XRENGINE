// ──────────────────────────────────────────────────────────────────────────────
// VkMeshRenderer.Buffers.cs  – partial class: Buffer & Material Resolution
//
// Gathers GPU data buffers from mesh/renderer, resolves index buffers, and
// determines the effective material for each draw call.
// ──────────────────────────────────────────────────────────────────────────────

using System;

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

			AddRuntimeDeformationBuffers();
			CaptureRuntimeDeformationBufferReferences();

			_buffersDirty = true;
			_descriptorDirty = true;
			Renderer.MarkCommandBuffersDirty();
		}

		private void AddRuntimeDeformationBuffers()
		{
			XRMesh? mesh = MeshRenderer.Mesh;
			bool useComputeSkinning = mesh?.HasSkinning == true
				&& RuntimeEngine.Rendering.Settings.AllowSkinning
				&& RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader;
			bool useComputeBlendshapes = mesh?.BlendshapeCount > 0
				&& RuntimeEngine.Rendering.Settings.AllowBlendshapes
				&& (RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || useComputeSkinning);

			if (useComputeSkinning || useComputeBlendshapes)
			{
				AddRuntimeBuffer(ComputeInterleavedBufferName, MeshRenderer.SkinnedInterleavedBuffer, ComputeInterleavedBinding);
				AddRuntimeBuffer(ComputePositionBufferName, MeshRenderer.SkinnedPositionsBuffer, ComputePositionBinding);
				AddRuntimeBuffer(ComputeNormalBufferName, MeshRenderer.SkinnedNormalsBuffer, ComputeNormalBinding);
				AddRuntimeBuffer(ComputeTangentBufferName, MeshRenderer.SkinnedTangentsBuffer, ComputeTangentBinding);
				AddMeshDeformSourceBuffers();
			}

			bool directBlendshapePath = mesh?.BlendshapeCount > 0
				&& RuntimeEngine.Rendering.Settings.AllowBlendshapes
				&& !useComputeBlendshapes;
			if (directBlendshapePath
				&& RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass
				&& MeshRenderer.HasValidPrecombinedBlendshapeDeltas)
			{
				AddRuntimeBuffer(PrecombinedBlendshapePositionBufferName, MeshRenderer.PrecombinedBlendshapePositionsBuffer, PrecombinedBlendshapePositionBinding);
				if (mesh?.HasNormals == true)
					AddRuntimeBuffer(PrecombinedBlendshapeNormalBufferName, MeshRenderer.PrecombinedBlendshapeNormalsBuffer, PrecombinedBlendshapeNormalBinding);
				if (mesh?.HasTangents == true)
					AddRuntimeBuffer(PrecombinedBlendshapeTangentBufferName, MeshRenderer.PrecombinedBlendshapeTangentsBuffer, PrecombinedBlendshapeTangentBinding);
			}
		}

		private void AddMeshDeformSourceBuffers()
		{
			if (MeshRenderer.DeformerPositionsBuffer is null || MeshRenderer.DeformMeshRenderer is null || MeshRenderer.MeshDeformInfluences is null)
				return;

			XRMeshRenderer deformerRenderer = MeshRenderer.DeformMeshRenderer;
			if (deformerRenderer.SkinnedInterleavedBuffer is not null)
			{
				Debug.VulkanWarningEvery(
					$"Vulkan.MeshDeform.InterleavedSourceAlias.{MeshRenderer.Name ?? "UnnamedRenderer"}",
					TimeSpan.FromSeconds(2),
					"[Vulkan] Mesh deform source aliases are skipped for renderer='{0}' because the deformer output is interleaved.",
					MeshRenderer.Name ?? "<unnamed renderer>");
				return;
			}

			AddRuntimeBuffer("DeformerPositionsBuffer", deformerRenderer.SkinnedPositionsBuffer, 0u, assignBindingOverride: false);

			uint nextBinding = 2u;
			if (MeshRenderer.DeformerNormalsBuffer is not null)
				AddRuntimeBuffer("DeformerNormalsBuffer", deformerRenderer.SkinnedNormalsBuffer, nextBinding++, assignBindingOverride: false);
			if (MeshRenderer.DeformerTangentsBuffer is not null)
				AddRuntimeBuffer("DeformerTangentsBuffer", deformerRenderer.SkinnedTangentsBuffer, nextBinding, assignBindingOverride: false);
		}

		private void AddRuntimeBuffer(string shaderName, XRDataBuffer? dataBuffer, uint binding, bool assignBindingOverride = true)
		{
			if (dataBuffer is null)
				return;

			if (assignBindingOverride)
				dataBuffer.BindingIndexOverride = binding;
			if (Renderer.GenericToAPI<VkDataBuffer>(dataBuffer) is { } vkBuffer)
				_bufferCache[shaderName] = vkBuffer;
		}

		private void CaptureRuntimeDeformationBufferReferences()
		{
			_cachedSkinnedPositionsBuffer = MeshRenderer.SkinnedPositionsBuffer;
			_cachedSkinnedNormalsBuffer = MeshRenderer.SkinnedNormalsBuffer;
			_cachedSkinnedTangentsBuffer = MeshRenderer.SkinnedTangentsBuffer;
			_cachedSkinnedInterleavedBuffer = MeshRenderer.SkinnedInterleavedBuffer;
			_cachedPrecombinedBlendshapePositionsBuffer = MeshRenderer.PrecombinedBlendshapePositionsBuffer;
			_cachedPrecombinedBlendshapeNormalsBuffer = MeshRenderer.PrecombinedBlendshapeNormalsBuffer;
			_cachedPrecombinedBlendshapeTangentsBuffer = MeshRenderer.PrecombinedBlendshapeTangentsBuffer;
			_cachedSkinnedOutputVersion = MeshRenderer.SkinnedOutputVersion;
		}

		private bool RuntimeDeformationBufferReferencesChanged()
			=> !ReferenceEquals(_cachedSkinnedPositionsBuffer, MeshRenderer.SkinnedPositionsBuffer)
			|| !ReferenceEquals(_cachedSkinnedNormalsBuffer, MeshRenderer.SkinnedNormalsBuffer)
			|| !ReferenceEquals(_cachedSkinnedTangentsBuffer, MeshRenderer.SkinnedTangentsBuffer)
			|| !ReferenceEquals(_cachedSkinnedInterleavedBuffer, MeshRenderer.SkinnedInterleavedBuffer)
			|| !ReferenceEquals(_cachedPrecombinedBlendshapePositionsBuffer, MeshRenderer.PrecombinedBlendshapePositionsBuffer)
			|| !ReferenceEquals(_cachedPrecombinedBlendshapeNormalsBuffer, MeshRenderer.PrecombinedBlendshapeNormalsBuffer)
			|| !ReferenceEquals(_cachedPrecombinedBlendshapeTangentsBuffer, MeshRenderer.PrecombinedBlendshapeTangentsBuffer)
			|| _cachedSkinnedOutputVersion != MeshRenderer.SkinnedOutputVersion;

		private void EnsureRuntimeDeformationBuffersCurrent()
		{
			if (RuntimeDeformationBufferReferencesChanged())
				CollectBuffers();
		}

		/// <summary>
		/// Lazily generates (uploads) all cached buffers and resolves index buffers
		/// for triangles, lines, and points. No-ops if buffers are already up-to-date.
		/// Index buffer construction is asynchronous — on first call the mesh kicks off a
		/// background Task.Run to build the buffer and returns null; the callback below
		/// flips <see cref="_buffersDirty"/> back to true so the next EnsureBuffers call
		/// picks up the now-cached buffer without stalling the render thread.
		/// </summary>
		private void EnsureBuffers(bool skipIndexBuffers = false)
		{
			EnsureRuntimeDeformationBuffersCurrent();

			if (skipIndexBuffers)
			{
				ClearIndexBufferBindings();
				_indexBuffersSkippedForShaderGeneratedVertices = true;
			}

			bool needIndexRebuild = _indexBuffersSkippedForShaderGeneratedVertices && !skipIndexBuffers;
			if (!_buffersDirty && !needIndexRebuild && AreCachedBuffersReadyForRendering(out _))
				return;

			if (!_buffersDirty)
			{
				_descriptorDirty = true;
				Renderer.MarkCommandBuffersDirty();
			}

			foreach (var buffer in _bufferCache.Values)
				buffer.EnsureReadyForRendering();

			if (skipIndexBuffers)
			{
				ClearIndexBufferBindings();
				_indexBuffersSkippedForShaderGeneratedVertices = true;
			}
			else if (Mesh is not null)
			{
				var tri = GetIndexBufferForBinding(EPrimitiveType.Triangles, out _triangleIndexSize, _triangleIndexBuffer);
				_triangleIndexBuffer = tri is not null ? Renderer.GenericToAPI<VkDataBuffer>(tri) : null;
				_triangleIndexBuffer?.EnsureReadyForRendering();

				var line = GetIndexBufferForBinding(EPrimitiveType.Lines, out _lineIndexSize, _lineIndexBuffer);
				_lineIndexBuffer = line is not null ? Renderer.GenericToAPI<VkDataBuffer>(line) : null;
				_lineIndexBuffer?.EnsureReadyForRendering();

				var point = GetIndexBufferForBinding(EPrimitiveType.Points, out _pointIndexSize, _pointIndexBuffer);
				_pointIndexBuffer = point is not null ? Renderer.GenericToAPI<VkDataBuffer>(point) : null;
				_pointIndexBuffer?.EnsureReadyForRendering();
				_indexBuffersSkippedForShaderGeneratedVertices = false;
			}
			else
			{
				ClearIndexBufferBindings();
				_indexBuffersSkippedForShaderGeneratedVertices = false;
			}

			_buffersDirty = false;
		}

		private XRDataBuffer? GetIndexBufferForBinding(EPrimitiveType type, out IndexSize elementSize, VkDataBuffer? currentBinding)
		{
			if (Mesh is not { } mesh)
			{
				elementSize = IndexSize.TwoBytes;
				return null;
			}

			Action<XRDataBuffer, IndexSize>? onReady =
				currentBinding is null && !mesh.HasCachedIndexBuffer(type)
					? OnAsyncIndexBufferReady
					: null;

			return mesh.GetIndexBuffer(type, out elementSize, EBufferTarget.ElementArrayBuffer, onReady);
		}

		private void ClearIndexBufferBindings()
		{
			_triangleIndexBuffer = null;
			_lineIndexBuffer = null;
			_pointIndexBuffer = null;
			_triangleIndexSize = IndexSize.FourBytes;
			_lineIndexSize = IndexSize.FourBytes;
			_pointIndexSize = IndexSize.FourBytes;
		}

		private void OnAsyncIndexBufferReady(XRDataBuffer buffer, IndexSize elementSize)
		{
			RuntimeRenderingHostServices.Current.EnqueueRenderThreadTask(
				MarkIndexBuffersDirty,
				"VkMeshRenderer.AsyncIndexBufferReady",
				RenderThreadJobKind.MeshUpload);
		}

		private void MarkIndexBuffersDirty()
		{
			_buffersDirty = true;
			_pipelineDirty = true;
			_descriptorDirty = true;
			_geometryLayoutSignature = MeshGeometryLayoutSignature.Empty;
			Renderer.MarkCommandBuffersDirty();
		}

		/// <summary>
		/// Overrides the triangle index buffer binding used for indexed draws.
		/// This is used by indirect-renderer atlas sync paths where indices are provided externally.
		/// </summary>
		internal void SetTriangleIndexBuffer(VkDataBuffer? buffer, IndexSize elementType)
		{
			_triangleIndexBuffer = buffer;
			_triangleIndexSize = elementType;
			_triangleIndexBuffer?.EnsureReadyForRendering();
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

			buffer!.EnsureReadyForRendering();
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
		private XRMaterial ResolveMaterial(XRMaterial? localOverride, uint instances)
			=> MeshRenderMaterialResolver.Resolve(
				MeshRenderer,
				localOverride,
				instances,
				RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.InvalidMaterial).Material;

		#endregion // Material Resolution
	}
}
