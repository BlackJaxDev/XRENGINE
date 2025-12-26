using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Compute;

namespace XREngine.Data.Trees
{
	public enum BvhBuildMode
	{
		MortonOnly = 0,
		MortonPlusSah = 1
	}

	/// <summary>
	/// GPU-managed octree that keeps a flat node buffer updated through compute shaders without a CPU mirror.
	/// </summary>
	public class OctreeGPU<T> : OctreeBase, I3DRenderTree<T> where T : class, IOctreeItem
	{
		private const uint MaxObjects = 1024;
		private const uint MaxQueueSize = 1024;
		private const uint NodeStrideScalars = 20;
		private const uint HeaderScalarCount = 2;

		private static readonly Vector4 ZeroVec4 = Vector4.Zero;
		private static readonly Matrix4x4 IdentityMatrix = Matrix4x4.Identity;

		private readonly object _syncRoot = new();
		private readonly List<T> _items = new();
		private readonly HashSet<T> _itemSet = new(ReferenceEqualityComparer<T>.Instance);
		private readonly Dictionary<T, uint> _generatedItemIds = new(ReferenceEqualityComparer<T>.Instance);
		private readonly List<Vector4> _aabbMins = new();
		private readonly List<Vector4> _aabbMaxs = new();
		private readonly List<Matrix4x4> _transformScratch = new();
		private readonly List<uint> _objectIdScratch = new();

		private XRDataBuffer? _aabbBuffer;
		private XRDataBuffer? _transformBuffer;
		private XRDataBuffer? _mortonBuffer;
		private XRDataBuffer? _nodeBuffer;
		private XRDataBuffer? _queueBuffer;
		private XRDataBuffer? _objectIdBuffer;
		private XRDataBuffer? _bvhNodeBuffer;
		private XRDataBuffer? _bvhRangeBuffer;
		private uint _lastBvhNodeCount;
		private uint _lastObjectCount;
		private bool _transformsDirty = true;

		private XRShader? _mortonShader;
		private XRShader? _smallSortShader;
		private XRShader? _padShader;
		private XRShader? _tileSortShader;
		private XRShader? _mergeShader;
		private XRShader? _buildShader;
		private XRShader? _propagateShader;
		private XRShader? _initQueueShader;
		private XRShader? _bvhBuildShader;
		private XRShader? _bvhRefineShader;
		private XRShader? _bvhRefitShader;

		private XRRenderProgram? _mortonProgram;
		private XRRenderProgram? _smallSortProgram;
		private XRRenderProgram? _padProgram;
		private XRRenderProgram? _tileSortProgram;
		private XRRenderProgram? _mergeProgram;
		private XRRenderProgram? _buildProgram;
		private XRRenderProgram? _propagateProgram;
		private XRRenderProgram? _initQueueProgram;
		private XRRenderProgram? _bvhBuildProgram;
		private XRRenderProgram? _bvhRefineProgram;
		private XRRenderProgram? _bvhRefitProgram;

		private bool _gpuDirty = true;
		private bool _hasUserBounds;
		private AABB _requestedBounds;
		private AABB _activeBounds;
		private Func<T, uint?>? _itemIdSelector;
		private uint _nextGeneratedId;
		private bool _useBvh = true;
		private BvhBuildMode _bvhMode = BvhBuildMode.MortonOnly;
		private uint _maxLeafPrimitives = 1u;

		public OctreeGPU()
		{
		}

		public OctreeGPU(AABB bounds)
		{
			_requestedBounds = bounds;
			_activeBounds = bounds;
			_hasUserBounds = true;
		}

		/// <summary>
		/// Optional callback that returns the GPU identifier recorded in the command buffers for a tree item.
		/// Returning null excludes the item from GPU traversal.
		/// </summary>
		public Func<T, uint?>? ItemIdSelector
		{
			get => _itemIdSelector;
			set
			{
				if (_itemIdSelector == value)
					return;
				_itemIdSelector = value;
				if (value is not null)
					ClearGeneratedIds();
				MarkDirty();
			}
		}

		/// <summary>
		/// When true (default), GPU buffers rebuild after every swap to guarantee item/node residency stays accurate.
		/// </summary>
		public bool AlwaysRebuildOnSwap { get; set; } = true;

		public AABB Bounds => _activeBounds;

		public XRDataBuffer? DrawCommandsBuffer => _nodeBuffer;

		public XRDataBuffer? NodesBuffer => _nodeBuffer;

		public XRDataBuffer? NodeItemIndexBuffer => _objectIdBuffer;

		public XRDataBuffer? BvhNodesBuffer => _bvhNodeBuffer;

		public XRDataBuffer? BvhRangeBuffer => _bvhRangeBuffer;

		public bool UseBvh
		{
			get => _useBvh;
			set
			{
				if (_useBvh == value)
					return;
				_useBvh = value;
				MarkDirty();
			}
		}

		public BvhBuildMode BvhMode
		{
			get => _bvhMode;
			set
			{
				if (_bvhMode == value)
					return;
				_bvhMode = value;
				ResetBvhPrograms();
				MarkDirty();
			}
		}

		public uint MaxLeafPrimitives
		{
			get => _maxLeafPrimitives;
			set
			{
				uint clamped = Math.Max(1u, value);
				if (_maxLeafPrimitives == clamped)
					return;
				_maxLeafPrimitives = clamped;
				ResetBvhPrograms();
				MarkDirty();
			}
		}

		private void ResetBvhPrograms()
		{
			_bvhBuildProgram = null;
			_bvhRefineProgram = null;
			_bvhBuildShader = null;
			_bvhRefineShader = null;
			_bvhRefitProgram = null;
			_bvhRefitShader = null;
		}

		public bool EnsureGpuBuffers(bool force = false)
		{
			if (!force && !_gpuDirty && !_transformsDirty)
				return false;

			if (Engine.IsRenderThread)
				return EnsureGpuBuffersOnRenderThread();

			using ManualResetEventSlim waitHandle = new(false);
			bool updated = false;
			Exception? captured = null;

			Engine.EnqueueMainThreadTask(() =>
			{
				try
				{
					updated = EnsureGpuBuffersOnRenderThread();
				}
				catch (Exception ex)
				{
					captured = ex;
				}
				finally
				{
					waitHandle.Set();
				}
			});

			waitHandle.Wait();

			if (captured is not null)
				throw captured;

			return updated;
		}

		public void Add(T value)
		{
			if (value is null)
				return;

			lock (_syncRoot)
			{
				if (_itemSet.Add(value))
				{
					_items.Add(value);
					MarkDirty();
				}
			}
		}

		public void Add(ITreeItem item)
		{
			if (item is T typed)
				Add(typed);
		}

		public void AddRange(IEnumerable<T> value)
		{
			if (value is null)
				return;

			foreach (T item in value)
				Add(item);
		}

		public void AddRange(IEnumerable<ITreeItem> renderedObjects)
		{
			if (renderedObjects is null)
				return;

			foreach (ITreeItem item in renderedObjects)
				if (item is T typed)
					Add(typed);
		}

		public void CollectAll(Action<IOctreeItem> action)
			=> throw new NotSupportedException("OctreeGPU is GPU-only and does not expose CPU enumeration.");

		public void CollectAll(Action<T> action)
			=> throw new NotSupportedException("OctreeGPU is GPU-only and does not expose CPU enumeration.");

		public void CollectVisible(IVolume? volume, bool onlyContainingItems, Action<T> action, OctreeNode<T>.DelIntersectionTest intersectionTest)
			=> throw new NotSupportedException("OctreeGPU does not support CPU culling queries.");

		public void CollectVisible(IVolume? volume, bool onlyContainingItems, Action<IOctreeItem> action, OctreeNode<IOctreeItem>.DelIntersectionTestGeneric intersectionTest)
			=> throw new NotSupportedException("OctreeGPU does not support CPU culling queries.");

		public void CollectVisibleNodes(IVolume? cullingVolume, bool containsOnly, Action<(OctreeNodeBase node, bool intersects)> action)
			=> throw new NotSupportedException("OctreeGPU does not expose CPU-side nodes.");

		public void DebugRender(IVolume? cullingVolume, DelRenderAABB render, bool onlyContainingItems = false)
			=> throw new NotSupportedException("OctreeGPU does not support CPU debug rendering.");

		public void Remake(AABB newBounds)
		{
			_requestedBounds = newBounds;
			_activeBounds = newBounds;
			_hasUserBounds = true;
			MarkDirty();
		}

		public void Remake()
		{
			_requestedBounds = default;
			_activeBounds = default;
			_hasUserBounds = false;
			MarkDirty();
		}

		public void Remove(T value)
		{
			if (value is null)
				return;

			lock (_syncRoot)
			{
				if (!_itemSet.Remove(value))
					return;

				_items.Remove(value);
				RemoveGeneratedId(value);
				MarkDirty();
			}
		}

		public void Remove(ITreeItem item)
		{
			if (item is T typed)
				Remove(typed);
		}

		public void RemoveRange(IEnumerable<T> value)
		{
			if (value is null)
				return;

			foreach (T item in value)
				Remove(item);
		}

		public void RemoveRange(IEnumerable<ITreeItem> renderedObjects)
		{
			if (renderedObjects is null)
				return;

			foreach (ITreeItem item in renderedObjects)
				if (item is T typed)
					Remove(typed);
		}

		public void Swap()
		{
			_transformsDirty = true;
			if (AlwaysRebuildOnSwap)
				MarkDirty();
			EnsureGpuBuffers();
		}

		private bool EnsureGpuBuffersOnRenderThread()
		{
			if (AbstractRenderer.Current is null)
				return false;

			lock (_syncRoot)
			{
				if (!_gpuDirty && !_transformsDirty)
					return false;

				EnsurePrograms();
				BuildAndUploadItemData(out int objectCount, out AABB boundsUsed);
				_activeBounds = boundsUsed;

				bool countChanged = _lastObjectCount != (uint)objectCount;
				if (countChanged)
					_gpuDirty = true;

				if (objectCount > 0)
				{
					if (_gpuDirty)
						RunComputePipeline((uint)objectCount, boundsUsed);
					else
						RunRefitOnly((uint)objectCount);
				}
				else
					UploadEmptyBuffers();

				_lastObjectCount = (uint)objectCount;
				_gpuDirty = false;
				_transformsDirty = false;
				return true;
			}
		}

		private void BuildAndUploadItemData(out int objectCount, out AABB boundsUsed)
		{
			_aabbMins.Clear();
			_aabbMaxs.Clear();
			_transformScratch.Clear();
			_objectIdScratch.Clear();

			bool truncated = false;
			bool hasBounds = _hasUserBounds;
			AABB dynamicBounds = hasBounds ? _requestedBounds : default;

			foreach (var item in _items)
			{
				if (!item.ShouldRender)
					continue;
				if (_aabbMins.Count >= MaxObjects)
				{
					truncated = true;
					break;
				}

				Box? worldVolume = item.WorldCullingVolume;
				if (worldVolume is null)
					continue;

				AABB bounds = worldVolume.Value.GetAABB(true);
				uint? id = ResolveItemId(item);
				if (id is null)
					continue;

				_aabbMins.Add(new Vector4(bounds.Min, 1f));
				_aabbMaxs.Add(new Vector4(bounds.Max, 1f));
				_transformScratch.Add(item.CullingOffsetMatrix);
				_objectIdScratch.Add(id.Value);

				if (!hasBounds)
				{
					dynamicBounds = _aabbMins.Count == 1
						? bounds
						: dynamicBounds.ExpandedToInclude(bounds);
				}
			}

			if (truncated)
				Debug.LogWarning($"OctreeGPU clamped item count to {MaxObjects}.");

			boundsUsed = _hasUserBounds ? _requestedBounds : dynamicBounds;
			if (_aabbMins.Count == 0 && !_hasUserBounds)
				boundsUsed = default;

			UploadAabbData();
			UploadTransformData();
			UploadObjectIdData();

			objectCount = _aabbMins.Count;
		}

		private void UploadAabbData()
		{
			int count = _aabbMins.Count;
			int total = count * 2;
			Vector4[] combined = new Vector4[total];
			for (int i = 0; i < count; ++i)
			{
				combined[i] = _aabbMins[i];
				combined[count + i] = _aabbMaxs[i];
			}

			_aabbBuffer ??= CreateBuffer("GPUOctree.AABBs", false);
			_aabbBuffer.SetDataRaw(combined, total);
			_aabbBuffer.SetBlockIndex(0);
			_aabbBuffer.PushSubData();
		}

		private void UploadTransformData()
		{
			_transformBuffer ??= CreateBuffer("GPUOctree.Transforms", false);
			if (_transformScratch.Count == 0)
			{
				_transformBuffer.SetDataRaw(new[] { IdentityMatrix }, 1);
			}
			else
			{
				_transformBuffer.SetDataRaw(_transformScratch, _transformScratch.Count);
			}
			_transformBuffer.SetBlockIndex(1);
			_transformBuffer.PushSubData();
		}

		private void UploadObjectIdData()
		{
			_objectIdBuffer ??= CreateBuffer("GPUOctree.ItemIds", true);
			if (_objectIdScratch.Count == 0)
			{
				_objectIdBuffer.SetDataRaw(new uint[] { 0u }, 1);
			}
			else
			{
				_objectIdBuffer.SetDataRaw(_objectIdScratch, _objectIdScratch.Count);
			}
			_objectIdBuffer.SetBlockIndex(5);
			_objectIdBuffer.PushSubData();
		}

		private void UploadEmptyBuffers()
		{
			_aabbBuffer ??= CreateBuffer("GPUOctree.AABBs", false);
			_aabbBuffer.SetDataRaw(new[] { ZeroVec4, ZeroVec4 }, 2);
			_aabbBuffer.SetBlockIndex(0);
			_aabbBuffer.PushSubData();

			_transformBuffer ??= CreateBuffer("GPUOctree.Transforms", false);
			_transformBuffer.SetDataRaw(new[] { IdentityMatrix }, 1);
			_transformBuffer.SetBlockIndex(1);
			_transformBuffer.PushSubData();

			_objectIdBuffer ??= CreateBuffer("GPUOctree.ItemIds", true);
			_objectIdBuffer.SetDataRaw(new uint[] { 0u }, 1);
			_objectIdBuffer.SetBlockIndex(5);
			_objectIdBuffer.PushSubData();

			EnsureNodeBufferCapacity(1);
			if (_nodeBuffer is not null)
			{
				ClearClientBuffer(_nodeBuffer);
				_nodeBuffer.PushSubData();
			}

			EnsureQueueBuffer();
			if (_queueBuffer is not null)
			{
				ClearClientBuffer(_queueBuffer);
				_queueBuffer.PushSubData();
			}

			if (_useBvh)
			{
				EnsureBvhBuffers(0);
				if (_bvhNodeBuffer is not null)
				{
					ClearClientBuffer(_bvhNodeBuffer);
					_bvhNodeBuffer.PushSubData();
				}

				if (_bvhRangeBuffer is not null)
				{
					ClearClientBuffer(_bvhRangeBuffer);
					_bvhRangeBuffer.PushSubData();
				}
			}
		}

		private void RunComputePipeline(uint objectCount, AABB bounds)
		{
			EnsureNodeBufferCapacity(objectCount);
			EnsureQueueBuffer();
			EnsureMortonBufferCapacity(objectCount);
			EnsureBvhBuffers(objectCount);

			Vector3 sceneMin = bounds.Min;
			Vector3 sceneMax = bounds.Max;
			if (sceneMin == sceneMax)
			{
				sceneMin -= new Vector3(0.5f);
				sceneMax += new Vector3(0.5f);
			}

			DispatchMorton(objectCount, sceneMin, sceneMax);
			SortMortonCodes(objectCount);
			bool builtBvh = DispatchBuildBvh(objectCount);
			if (builtBvh)
				DispatchRefitBvh();
			bool refinedBvh = DispatchRefineBvh();
			if (refinedBvh)
				DispatchRefitBvh();
			DispatchBuildOctree(objectCount, sceneMin, sceneMax);
			DispatchPropagate();
			DispatchInitQueue();

			_transformBuffer?.SetBlockIndex(1);
		}

		private void RunRefitOnly(uint objectCount)
		{
			if (_useBvh)
			{
				EnsureBvhBuffers(objectCount);
				DispatchRefitBvh();
			}

			_transformBuffer?.SetBlockIndex(1);
		}

		private void DispatchMorton(uint objectCount, Vector3 sceneMin, Vector3 sceneMax)
		{
			var program = _mortonProgram!;
			program.BindBuffer(_aabbBuffer!, 0);
			program.BindBuffer(_mortonBuffer!, 1);
			program.Uniform("sceneMin", sceneMin);
			program.Uniform("sceneMax", sceneMax);
			program.Uniform("numObjects", objectCount);
			uint groups = ComputeGroups(objectCount, 256u);
			program.DispatchCompute(groups, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
		}

		private bool DispatchBuildBvh(uint objectCount)
		{
			if (!_useBvh || objectCount == 0)
				return false;

			if (_bvhBuildProgram is null || _bvhNodeBuffer is null || _bvhRangeBuffer is null)
				return false;

			uint leafCount = (objectCount + _maxLeafPrimitives - 1u) / _maxLeafPrimitives;
			uint internalCount = leafCount > 0 ? leafCount - 1u : 0u;
			if (leafCount == 0)
				return false;

			var program = _bvhBuildProgram;
			program.BindBuffer(_aabbBuffer!, 0);
			program.BindBuffer(_mortonBuffer!, 1);
			program.BindBuffer(_bvhNodeBuffer, 2);
			program.BindBuffer(_bvhRangeBuffer, 3);
			program.Uniform("numPrimitives", objectCount);
			program.Uniform("buildStage", 0u);
			program.DispatchCompute(ComputeGroups(Math.Max(leafCount, 1u), 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
			if (internalCount > 0)
			{
				program.Uniform("buildStage", 1u);
				program.DispatchCompute(ComputeGroups(internalCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
			}

			return true;
		}

		private bool DispatchRefineBvh()
		{
			if (!_useBvh || _bvhRefineProgram is null || _bvhNodeBuffer is null || _bvhRangeBuffer is null)
				return false;

			if (_bvhMode != BvhBuildMode.MortonPlusSah || _lastBvhNodeCount == 0)
				return false;

			var program = _bvhRefineProgram;
			program.BindBuffer(_aabbBuffer!, 0);
			program.BindBuffer(_mortonBuffer!, 1);
			program.BindBuffer(_bvhNodeBuffer, 2);
			program.BindBuffer(_bvhRangeBuffer, 3);
			program.DispatchCompute(ComputeGroups(_lastBvhNodeCount, 128u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
			return true;
		}

		private bool DispatchRefitBvh()
		{
			if (!_useBvh || _bvhRefitProgram is null || _bvhNodeBuffer is null || _bvhRangeBuffer is null || _transformBuffer is null)
				return false;

			if (_lastBvhNodeCount == 0)
				return false;

			var program = _bvhRefitProgram;
			program.BindBuffer(_aabbBuffer!, 0);
			program.BindBuffer(_mortonBuffer!, 1);
			program.BindBuffer(_bvhNodeBuffer, 2);
			program.BindBuffer(_bvhRangeBuffer, 3);
			program.BindBuffer(_transformBuffer, 4);
			program.Uniform("refitStage", 0u);
			program.DispatchCompute(ComputeGroups(_lastBvhNodeCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
			program.Uniform("refitStage", 1u);
			program.DispatchCompute(1u, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
			return true;
		}

		private void SortMortonCodes(uint objectCount)
		{
			if (objectCount <= 1)
				return;

			if (objectCount <= 1024)
			{
				var program = _smallSortProgram!;
				program.BindBuffer(_mortonBuffer!, 1);
				program.Uniform("numObjects", objectCount);
				program.DispatchCompute(1u, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
				return;
			}

			uint paddedCount = Math.Max(1024u, XRMath.NextPowerOfTwo(objectCount));
			var padProgram = _padProgram!;
			padProgram.BindBuffer(_mortonBuffer!, 1);
			padProgram.Uniform("numObjects", objectCount);
			padProgram.Uniform("paddedCount", paddedCount);
			padProgram.DispatchCompute(ComputeGroups(paddedCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

			var tileProgram = _tileSortProgram!;
			tileProgram.BindBuffer(_mortonBuffer!, 1);
			tileProgram.Uniform("paddedCount", paddedCount);
			tileProgram.DispatchCompute(Math.Max(1u, paddedCount / 1024u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

			var mergeProgram = _mergeProgram!;
			mergeProgram.BindBuffer(_mortonBuffer!, 1);
			mergeProgram.Uniform("paddedCount", paddedCount);
			for (uint k = 2048u; k <= paddedCount; k <<= 1)
			{
				mergeProgram.Uniform("K", k);
				for (uint j = k >> 1; j > 0; j >>= 1)
				{
					mergeProgram.Uniform("J", j);
					mergeProgram.DispatchCompute(ComputeGroups(paddedCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
				}
			}
		}

		private void DispatchBuildOctree(uint objectCount, Vector3 sceneMin, Vector3 sceneMax)
		{
			var program = _buildProgram!;
			program.BindBuffer(_aabbBuffer!, 0);
			program.BindBuffer(_mortonBuffer!, 1);
			program.BindBuffer(_nodeBuffer!, 2);
			program.Uniform("numObjects", objectCount);
			program.Uniform("sceneMin", sceneMin);
			program.Uniform("sceneMax", sceneMax);
			uint groups = ComputeGroups(Math.Max(objectCount, 1u), 256u);
			program.DispatchCompute(groups, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
		}

		private void DispatchPropagate()
		{
			var program = _propagateProgram!;
			program.BindBuffer(_nodeBuffer!, 2);
			uint nodeCount = ReadNodeCount();
			if (nodeCount == 0)
				return;
			program.DispatchCompute(ComputeGroups(nodeCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
		}

		private void DispatchInitQueue()
		{
			var program = _initQueueProgram!;
			program.BindBuffer(_nodeBuffer!, 2);
			program.BindBuffer(_queueBuffer!, 4);
			program.DispatchCompute(1u, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
		}

		private void EnsurePrograms()
		{
			_mortonProgram ??= CreateProgram(ref _mortonShader, "Scene3D/RenderPipeline/OctreeGeneration/morton_codes.comp");
			_smallSortProgram ??= CreateProgram(ref _smallSortShader, "Scene3D/RenderPipeline/OctreeGeneration/sort_morton.comp");
			_padProgram ??= CreateProgram(ref _padShader, "Scene3D/RenderPipeline/OctreeGeneration/pad_morton.comp");
			_tileSortProgram ??= CreateProgram(ref _tileSortShader, "Scene3D/RenderPipeline/OctreeGeneration/sort_morton_tiles.comp");
			_mergeProgram ??= CreateProgram(ref _mergeShader, "Scene3D/RenderPipeline/OctreeGeneration/merge_morton.comp");
			_buildProgram ??= CreateProgram(ref _buildShader, "Scene3D/RenderPipeline/OctreeGeneration/build_octree.comp");
			_propagateProgram ??= CreateProgram(ref _propagateShader, "Scene3D/RenderPipeline/OctreeGeneration/propagate_aabbs.comp");
			_initQueueProgram ??= CreateProgram(ref _initQueueShader, "Scene3D/RenderPipeline/OctreeGeneration/init_queue.comp");
			if (_useBvh)
				EnsureBvhPrograms();
		}

		private void EnsureBvhPrograms()
		{
			_bvhBuildProgram ??= CreateProgram(ref _bvhBuildShader, "Scene3D/RenderPipeline/bvh_build.comp", true);
			_bvhRefineProgram ??= CreateProgram(ref _bvhRefineShader, "Scene3D/RenderPipeline/bvh_sah_refine.comp", true);
			_bvhRefitProgram ??= CreateProgram(ref _bvhRefitShader, "Scene3D/RenderPipeline/bvh_refit.comp", true);
		}

		private void EnsureMortonBufferCapacity(uint objectCount)
		{
			uint padded = Math.Max(1u, XRMath.NextPowerOfTwo(Math.Max(1u, objectCount)));
			uint elements = Math.Max(padded, 1u);
			if (_mortonBuffer is null)
			{
				_mortonBuffer = CreateBuffer("GPUOctree.Morton", true);
				_mortonBuffer.SetDataRaw(new uint[elements * 2], (int)(elements * 2));
			}
			else if (_mortonBuffer.ElementCount < elements * 2u)
			{
				_mortonBuffer.Resize(elements * 2u, false, true);
			}
		}

		private void EnsureBvhBuffers(uint objectCount)
		{
			if (!_useBvh)
				return;

			uint leafCount = objectCount == 0 ? 0u : ((objectCount + _maxLeafPrimitives - 1u) / _maxLeafPrimitives);
			uint nodeCount = leafCount == 0 ? 0u : (leafCount * 2u) - 1u;
			_lastBvhNodeCount = nodeCount;

			uint nodeScalars = GpuBvhLayout.NodeScalarCapacity(Math.Max(nodeCount, 1u));
			uint rangeScalars = GpuBvhLayout.RangeScalarCapacity(Math.Max(nodeCount, 1u));

			if (_bvhNodeBuffer is null)
			{
				_bvhNodeBuffer = CreateBvhBuffer("GPUOctree.BvhNodes", nodeScalars);
				_bvhNodeBuffer.SetBlockIndex(6);
			}
			else if (_bvhNodeBuffer.ElementCount < nodeScalars)
			{
				_bvhNodeBuffer.Resize(nodeScalars, false, true);
			}

			if (_bvhRangeBuffer is null)
			{
				_bvhRangeBuffer = CreateBvhBuffer("GPUOctree.BvhRanges", rangeScalars);
				_bvhRangeBuffer.SetBlockIndex(7);
			}
			else if (_bvhRangeBuffer.ElementCount < rangeScalars)
			{
				_bvhRangeBuffer.Resize(rangeScalars, false, true);
			}
		}

		private void EnsureNodeBufferCapacity(uint objectCount)
		{
			uint nodeCount = objectCount == 0 ? 0 : (objectCount * 2u) - 1u;
			uint required = HeaderScalarCount + nodeCount * NodeStrideScalars;
			if (_nodeBuffer is null)
			{
				_nodeBuffer = CreateBuffer("GPUOctree.Nodes", true);
				_nodeBuffer.SetBlockIndex(2);
				_nodeBuffer.SetDataRaw(new uint[required == 0 ? HeaderScalarCount : required], (int)(required == 0 ? HeaderScalarCount : required));
			}
			else if (_nodeBuffer.ElementCount < required)
			{
				_nodeBuffer.Resize(required, false, true);
			}
		}

		private void EnsureQueueBuffer()
		{
			uint length = MaxQueueSize + 3u;
			if (_queueBuffer is null)
			{
				_queueBuffer = CreateBuffer("GPUOctree.Queue", true);
				_queueBuffer.SetBlockIndex(4);
				_queueBuffer.SetDataRaw(new uint[length], (int)length);
			}
			else if (_queueBuffer.ElementCount < length)
			{
				_queueBuffer.Resize(length, false, true);
			}
		}

		private uint ReadNodeCount()
		{
			if (_nodeBuffer?.ClientSideSource is null)
				return 0u;
			unsafe
			{
				return ((uint*)_nodeBuffer.Address.Pointer)[0];
			}
		}

		private XRRenderProgram CreateProgram(ref XRShader? shader, string path, bool applyBvhSpecialization = false)
		{
			shader ??= applyBvhSpecialization
				? LoadBvhShaderWithConstants(path)
				: ShaderHelper.LoadEngineShader(path, EShaderType.Compute);
			return new XRRenderProgram(true, false, shader);
		}

		private XRShader LoadBvhShaderWithConstants(string path)
		{
			XRShader baseShader = ShaderHelper.LoadEngineShader(path, EShaderType.Compute);
			string? source = baseShader.Source?.Text;
			if (source is null)
				return baseShader;

			string patched = PatchBvhSpecializationConstants(source, _maxLeafPrimitives, _bvhMode);
			if (string.Equals(patched, source, StringComparison.Ordinal))
				return baseShader;

			TextFile cloneText = new(baseShader.Source?.FilePath ?? string.Empty)
			{
				Text = patched
			};

			return new XRShader(EShaderType.Compute, cloneText);
		}

		private static string PatchBvhSpecializationConstants(string source, uint maxLeafPrimitives, BvhBuildMode mode)
		{
			string patched = Regex.Replace(
				source,
				@"const\s+uint\s+MAX_LEAF_PRIMITIVES\s*=\s*[0-9]+u?\s*;",
				$"const uint MAX_LEAF_PRIMITIVES = {maxLeafPrimitives}u;",
				RegexOptions.CultureInvariant);

			patched = Regex.Replace(
				patched,
				@"const\s+uint\s+BVH_MODE\s*=\s*[0-9]+u?\s*;",
				$"const uint BVH_MODE = {(mode == BvhBuildMode.MortonPlusSah ? 1u : 0u)}u;",
				RegexOptions.CultureInvariant);

			return patched;
		}

		private static XRDataBuffer CreateBuffer(string name, bool integral)
			=> new(name, EBufferTarget.ShaderStorageBuffer, integral)
			{
				Usage = EBufferUsage.DynamicDraw,
				Resizable = true,
				DisposeOnPush = false,
				PadEndingToVec4 = true
			};

		private static XRDataBuffer CreateBvhBuffer(string name, uint scalarCount)
			=> new(name, EBufferTarget.ShaderStorageBuffer, scalarCount, EComponentType.UInt, 1, false, true)
			{
				Usage = EBufferUsage.DynamicDraw,
				Resizable = true,
				DisposeOnPush = false,
				PadEndingToVec4 = true,
				ShouldMap = true
			};

		private static unsafe void ClearClientBuffer(XRDataBuffer buffer)
		{
			if (buffer.ClientSideSource is null)
				return;
			Span<byte> span = new(buffer.Address.Pointer, (int)buffer.Length);
			span.Clear();
		}

		private static uint ComputeGroups(uint workItems, uint localSize)
			=> Math.Max(1u, (workItems + localSize - 1u) / localSize);

		private void MarkDirty()
		{
			_gpuDirty = true;
			_transformsDirty = true;
		}

		private uint? ResolveItemId(T item)
		{
			if (_itemIdSelector is not null)
				return _itemIdSelector(item);

			if (_generatedItemIds.TryGetValue(item, out uint id))
				return id;

			id = _nextGeneratedId++;
			_generatedItemIds[item] = id;
			return id;
		}

		private void RemoveGeneratedId(T item)
		{
			if (_itemIdSelector is not null)
				return;
			_generatedItemIds.Remove(item);
		}

		private void ClearGeneratedIds()
		{
			_generatedItemIds.Clear();
			_nextGeneratedId = 0;
		}

		private sealed class ReferenceEqualityComparer<TRef> : IEqualityComparer<TRef> where TRef : class
		{
			public static ReferenceEqualityComparer<TRef> Instance { get; } = new();

			public bool Equals(TRef? x, TRef? y)
				=> ReferenceEquals(x, y);

			public int GetHashCode(TRef obj)
				=> RuntimeHelpers.GetHashCode(obj);
		}

		private sealed class DebugOctreeNode(AABB bounds) : OctreeNodeBase(bounds, 0, 0)
		{
            public override OctreeNodeBase? GenericParent => null;

			protected override OctreeNodeBase? GetNodeInternal(int index) => null;

			public override void HandleMovedItem(IOctreeItem item)
			{
			}

			public override bool Remove(IOctreeItem item, out bool destroyNode)
			{
				destroyNode = false;
				return false;
			}

			protected override void RemoveNodeAt(int subDivIndex)
			{
			}
		}
	}
}
