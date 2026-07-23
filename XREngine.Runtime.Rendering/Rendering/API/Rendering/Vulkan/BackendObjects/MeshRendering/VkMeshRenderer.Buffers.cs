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
		private XRDataBuffer? _cachedActiveSkinPaletteBuffer;
		private BufferStructuralIdentity _cachedActiveSkinPaletteIdentity;

		/// <summary>
		/// Gathers all named GPU data buffers from both the Mesh and the MeshRenderer,
		/// converting them to Vulkan-side VkDataBuffer wrappers. MeshRenderer buffers
		/// take priority over mesh buffers for the same name.
		/// </summary>
		private void CollectBuffers()
		{
			lock (_bufferStateSync)
			{
				_bufferCache.Clear();

				var meshBuffers = Mesh?.Buffers as IEventDictionary<string, XRDataBuffer>;
				if (meshBuffers is not null)
				
					foreach (var pair in meshBuffers)
						if (Renderer.GenericToAPI<VkDataBuffer>(pair.Value) is { } vkBuffer)
							_bufferCache[pair.Key] = vkBuffer;

				var rendererBuffers = MeshRenderer.Buffers as IEventDictionary<string, XRDataBuffer>;
				if (rendererBuffers is not null)
					foreach (var pair in rendererBuffers)
						if (Renderer.GenericToAPI<VkDataBuffer>(pair.Value) is { } vkBuffer)
							_bufferCache[pair.Key] = vkBuffer;
							
				FilterRuntimeDeformationSourceBuffers();
				OverrideCollectedSkinPaletteWithActiveSource();
				AddRuntimeDeformationBuffers();
				CaptureRuntimeDeformationBufferReferences();

				bool structuralBindingsChanged = UpdateBufferStructuralIdentitySnapshot();
				if (structuralBindingsChanged)
				{
					_buffersDirty = true;
					_descriptorDirty = true;
					_vertexInputStateDirty = true;
					Renderer.MarkCommandBuffersDirtyForLegacyMeshState();
				}
			}
		}

		private BufferStructuralIdentity CaptureBufferStructuralIdentity(VkDataBuffer? buffer)
		{
			if (buffer is null)
				return default;

			XRDataBuffer data = buffer.Data;
			ulong handle = buffer.BufferHandle?.Handle ?? 0UL;
			return new BufferStructuralIdentity(
				handle,
				Renderer.GetCurrentVulkanResourceGeneration(ObjectType.Buffer, handle),
				buffer.AllocatedByteSize,
				data.BindingIndexOverride ?? uint.MaxValue,
				data.Target,
				data.ComponentType,
				data.ComponentCount,
				data.ElementCount);
		}

		private BufferStructuralIdentity CaptureBufferStructuralIdentity(XRDataBuffer? buffer)
			=> buffer is null
				? default
				: CaptureBufferStructuralIdentity(Renderer.GenericToAPI<VkDataBuffer>(buffer));

		private bool UpdateBufferStructuralIdentitySnapshot()
		{
			bool changed = _bufferStructuralIdentities.Count != _bufferCache.Count;
			foreach ((string name, VkDataBuffer buffer) in _bufferCache)
			{
				BufferStructuralIdentity identity = CaptureBufferStructuralIdentity(buffer);
				if (!_bufferStructuralIdentities.TryGetValue(name, out BufferStructuralIdentity previous) || previous != identity)
					changed = true;
			}

			_bufferStructuralIdentities.Clear();
			foreach ((string name, VkDataBuffer buffer) in _bufferCache)
				_bufferStructuralIdentities[name] = CaptureBufferStructuralIdentity(buffer);
			return changed;
		}

		private void FilterRuntimeDeformationSourceBuffers()
		{
			XRMesh? mesh = Mesh;
			bool allowSkinning = RuntimeEngine.Rendering.Settings.AllowSkinning;
			bool allowBlendshapes = RuntimeEngine.Rendering.Settings.AllowBlendshapes;
			bool hasSkinning = mesh?.HasSkinning == true;
			bool hasBlendshapes = mesh?.BlendshapeCount > 0;
			bool useComputeSkinning = hasSkinning
				&& allowSkinning
				&& RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader
				&& !RuntimeEngine.Rendering.State.IsVulkan;
			bool useVertexSkinning = hasSkinning && allowSkinning && !useComputeSkinning;
			bool useComputeBlendshapes = hasBlendshapes
				&& allowBlendshapes
				&& !RuntimeEngine.Rendering.State.IsVulkan
				&& (RuntimeEngine.Rendering.Settings.CalculateBlendshapesInComputeShader || useComputeSkinning);
			bool useVertexBlendshapes = hasBlendshapes && allowBlendshapes && !useComputeBlendshapes;

			if (!useVertexSkinning)
			{
				RemoveCollectedBuffer(ECommonBufferType.BoneInfluenceCoreIndices.ToString());
				RemoveCollectedBuffer(ECommonBufferType.BoneInfluenceCoreWeights.ToString());
				RemoveCollectedBuffer(ECommonBufferType.BoneInfluenceSpillHeaders.ToString());
				RemoveCollectedBuffer(ECommonBufferType.BoneInfluenceSpillEntries.ToString());
				RemoveCollectedBuffer($"{ECommonBufferType.BoneMatrices}Buffer");
				RemoveCollectedBuffer($"{ECommonBufferType.BoneInvBindMatrices}Buffer");
				RemoveCollectedBuffer($"{ECommonBufferType.SkinPalette}Buffer");
			}
			else if (mesh?.SkinningInfluenceEncoding is SkinningInfluenceEncoding.Core4Spill or SkinningInfluenceEncoding.Core4NoSpill)
			{
				RemoveCollectedBuffer($"{ECommonBufferType.BoneMatrices}Buffer");
				RemoveCollectedBuffer($"{ECommonBufferType.BoneInvBindMatrices}Buffer");
				if (!mesh.HasSpillInfluences)
				{
					RemoveCollectedBuffer(ECommonBufferType.BoneInfluenceSpillHeaders.ToString());
					RemoveCollectedBuffer(ECommonBufferType.BoneInfluenceSpillEntries.ToString());
				}
			}

			if (!useVertexBlendshapes)
			{
				RemoveCollectedBuffer(ECommonBufferType.BlendshapeCount.ToString());
				RemoveCollectedBuffer($"{ECommonBufferType.BlendshapeIndices}Buffer");
				RemoveCollectedBuffer($"{ECommonBufferType.BlendshapeDeltas}Buffer");
				RemoveCollectedBuffer($"{ECommonBufferType.BlendshapeWeights}Buffer");
				RemoveCollectedBuffer($"{ECommonBufferType.BlendshapeActiveWeights}Buffer");
				RemoveCollectedBuffer($"{ECommonBufferType.BlendshapeSparseShapeRanges}Buffer");
				RemoveCollectedBuffer($"{ECommonBufferType.BlendshapeSparseRecords}Buffer");
				RemoveCollectedBuffer($"{ECommonBufferType.BlendshapeQuantizedDeltas}Buffer");
				RemoveCollectedBuffer($"{ECommonBufferType.BlendshapeQuantizationMetadata}Buffer");
			}
		}

		private void RemoveCollectedBuffer(string key)
			=> _bufferCache.Remove(key);

		/// <summary>
		/// Replaces the renderer-owned CPU palette with the active palette source used by
		/// skinning uniforms. GPU-driven systems can publish an external atlas without
		/// mutating the renderer's persistent buffer collection.
		/// </summary>
		private void OverrideCollectedSkinPaletteWithActiveSource()
		{
			string shaderName = $"{ECommonBufferType.SkinPalette}Buffer";
			if (!_bufferCache.ContainsKey(shaderName)
				|| MeshRenderer.ActiveSkinPaletteBuffer is not { } activeSkinPalette)
				return;

			if (Renderer.GenericToAPI<VkDataBuffer>(activeSkinPalette) is { } vkBuffer)
				_bufferCache[shaderName] = vkBuffer;
		}

		private void AddRuntimeDeformationBuffers()
		{
			XRMesh? mesh = MeshRenderer.Mesh;
			bool useComputeSkinning = mesh?.HasSkinning == true
				&& RuntimeEngine.Rendering.Settings.AllowSkinning
				&& RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader
				&& !RuntimeEngine.Rendering.State.IsVulkan;
			bool useComputeBlendshapes = mesh?.BlendshapeCount > 0
				&& RuntimeEngine.Rendering.Settings.AllowBlendshapes
				&& !RuntimeEngine.Rendering.State.IsVulkan
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
				&& !RuntimeEngine.Rendering.State.IsVulkan
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
			_cachedActiveSkinPaletteBuffer = MeshRenderer.ActiveSkinPaletteBuffer;
			_cachedActiveSkinPaletteIdentity = CaptureBufferStructuralIdentity(_cachedActiveSkinPaletteBuffer);
			_cachedHasValidPrecombinedBlendshapeDeltas = MeshRenderer.HasValidPrecombinedBlendshapeDeltas;
			_cachedSkinnedPositionsIdentity = CaptureBufferStructuralIdentity(MeshRenderer.SkinnedPositionsBuffer);
			_cachedSkinnedNormalsIdentity = CaptureBufferStructuralIdentity(MeshRenderer.SkinnedNormalsBuffer);
			_cachedSkinnedTangentsIdentity = CaptureBufferStructuralIdentity(MeshRenderer.SkinnedTangentsBuffer);
			_cachedSkinnedInterleavedIdentity = CaptureBufferStructuralIdentity(MeshRenderer.SkinnedInterleavedBuffer);
			_cachedPrecombinedBlendshapePositionsIdentity = CaptureBufferStructuralIdentity(MeshRenderer.PrecombinedBlendshapePositionsBuffer);
			_cachedPrecombinedBlendshapeNormalsIdentity = CaptureBufferStructuralIdentity(MeshRenderer.PrecombinedBlendshapeNormalsBuffer);
			_cachedPrecombinedBlendshapeTangentsIdentity = CaptureBufferStructuralIdentity(MeshRenderer.PrecombinedBlendshapeTangentsBuffer);
		}

		private bool RuntimeDeformationBufferReferencesChanged()
			=> !ReferenceEquals(_cachedActiveSkinPaletteBuffer, MeshRenderer.ActiveSkinPaletteBuffer)
			|| _cachedActiveSkinPaletteIdentity != CaptureBufferStructuralIdentity(MeshRenderer.ActiveSkinPaletteBuffer)
			|| _cachedSkinnedPositionsIdentity != CaptureBufferStructuralIdentity(MeshRenderer.SkinnedPositionsBuffer)
			|| _cachedSkinnedNormalsIdentity != CaptureBufferStructuralIdentity(MeshRenderer.SkinnedNormalsBuffer)
			|| _cachedSkinnedTangentsIdentity != CaptureBufferStructuralIdentity(MeshRenderer.SkinnedTangentsBuffer)
			|| _cachedSkinnedInterleavedIdentity != CaptureBufferStructuralIdentity(MeshRenderer.SkinnedInterleavedBuffer)
			|| _cachedPrecombinedBlendshapePositionsIdentity != CaptureBufferStructuralIdentity(MeshRenderer.PrecombinedBlendshapePositionsBuffer)
			|| _cachedPrecombinedBlendshapeNormalsIdentity != CaptureBufferStructuralIdentity(MeshRenderer.PrecombinedBlendshapeNormalsBuffer)
			|| _cachedPrecombinedBlendshapeTangentsIdentity != CaptureBufferStructuralIdentity(MeshRenderer.PrecombinedBlendshapeTangentsBuffer)
			|| _cachedHasValidPrecombinedBlendshapeDeltas != MeshRenderer.HasValidPrecombinedBlendshapeDeltas;

		private void EnsureRuntimeDeformationBuffersCurrent()
		{
			lock (_bufferStateSync)
			{
				if (RuntimeDeformationBufferReferencesChanged())
					CollectBuffers();
			}
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
			lock (_bufferStateSync)
			{
				EnsureRuntimeDeformationBuffersCurrent();

				if (skipIndexBuffers)
				{
					ClearIndexBufferBindings();
					_indexBuffersSkippedForShaderGeneratedVertices = true;
				}

				bool needIndexRebuild = _indexBuffersSkippedForShaderGeneratedVertices && !skipIndexBuffers;
				if (!_buffersDirty && !needIndexRebuild && AreCachedBuffersReadyForRendering(out _, skipIndexBuffers))
					return;

				if (!_buffersDirty)
				{
					_descriptorDirty = true;
					Renderer.MarkCommandBuffersDirtyForLegacyMeshState();
				}

				bool allowSynchronousBufferUpload = Renderer.AllowSynchronousResourceUploads;
				foreach (var buffer in _bufferCache.Values)
					buffer.TryEnsureReadyForRendering(allowSynchronousBufferUpload);

				if (skipIndexBuffers)
				{
					ClearIndexBufferBindings();
					_indexBuffersSkippedForShaderGeneratedVertices = true;
				}
				else if (_triangleIndexBufferExternallyProvided)
				{
					_triangleIndexBuffer?.TryEnsureReadyForRendering(allowSynchronousBufferUpload);
					_lineIndexBuffer = null;
					_pointIndexBuffer = null;
					_indexBuffersSkippedForShaderGeneratedVertices = false;
				}
				else if (Mesh is not null)
				{
					_triangleIndexBufferExternallyProvided = false;
					var tri = GetIndexBufferForBinding(EPrimitiveType.Triangles, out _triangleIndexSize, _triangleIndexBuffer);
					_triangleIndexBuffer = tri is not null ? Renderer.GenericToAPI<VkDataBuffer>(tri) : null;
					_triangleIndexBuffer?.TryEnsureReadyForRendering(allowSynchronousBufferUpload);

					var line = GetIndexBufferForBinding(EPrimitiveType.Lines, out _lineIndexSize, _lineIndexBuffer);
					_lineIndexBuffer = line is not null ? Renderer.GenericToAPI<VkDataBuffer>(line) : null;
					_lineIndexBuffer?.TryEnsureReadyForRendering(allowSynchronousBufferUpload);

					var point = GetIndexBufferForBinding(EPrimitiveType.Points, out _pointIndexSize, _pointIndexBuffer);
					_pointIndexBuffer = point is not null ? Renderer.GenericToAPI<VkDataBuffer>(point) : null;
					_pointIndexBuffer?.TryEnsureReadyForRendering(allowSynchronousBufferUpload);
					_indexBuffersSkippedForShaderGeneratedVertices = false;
				}
				else
				{
					ClearIndexBufferBindings();
					_indexBuffersSkippedForShaderGeneratedVertices = false;
				}

				_buffersDirty = false;
				_vertexInputStateDirty = true;
			}
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
			_triangleIndexBufferExternallyProvided = false;
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
			lock (_bufferStateSync)
			{
				_buffersDirty = true;
				_pipelineDirty = true;
				_descriptorDirty = true;
				_vertexInputStateDirty = true;
				_geometryLayoutSignature = MeshGeometryLayoutSignature.Empty;
				Renderer.MarkCommandBuffersDirtyForLegacyMeshState();
			}
		}

		/// <summary>
		/// Overrides the triangle index buffer binding used for indexed draws.
		/// This is used by indirect-renderer atlas sync paths where indices are provided externally.
		/// </summary>
		internal bool SetTriangleIndexBuffer(VkDataBuffer? buffer, IndexSize elementType)
		{
			lock (_bufferStateSync)
			{
				bool changed = CaptureBufferStructuralIdentity(_triangleIndexBuffer) != CaptureBufferStructuralIdentity(buffer) ||
					_triangleIndexSize != elementType;
				_triangleIndexBuffer = buffer;
				_triangleIndexSize = elementType;
				_triangleIndexBufferExternallyProvided = buffer is not null;
				_indexBuffersSkippedForShaderGeneratedVertices = false;
				if (changed)
					_vertexInputStateDirty = true;
				_triangleIndexBuffer?.TryEnsureReadyForRendering(Renderer.AllowSynchronousResourceUploads);
				return changed;
			}
		}

		/// <summary>
		/// Returns the first available index buffer in primitive priority order:
		/// triangles, then lines, then points.
		/// </summary>
		internal bool TryGetPrimaryIndexBufferInfo(out IndexSize indexElementSize, out uint indexCount)
		{
			lock (_bufferStateSync)
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
		}

		internal bool TryGetPrimaryIndexBinding(out VkBufferHandle handle, out IndexType indexType, out uint indexCount)
		{
			lock (_bufferStateSync)
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
		}

		private bool TryResolveIndexBinding(VkDataBuffer? buffer, IndexSize size, out VkBufferHandle handle, out IndexType indexType, out uint indexCount)
		{
			handle = default;
			indexType = IndexType.Uint32;
			indexCount = 0;

			if (!HasIndexData(buffer))
				return false;

			if (!buffer!.TryEnsureReadyForRendering(Renderer.AllowSynchronousResourceUploads))
				return false;
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
	}
}
