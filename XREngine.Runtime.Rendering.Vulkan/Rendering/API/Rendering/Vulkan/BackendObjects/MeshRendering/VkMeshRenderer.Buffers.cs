// â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
// VkMeshRenderer.Buffers.cs  â€“ partial class: Buffer & Material Resolution
//
// Gathers GPU data buffers from mesh/renderer, resolves index buffers, and
// determines the effective material for each draw call.
// â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

using System;
using System.Collections.Generic;
using System.Threading;

using Silk.NET.Vulkan;

using XREngine;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
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
					_bufferCache[pair.Key] = ProgramCreationPort.GetOrCreateBuffer(pair.Value, generateNow: false);

			var rendererBuffers = MeshRenderer.Buffers as IEventDictionary<string, XRDataBuffer>;
			if (rendererBuffers is not null)
				foreach (var pair in rendererBuffers)
					_bufferCache[pair.Key] = ProgramCreationPort.GetOrCreateBuffer(pair.Value, generateNow: false);

			FilterRuntimeDeformationSourceBuffers();
			OverrideCollectedSkinPaletteWithActiveSource();
			AddRuntimeDeformationBuffers();
			CaptureRuntimeDeformationBufferReferences();

			bool structuralBindingsChanged = UpdateBufferStructuralIdentitySnapshot();
			PublishCachedBufferResourceFingerprint();
			if (structuralBindingsChanged)
			{
				BumpPreparationCompatibilityRevision();
				_buffersDirty = true;
				_descriptorDirty = true;
				_vertexInputStateDirty = true;
				CommandOperations.MarkCommandBuffersDirtyForLegacyMeshState();
			}

			PublishBufferReadinessSnapshot();
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
			GetResourceGeneration(ObjectType.Buffer, handle),
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
			: CaptureBufferStructuralIdentity(ProgramCreationPort.GetOrCreateBuffer(buffer, generateNow: false));

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
		XRMeshSkinningBufferState? skinningState = mesh?.GetSkinningBufferStateSnapshot();
		bool allowSkinning = RuntimeEngine.Rendering.Settings.AllowSkinning;
		bool allowBlendshapes = RuntimeEngine.Rendering.Settings.AllowBlendshapes;
		bool hasSkinning = skinningState?.UtilizedBones.Length > 0;
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
		else if (skinningState?.InfluenceEncoding is SkinningInfluenceEncoding.Core4Spill or SkinningInfluenceEncoding.Core4NoSpill)
		{
			RemoveCollectedBuffer($"{ECommonBufferType.BoneMatrices}Buffer");
			RemoveCollectedBuffer($"{ECommonBufferType.BoneInvBindMatrices}Buffer");
			if (!skinningState.HasSpillInfluences)
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

		_bufferCache[shaderName] = ProgramCreationPort.GetOrCreateBuffer(activeSkinPalette, generateNow: false);
	}

	private void AddRuntimeDeformationBuffers()
	{
		XRMesh? mesh = MeshRenderer.Mesh;
		XRMeshRenderer.SkinnedOutputResourceSnapshot outputState =
			MeshRenderer.CaptureSkinnedOutputResources();
		XRMeshRenderer.BlendshapeResourceSnapshot blendshapeState =
			MeshRenderer.CaptureBlendshapeResources();
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
			AddRuntimeBuffer(ComputeInterleavedBufferName, outputState.Interleaved, ComputeInterleavedBinding);
			AddRuntimeBuffer(ComputePositionBufferName, outputState.Positions, ComputePositionBinding);
			AddRuntimeBuffer(ComputeNormalBufferName, outputState.Normals, ComputeNormalBinding);
			AddRuntimeBuffer(ComputeTangentBufferName, outputState.Tangents, ComputeTangentBinding);
			AddMeshDeformSourceBuffers();
		}

		bool directBlendshapePath = mesh?.BlendshapeCount > 0
			&& RuntimeEngine.Rendering.Settings.AllowBlendshapes
			&& !useComputeBlendshapes;
		if (directBlendshapePath
			&& RuntimeEngine.Rendering.Settings.EnableBlendshapePrecombinePass
			&& !RuntimeEngine.Rendering.State.IsVulkan
			&& mesh is not null
			&& blendshapeState.IsPrecombinedValidFor(mesh))
		{
			AddRuntimeBuffer(PrecombinedBlendshapePositionBufferName, blendshapeState.PrecombinedPositions, PrecombinedBlendshapePositionBinding);
			if (mesh?.HasNormals == true)
				AddRuntimeBuffer(PrecombinedBlendshapeNormalBufferName, blendshapeState.PrecombinedNormals, PrecombinedBlendshapeNormalBinding);
			if (mesh?.HasTangents == true)
				AddRuntimeBuffer(PrecombinedBlendshapeTangentBufferName, blendshapeState.PrecombinedTangents, PrecombinedBlendshapeTangentBinding);
		}
	}

	private void AddMeshDeformSourceBuffers()
	{
		XRMeshRenderer.MeshDeformResourceSnapshot meshDeformState =
			MeshRenderer.CaptureMeshDeformResources();
		if (meshDeformState.Positions is null || MeshRenderer.DeformMeshRenderer is null || MeshRenderer.MeshDeformInfluences is null)
			return;

		XRMeshRenderer deformerRenderer = MeshRenderer.DeformMeshRenderer;
		XRMeshRenderer.SkinnedOutputResourceSnapshot deformerOutputState =
			deformerRenderer.CaptureSkinnedOutputResources();
		if (deformerOutputState.Interleaved is not null)
		{
			Debug.VulkanWarningEvery(
				$"Vulkan.MeshDeform.InterleavedSourceAlias.{MeshRenderer.Name ?? "UnnamedRenderer"}",
				TimeSpan.FromSeconds(2),
				"[Vulkan] Mesh deform source aliases are skipped for renderer='{0}' because the deformer output is interleaved.",
				MeshRenderer.Name ?? "<unnamed renderer>");
			return;
		}

		AddRuntimeBuffer("DeformerPositionsBuffer", deformerOutputState.Positions, 0u, assignBindingOverride: false);

		uint nextBinding = 2u;
		if (meshDeformState.Normals is not null)
			AddRuntimeBuffer("DeformerNormalsBuffer", deformerOutputState.Normals, nextBinding++, assignBindingOverride: false);
		if (meshDeformState.Tangents is not null)
			AddRuntimeBuffer("DeformerTangentsBuffer", deformerOutputState.Tangents, nextBinding, assignBindingOverride: false);
	}

	private void AddRuntimeBuffer(string shaderName, XRDataBuffer? dataBuffer, uint binding, bool assignBindingOverride = true)
	{
		if (dataBuffer is null)
			return;

		if (assignBindingOverride)
			dataBuffer.BindingIndexOverride = binding;
		_bufferCache[shaderName] = ProgramCreationPort.GetOrCreateBuffer(dataBuffer, generateNow: false);
	}

	private void CaptureRuntimeDeformationBufferReferences()
	{
		XRMeshRenderer.SkinnedOutputResourceSnapshot outputState =
			MeshRenderer.CaptureSkinnedOutputResources();
		XRMeshRenderer.BlendshapeResourceSnapshot blendshapeState =
			MeshRenderer.CaptureBlendshapeResources();
		_cachedActiveSkinPaletteBuffer = MeshRenderer.ActiveSkinPaletteBuffer;
		_cachedActiveSkinPaletteIdentity = CaptureBufferStructuralIdentity(_cachedActiveSkinPaletteBuffer);
		_cachedHasValidPrecombinedBlendshapeDeltas =
			MeshRenderer.Mesh is { } mesh && blendshapeState.IsPrecombinedValidFor(mesh);
		_cachedSkinnedPositionsIdentity = CaptureBufferStructuralIdentity(outputState.Positions);
		_cachedSkinnedNormalsIdentity = CaptureBufferStructuralIdentity(outputState.Normals);
		_cachedSkinnedTangentsIdentity = CaptureBufferStructuralIdentity(outputState.Tangents);
		_cachedSkinnedInterleavedIdentity = CaptureBufferStructuralIdentity(outputState.Interleaved);
		_cachedPrecombinedBlendshapePositionsIdentity = CaptureBufferStructuralIdentity(blendshapeState.PrecombinedPositions);
		_cachedPrecombinedBlendshapeNormalsIdentity = CaptureBufferStructuralIdentity(blendshapeState.PrecombinedNormals);
		_cachedPrecombinedBlendshapeTangentsIdentity = CaptureBufferStructuralIdentity(blendshapeState.PrecombinedTangents);
	}

	private bool RuntimeDeformationBufferReferencesChanged()
	{
		XRMeshRenderer.SkinnedOutputResourceSnapshot outputState =
			MeshRenderer.CaptureSkinnedOutputResources();
		XRMeshRenderer.BlendshapeResourceSnapshot blendshapeState =
			MeshRenderer.CaptureBlendshapeResources();
		XRDataBuffer? activeSkinPalette = MeshRenderer.ActiveSkinPaletteBuffer;
		return !ReferenceEquals(_cachedActiveSkinPaletteBuffer, activeSkinPalette)
			|| _cachedActiveSkinPaletteIdentity != CaptureBufferStructuralIdentity(activeSkinPalette)
			|| _cachedSkinnedPositionsIdentity != CaptureBufferStructuralIdentity(outputState.Positions)
			|| _cachedSkinnedNormalsIdentity != CaptureBufferStructuralIdentity(outputState.Normals)
			|| _cachedSkinnedTangentsIdentity != CaptureBufferStructuralIdentity(outputState.Tangents)
			|| _cachedSkinnedInterleavedIdentity != CaptureBufferStructuralIdentity(outputState.Interleaved)
			|| _cachedPrecombinedBlendshapePositionsIdentity != CaptureBufferStructuralIdentity(blendshapeState.PrecombinedPositions)
			|| _cachedPrecombinedBlendshapeNormalsIdentity != CaptureBufferStructuralIdentity(blendshapeState.PrecombinedNormals)
			|| _cachedPrecombinedBlendshapeTangentsIdentity != CaptureBufferStructuralIdentity(blendshapeState.PrecombinedTangents)
			|| _cachedHasValidPrecombinedBlendshapeDeltas !=
				(MeshRenderer.Mesh is { } mesh && blendshapeState.IsPrecombinedValidFor(mesh));
	}

	private void EnsureRuntimeDeformationBuffersCurrent()
	{
		if (!RuntimeDeformationBufferReferencesChanged())
			return;

		lock (_bufferStateSync)
		{
			if (RuntimeDeformationBufferReferencesChanged())
				CollectBuffers();
		}
	}

	/// <summary>
	/// Lazily generates (uploads) all cached buffers and resolves index buffers
	/// for triangles, lines, and points. No-ops if buffers are already up-to-date.
	/// Index buffer construction is asynchronous â€” on first call the mesh kicks off a
	/// background Task.Run to build the buffer and returns null; the callback below
	/// flips <see cref="_buffersDirty"/> back to true so the next EnsureBuffers call
	/// picks up the now-cached buffer without stalling the render thread.
	/// </summary>
	private void EnsureBuffers(
		bool skipIndexBuffers = false,
		bool requireSynchronousIndexBuild = false)
	{
		lock (_bufferStateSync)
		{
			if (Interlocked.Exchange(ref _pendingAsyncIndexBufferReady, 0) != 0)
				ApplyIndexBufferReadyNoLock();

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
				CommandOperations.MarkCommandBuffersDirtyForLegacyMeshState();
			}

			bool allowSynchronousBufferUpload = BackendContext.Resources.AllowSynchronousResourceUploads;
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
				var tri = GetIndexBufferForBinding(EPrimitiveType.Triangles, out _triangleIndexSize, _triangleIndexBuffer, requireSynchronousIndexBuild);
				_triangleIndexBuffer = tri is not null
					? ProgramCreationPort.GetOrCreateBuffer(tri, generateNow: allowSynchronousBufferUpload)
					: null;
				_triangleIndexBuffer?.TryEnsureReadyForRendering(allowSynchronousBufferUpload);

				var line = GetIndexBufferForBinding(EPrimitiveType.Lines, out _lineIndexSize, _lineIndexBuffer, requireSynchronousIndexBuild);
				_lineIndexBuffer = line is not null
					? ProgramCreationPort.GetOrCreateBuffer(line, generateNow: allowSynchronousBufferUpload)
					: null;
				_lineIndexBuffer?.TryEnsureReadyForRendering(allowSynchronousBufferUpload);

				var point = GetIndexBufferForBinding(EPrimitiveType.Points, out _pointIndexSize, _pointIndexBuffer, requireSynchronousIndexBuild);
				_pointIndexBuffer = point is not null
					? ProgramCreationPort.GetOrCreateBuffer(point, generateNow: allowSynchronousBufferUpload)
					: null;
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
			PublishBufferReadinessSnapshot();
		}
	}

	private XRDataBuffer? GetIndexBufferForBinding(
		EPrimitiveType type,
		out IndexSize elementSize,
		VkDataBuffer? currentBinding,
		bool requireSynchronous)
	{
		if (Mesh is not { } mesh)
		{
			elementSize = IndexSize.TwoBytes;
			return null;
		}

		bool hasCachedIndices = mesh.HasCachedIndexBuffer(type);
		if (hasCachedIndices)
			Interlocked.And(ref _asyncIndexBufferSubscriptions, ~(1 << (int)type));
		if (mesh.TryGetIndexBufferBuildFailure(type, out Exception? error))
		{
			Interlocked.And(ref _asyncIndexBufferSubscriptions, ~(1 << (int)type));
			throw new InvalidOperationException(
				$"Index buffer build failed for mesh '{mesh.Name ?? "<unnamed>"}' primitive '{type}'.",
				error);
		}

		Action<XRDataBuffer, IndexSize>? onReady =
			currentBinding is null && !requireSynchronous &&
			!hasCachedIndices &&
			TryRegisterAsyncIndexBufferSubscription(type)
				? type switch
				{
					EPrimitiveType.Triangles => OnTriangleIndexBufferReady,
					EPrimitiveType.Lines => OnLineIndexBufferReady,
					EPrimitiveType.Points => OnPointIndexBufferReady,
					_ => null,
				}
				: null;

		return mesh.GetIndexBuffer(
			type,
			out elementSize,
			EBufferTarget.ElementArrayBuffer,
			onReady,
			requireSynchronous);
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

	/// <summary>
	/// Publishes immutable buffer membership after structural state changes.
	/// Buffer readiness itself remains live, so a buffer becoming pending is
	/// observed without rebuilding this snapshot.
	/// </summary>
	private void PublishBufferReadinessSnapshot()
	{
		int indexBufferCount = _indexBuffersSkippedForShaderGeneratedVertices
			? 0
			: (_triangleIndexBuffer is null ? 0 : 1)
			  + (_lineIndexBuffer is null ? 0 : 1)
			  + (_pointIndexBuffer is null ? 0 : 1);
		int requiredBufferCount = _bufferCache.Count + indexBufferCount;
		int shaderGeneratedBufferCount = indexBufferCount;
		foreach (VkDataBuffer buffer in _bufferCache.Values)
			if (buffer.Data.Target != EBufferTarget.ArrayBuffer)
				shaderGeneratedBufferCount++;

		KeyValuePair<string, VkDataBuffer>[] requiredBuffers =
			new KeyValuePair<string, VkDataBuffer>[requiredBufferCount];
		KeyValuePair<string, VkDataBuffer>[] shaderGeneratedRequiredBuffers =
			new KeyValuePair<string, VkDataBuffer>[shaderGeneratedBufferCount];
		int requiredIndex = 0;
		int shaderGeneratedIndex = 0;
		foreach (KeyValuePair<string, VkDataBuffer> pair in _bufferCache)
		{
			requiredBuffers[requiredIndex++] = pair;
			if (pair.Value.Data.Target != EBufferTarget.ArrayBuffer)
				shaderGeneratedRequiredBuffers[shaderGeneratedIndex++] = pair;
		}

		if (!_indexBuffersSkippedForShaderGeneratedVertices)
		{
			AppendIndexBuffer("Triangles", _triangleIndexBuffer);
			AppendIndexBuffer("Lines", _lineIndexBuffer);
			AppendIndexBuffer("Points", _pointIndexBuffer);
		}

		string missingExpectedIndexBufferDetail = ResolveMissingExpectedIndexBufferDetail();
		uint triangleIndexCount = _triangleIndexBuffer?.Data.ElementCount ?? 0u;
		uint lineIndexCount = _lineIndexBuffer?.Data.ElementCount ?? 0u;
		uint pointIndexCount = _pointIndexBuffer?.Data.ElementCount ?? 0u;
		XRMesh? mesh = Mesh;
		uint fallbackVertexCount = mesh is null
			? 0u
			: (uint)Math.Max(mesh.VertexCount, 0);
		bool fallbackIsTriangleClass = mesh is not null &&
			mesh.Type is not (EPrimitiveType.Points or EPrimitiveType.Lines or EPrimitiveType.LineStrip);
		Volatile.Write(
			ref _bufferReadinessSnapshot,
			new BufferReadinessSnapshot(
				requiredBuffers,
				shaderGeneratedRequiredBuffers,
				missingExpectedIndexBufferDetail,
				triangleIndexCount,
				lineIndexCount,
				pointIndexCount,
				fallbackVertexCount,
				fallbackIsTriangleClass));
		return;

		void AppendIndexBuffer(string name, VkDataBuffer? buffer)
		{
			if (buffer is null)
				return;

			KeyValuePair<string, VkDataBuffer> pair = new(name, buffer);
			requiredBuffers[requiredIndex++] = pair;
			shaderGeneratedRequiredBuffers[shaderGeneratedIndex++] = pair;
		}
	}

	private string ResolveMissingExpectedIndexBufferDetail()
	{
		if (_indexBuffersSkippedForShaderGeneratedVertices || Mesh is not { } mesh)
			return string.Empty;

		if (mesh.HasIndexData(EPrimitiveType.Triangles) && _triangleIndexBuffer is null)
			return "indexBuffer='Triangles' pending async build for indexed mesh";
		if (mesh.HasIndexData(EPrimitiveType.Lines) && _lineIndexBuffer is null)
			return "indexBuffer='Lines' pending async build for indexed mesh";
		if (mesh.HasIndexData(EPrimitiveType.Points) && _pointIndexBuffer is null)
			return "indexBuffer='Points' pending async build for indexed mesh";

		return string.Empty;
	}

	private bool TryRegisterAsyncIndexBufferSubscription(EPrimitiveType type)
	{
		int bit = 1 << (int)type;
		return (Interlocked.Or(ref _asyncIndexBufferSubscriptions, bit) & bit) == 0;
	}

	private void OnAsyncIndexBufferReady(
		EPrimitiveType type,
		XRDataBuffer buffer,
		IndexSize elementSize)
	{
		Interlocked.And(ref _asyncIndexBufferSubscriptions, ~(1 << (int)type));
		_ = buffer;
		_ = elementSize;
		// The worker publishes only an atomic readiness edge. EnsureBuffers consumes
		// it while already holding the render-thread-owned buffer-state lock, so a
		// PresentNow materialization loop never depends on pumping a generic callback.
		Interlocked.Exchange(ref _pendingAsyncIndexBufferReady, 1);
	}

	private void OnTriangleIndexBufferReady(XRDataBuffer buffer, IndexSize size)
		=> OnAsyncIndexBufferReady(EPrimitiveType.Triangles, buffer, size);
	private void OnLineIndexBufferReady(XRDataBuffer buffer, IndexSize size)
		=> OnAsyncIndexBufferReady(EPrimitiveType.Lines, buffer, size);
	private void OnPointIndexBufferReady(XRDataBuffer buffer, IndexSize size)
		=> OnAsyncIndexBufferReady(EPrimitiveType.Points, buffer, size);

	private void ApplyIndexBufferReadyNoLock()
	{
		BumpPreparationCompatibilityRevision();
		_buffersDirty = true;
		_pipelineDirty = true;
		_descriptorDirty = true;
		_vertexInputStateDirty = true;
		_geometryLayoutSignature = MeshGeometryLayoutSignature.Empty;
		CommandOperations.MarkCommandBuffersDirtyForLegacyMeshState();
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
			_triangleIndexBuffer?.TryEnsureReadyForRendering(BackendContext.Resources.AllowSynchronousResourceUploads);
			PublishBufferReadinessSnapshot();
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

		if (!buffer!.TryEnsureReadyForRendering(BackendContext.Resources.AllowSynchronousResourceUploads))
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
		=> ResolveMaterialSelection(localOverride, instances).Material;

	private ResolvedMeshRenderMaterial ResolveMaterialSelection(
		XRMaterial? localOverride,
		uint instances)
		=> MeshRenderMaterialResolver.Resolve(
			MeshRenderer,
			localOverride,
			instances,
			RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.InvalidMaterial);
}
