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
using XREngine.Scene;

namespace XREngine.Data.Trees
{
	public enum BvhBuildMode
	{
		MortonOnly = 0,
		MortonPlusSah = 1
	}

	public enum SwapUpdateMode
	{
		Manual = 0,
		RefitOnly = 1,
		AlwaysRebuild = 2
	}

	/// <summary>
	/// GPU-managed octree that keeps a flat node buffer updated through compute shaders without a CPU mirror.
	/// </summary>
	public class OctreeGPU<T> : OctreeBase, I3DRenderTree<T>, IDisposable where T : class, IOctreeItem
	{
		private bool _disposed;

		/// <summary>
		/// Maximum number of objects supported by the GPU octree.
		/// IMPORTANT: This value must match MAX_OBJECTS in the compute shaders:
		/// - morton_codes.comp
		/// - build_octree.comp
		/// - refit_octree.comp
		/// - propagate_aabbs.comp
		/// </summary>
		public const uint MaxObjects = 1024;

		private const uint MaxQueueSize = 1024;
		private const uint NodeStrideScalars = 20;
		private const uint HeaderScalarCount = 2;
		internal static uint QueueCapacity => MaxQueueSize;
		private const uint OverflowMortonBit = 1u;
		private const uint OverflowNodeBit = 1u << 1;
		private const uint OverflowQueueBit = 1u << 2;
		private const uint OverflowBvhBit = 1u << 3;
		private const uint OverflowFlagBinding = 8u;

		private static readonly Vector4 ZeroVec4 = Vector4.Zero;
		private static readonly Matrix4x4 IdentityMatrix = Matrix4x4.Identity;

		private enum TreeDirtyState
		{
			None = 0,
			Refit = 1,
			Rebuild = 2
		}

		private readonly struct ItemState
		{
			public ItemState(int objectIndex, AABB? bounds, Matrix4x4 offset, bool shouldRender)
			{
				ObjectIndex = objectIndex;
				Bounds = bounds;
				Offset = offset;
				ShouldRender = shouldRender;
			}

			public int ObjectIndex { get; }
			public AABB? Bounds { get; }
			public Matrix4x4 Offset { get; }
			public bool ShouldRender { get; }
		}

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
		private XRDataBuffer? _overflowFlagBuffer;
		private bool _overflowed;
		private bool _overflowWarningIssued;
		private string? _overflowReason;

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
		private XRShader? _refitOctreeShader;

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
		private XRRenderProgram? _refitOctreeProgram;

		private bool _gpuDirty = true;
		private TreeDirtyState _dirtyState = TreeDirtyState.Rebuild;
		private bool _hasUserBounds;
		private AABB _requestedBounds;
		private AABB _activeBounds;
		private Func<T, uint?>? _itemIdSelector;
		private uint _nextGeneratedId;
		private bool _useBvh = true;
		private BvhBuildMode _bvhMode = BvhBuildMode.MortonOnly;
		private uint _maxLeafPrimitives = 1u;
		private SwapUpdateMode _swapMode = SwapUpdateMode.AlwaysRebuild;

		private readonly HashSet<T> _dirtyItems = new(ReferenceEqualityComparer<T>.Instance);
		private readonly Dictionary<T, ItemState> _lastItemStates = new(ReferenceEqualityComparer<T>.Instance);

#if DEBUG
		public bool EnableValidation { get; set; } = true;
#else
		public bool EnableValidation { get; set; } = false;
#endif

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
				MarkDirty(TreeDirtyState.Rebuild);
			}
		}

		/// <summary>
		/// When true (default), GPU buffers rebuild after every swap to guarantee item/node residency stays accurate.
		/// </summary>
		public bool AlwaysRebuildOnSwap
		{
			get => _swapMode == SwapUpdateMode.AlwaysRebuild;
			set => _swapMode = value ? SwapUpdateMode.AlwaysRebuild : SwapUpdateMode.Manual;
		}

		public SwapUpdateMode SwapMode
		{
			get => _swapMode;
			set
			{
				if (_swapMode == value)
					return;
				_swapMode = value;
			}
		}

		public AABB Bounds => _activeBounds;

		public XRDataBuffer? DrawCommandsBuffer => _nodeBuffer;

		public XRDataBuffer? NodesBuffer => _nodeBuffer;

		public XRDataBuffer? NodeItemIndexBuffer => _objectIdBuffer;

		public XRDataBuffer? BvhNodesBuffer => _bvhNodeBuffer;

		public XRDataBuffer? BvhRangeBuffer => _bvhRangeBuffer;

		public IReadOnlyCollection<T> DirtyItems => _dirtyItems;

		public bool UseBvh
		{
			get => _useBvh;
			set
			{
				if (_useBvh == value)
					return;
				_useBvh = value;
				if (!_useBvh)
					ReleaseBvhResources();
				MarkDirty(TreeDirtyState.Rebuild);
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
				MarkDirty(TreeDirtyState.Rebuild);
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
				MarkDirty(TreeDirtyState.Rebuild);
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
					MarkDirty(TreeDirtyState.Rebuild);
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
			MarkDirty(TreeDirtyState.Rebuild);
			EnsureGpuBuffers();
		}

		public void Remake()
		{
			_requestedBounds = default;
			_activeBounds = default;
			_hasUserBounds = false;
			MarkDirty(TreeDirtyState.Rebuild);
			EnsureGpuBuffers();
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
				MarkDirty(TreeDirtyState.Rebuild);
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
			if (_swapMode == SwapUpdateMode.AlwaysRebuild || AlwaysRebuildOnSwap)
			{
				MarkDirty(TreeDirtyState.Rebuild);
			}
			else if (_swapMode == SwapUpdateMode.RefitOnly && _dirtyState == TreeDirtyState.None)
			{
				if (_items.Count == _lastObjectCount)
					MarkDirty(TreeDirtyState.Refit);
				else
					MarkDirty(TreeDirtyState.Rebuild);
			}
			_transformsDirty = true;
			EnsureGpuBuffers();
		}

		private bool EnsureGpuBuffersOnRenderThread()
		{
			if (AbstractRenderer.Current is null)
				return false;

			lock (_syncRoot)
			{
				if (!_gpuDirty && !_transformsDirty && _dirtyState == TreeDirtyState.None)
					return false;

				EnsurePrograms();
				BuildAndUploadItemData(out uint objectCount, out AABB boundsUsed, out Dictionary<T, ItemState> newStates);
				uint nodeCount = objectCount == 0 ? 0 : (objectCount * 2u) - 1u;
				TryRecoverFromOverflow(objectCount, nodeCount);
				if (!EnsureCapacityForCounts(objectCount, nodeCount, out string? overflowReason))
				{
					HandleOverflow(overflowReason ?? "GPU octree overflow detected before dispatch.");
					return true;
				}
				if (_overflowed)
				{
					UploadEmptyBuffers();
					return true;
				}
				_activeBounds = boundsUsed;
				// On first run, _nodeBuffer is null and _lastObjectCount is 0, so always rebuild
				bool isFirstRun = _nodeBuffer is null;
				bool countsStable = !isFirstRun && objectCount == _lastObjectCount;
				bool ranRefit = false;
        
        		bool countChanged = _lastObjectCount != (uint)objectCount;
				if (countChanged || isFirstRun)
					_gpuDirty = true;
        
				if (_dirtyState != TreeDirtyState.Rebuild && _swapMode == SwapUpdateMode.RefitOnly && countsStable && _nodeBuffer is not null)
					ranRefit = RunRefitPipeline((uint)objectCount);

				if (!ranRefit)
				{
					if (objectCount > 0)
					{
						if (_gpuDirty)
							RunComputePipeline((uint)objectCount, boundsUsed);
						else
							RunRefitOnly((uint)objectCount);
					}
					else
						UploadEmptyBuffers();
				}

				ConsumeOverflowFlag((uint)objectCount, nodeCount);

				_lastObjectCount = objectCount;
				_gpuDirty = false;
				_dirtyState = TreeDirtyState.None;
				_dirtyItems.Clear();
				_lastItemStates.Clear();
				foreach (var kvp in newStates)
					_lastItemStates[kvp.Key] = kvp.Value;
				_transformsDirty = false;
				return true;
			}
		}

		private void BuildAndUploadItemData(out uint objectCount, out AABB boundsUsed, out Dictionary<T, ItemState> newStates)
		{
			_aabbMins.Clear();
			_aabbMaxs.Clear();
			_transformScratch.Clear();
			_objectIdScratch.Clear();
			_dirtyItems.Clear();
			newStates = new Dictionary<T, ItemState>(ReferenceEqualityComparer<T>.Instance);
			HashSet<T> seenItems = new(ReferenceEqualityComparer<T>.Instance);

			bool truncated = false;
			bool hasBounds = _hasUserBounds;
			AABB dynamicBounds = hasBounds ? _requestedBounds : default;

			foreach (var item in _items)
			{
				bool shouldRender = item.ShouldRender;
				if (!shouldRender)
				{
					if (_lastItemStates.ContainsKey(item))
						MarkDirty(TreeDirtyState.Rebuild);
					continue;
				}
				if (_aabbMins.Count >= MaxObjects)
				{
					truncated = true;
					break;
				}

				Box? worldVolume = item.WorldCullingVolume;
				if (worldVolume is null)
				{
					if (_lastItemStates.ContainsKey(item))
						MarkDirty(TreeDirtyState.Rebuild);
					continue;
				}

				AABB bounds = worldVolume.Value.GetAABB(true);
				if (EnableValidation && !bounds.IsValid)
				{
					Debug.LogWarning($"OctreeGPU encountered invalid bounds for item {item} and skipped it.");
					continue;
				}
				uint? id = ResolveItemId(item);
				if (id is null)
					continue;

				_aabbMins.Add(new Vector4(bounds.Min, 1f));
				_aabbMaxs.Add(new Vector4(bounds.Max, 1f));
				_transformScratch.Add(item.CullingOffsetMatrix);
				_objectIdScratch.Add(id.Value);
				int objectIndex = _aabbMins.Count - 1;

				bool requiresRebuild = false;
				if (_lastItemStates.TryGetValue(item, out ItemState previous))
				{
					if (previous.ObjectIndex != objectIndex || previous.ShouldRender != shouldRender)
					{
						requiresRebuild = true;
					}
					else if (!previous.Bounds.HasValue || !AreBoundsEqual(previous.Bounds.Value, bounds) || previous.Offset != item.CullingOffsetMatrix)
					{
						MarkDirty(TreeDirtyState.Refit);
						_dirtyItems.Add(item);
					}
				}
				else
				{
					requiresRebuild = true;
				}

				if (requiresRebuild)
					MarkDirty(TreeDirtyState.Rebuild);

				newStates[item] = new ItemState(objectIndex, bounds, item.CullingOffsetMatrix, shouldRender);
				seenItems.Add(item);

				if (!hasBounds)
				{
					dynamicBounds = _aabbMins.Count == 1
						? bounds
						: dynamicBounds.ExpandedToInclude(bounds);
				}
			}

			foreach (var kvp in _lastItemStates)
			{
				if (!seenItems.Contains(kvp.Key))
					MarkDirty(TreeDirtyState.Rebuild);
			}

			if (truncated)
			{
				HandleOverflow($"OctreeGPU item count exceeded shader capacity of {MaxObjects}.");
				_aabbMins.Clear();
				_aabbMaxs.Clear();
				_transformScratch.Clear();
				_objectIdScratch.Clear();
				newStates.Clear();
				objectCount = 0;
				boundsUsed = _hasUserBounds ? _requestedBounds : dynamicBounds;
				UploadEmptyBuffers();
				return;
			}

			boundsUsed = _hasUserBounds ? _requestedBounds : dynamicBounds;
			if (_aabbMins.Count == 0 && !_hasUserBounds)
				boundsUsed = default;

			UploadAabbData();
			UploadTransformData();
			UploadObjectIdData();

			objectCount = (uint)_aabbMins.Count;
		}

		private void UploadAabbData()
		{
			int count = _aabbMins.Count;
			int total = count * 2;
			Vector4[] combined = new Vector4[total];
			// Interleave min/max pairs for bvh_refit.comp compatibility:
			// struct Aabb { vec4 minBounds; vec4 maxBounds; }; Aabb aabbs[];
			for (int i = 0; i < count; ++i)
			{
				combined[i * 2] = _aabbMins[i];
				combined[i * 2 + 1] = _aabbMaxs[i];
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
			_lastBvhNodeCount = 0;
			ResetOverflowFlagBuffer();
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

			EnsureNodeBufferCapacity(1, out _);
			if (_nodeBuffer is not null)
			{
				ClearClientBuffer(_nodeBuffer);
				_nodeBuffer.PushSubData();
			}

			EnsureQueueBuffer(out _);
			if (_queueBuffer is not null)
			{
				ClearClientBuffer(_queueBuffer);
				_queueBuffer.PushSubData();
			}

			if (_useBvh)
			{
				EnsureBvhBuffers(0, out _);
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
			if (!EnsureNodeBufferCapacity(objectCount, out string? overflowReason) ||
				!EnsureMortonBufferCapacity(objectCount, out overflowReason) ||
				!EnsureQueueBuffer(out overflowReason) ||
				!EnsureBvhBuffers(objectCount, out overflowReason))
			{
				HandleOverflow(overflowReason ?? "GPU octree buffer overflow during compute pipeline setup.");
				return;
			}

			Vector3 sceneMin = bounds.Min;
			Vector3 sceneMax = bounds.Max;
			if (sceneMin == sceneMax)
			{
				sceneMin -= new Vector3(0.5f);
				sceneMax += new Vector3(0.5f);
            }

            DispatchMorton(objectCount, sceneMin, sceneMax);
            SortMortonCodes(objectCount);
            bool builtBvh = false;
            bool refinedBvh = false;
            if (_useBvh && objectCount > 0)
            {
                using (BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Build, objectCount))
                {
                    builtBvh = DispatchBuildBvh(objectCount);
                    refinedBvh = DispatchRefineBvh();
                }

                if (builtBvh)
                {
                    using (BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Refit, objectCount))
                        DispatchRefitBvh();
                }

                if (refinedBvh)
                {
                    using (BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Refit, objectCount))
                        DispatchRefitBvh();
                }
            }
            else
            {
                builtBvh = DispatchBuildBvh(objectCount);
                if (builtBvh)
                    DispatchRefitBvh();
                refinedBvh = DispatchRefineBvh();
                if (refinedBvh)
                    DispatchRefitBvh();
            }
            DispatchBuildOctree(objectCount, sceneMin, sceneMax);
            DispatchPropagate();
            DispatchInitQueue();

            _transformBuffer?.SetBlockIndex(1);
		}

		private void RunRefitOnly(uint objectCount)
		{
			if (_useBvh)
			{
				if (!EnsureBvhBuffers(objectCount, out string? overflowReason))
				{
					HandleOverflow(overflowReason ?? "GPU BVH buffer overflow during refit-only path.");
					return;
				}
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
			program.Uniform("mortonCapacity", GetMortonCapacity());
			if (_overflowFlagBuffer is not null)
				program.BindBuffer(_overflowFlagBuffer, OverflowFlagBinding);
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
			program.Uniform("nodeScalarCapacity", _bvhNodeBuffer.ElementCount);
			program.Uniform("rangeScalarCapacity", _bvhRangeBuffer.ElementCount);
			program.Uniform("mortonCapacity", GetMortonCapacity());
			if (_overflowFlagBuffer is not null)
				program.BindBuffer(_overflowFlagBuffer, OverflowFlagBinding);
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
			if (_overflowFlagBuffer is not null)
				program.BindBuffer(_overflowFlagBuffer, OverflowFlagBinding);
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
			program.Uniform("debugValidation", EnableValidation ? 1u : 0u);
			program.BindBuffer(_transformBuffer, 4);
			if (_overflowFlagBuffer is not null)
				program.BindBuffer(_overflowFlagBuffer, OverflowFlagBinding);

			// Stage 0: Refit leaf nodes
			program.Uniform("refitStage", 0u);
			program.DispatchCompute(ComputeGroups(_lastBvhNodeCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);

			// Stage 1: Propagate internal nodes in parallel
			// Internal nodes count = total nodes - leaf nodes = nodeCount - ((nodeCount + 1) / 2)
			uint leafCount = (_lastBvhNodeCount + 1u) >> 1;
			uint internalCount = _lastBvhNodeCount > leafCount ? _lastBvhNodeCount - leafCount : 0u;
			if (internalCount > 0)
			{
				program.Uniform("refitStage", 1u);
				program.DispatchCompute(ComputeGroups(internalCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
			}
			return true;
		}

		private bool RunRefitPipeline(uint objectCount)
		{
			if (_nodeBuffer is null)
				return false;

			if (objectCount == 0)
			{
				UploadEmptyBuffers();
				return true;
			}

            DispatchRefitOctree(objectCount);
            DispatchPropagate();
            if (_useBvh && _lastBvhNodeCount > 0)
            {
                using (BvhGpuProfiler.Instance.Scope(BvhGpuProfiler.Stage.Refit, objectCount))
                    DispatchRefitBvh();
            }
            DispatchInitQueue();

			_transformBuffer?.SetBlockIndex(1);
			return true;
		}

		private void DispatchRefitOctree(uint objectCount)
		{
			var program = _refitOctreeProgram;
			if (program is null)
				return;

			program.BindBuffer(_aabbBuffer!, 0);
			program.BindBuffer(_nodeBuffer!, 2);
			program.Uniform("numObjects", objectCount);
			program.Uniform("debugValidation", EnableValidation ? 1u : 0u);
			program.Uniform("nodeScalarCapacity", _nodeBuffer!.ElementCount);
			program.Uniform("mortonCapacity", GetMortonCapacity());
			if (_overflowFlagBuffer is not null)
				program.BindBuffer(_overflowFlagBuffer, OverflowFlagBinding);
			program.DispatchCompute(ComputeGroups(objectCount, 256u), 1u, 1u, EMemoryBarrierMask.ShaderStorage);
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
			program.Uniform("nodeScalarCapacity", _nodeBuffer!.ElementCount);
			program.Uniform("mortonCapacity", GetMortonCapacity());
			if (_overflowFlagBuffer is not null)
				program.BindBuffer(_overflowFlagBuffer, OverflowFlagBinding);
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
			program.Uniform("totalNodes", ReadNodeCount());
			uint queueCapacity = _queueBuffer is null
				? 0u
				: (_queueBuffer.ElementCount > 3u ? _queueBuffer.ElementCount - 3u : 0u);
			program.Uniform("queueCapacity", queueCapacity);
			if (_overflowFlagBuffer is not null)
				program.BindBuffer(_overflowFlagBuffer, OverflowFlagBinding);
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
			EnsureRefitPrograms();
			EnsureBvhPrograms();
		}

		private void EnsureBvhPrograms()
		{
			_bvhBuildProgram ??= CreateProgram(ref _bvhBuildShader, "Scene3D/RenderPipeline/bvh_build.comp", true);
			_bvhRefineProgram ??= CreateProgram(ref _bvhRefineShader, "Scene3D/RenderPipeline/bvh_sah_refine.comp", true);
			_bvhRefitProgram ??= CreateProgram(ref _bvhRefitShader, "Scene3D/RenderPipeline/bvh_refit.comp", true);
		}

		public void ReleaseBvhResources()
		{
			lock (_syncRoot)
			{
				_lastBvhNodeCount = 0;
				_bvhNodeBuffer?.Dispose();
				_bvhRangeBuffer?.Dispose();
				_bvhNodeBuffer = null;
				_bvhRangeBuffer = null;
			}
		}

		private void EnsureRefitPrograms()
		{
			_refitOctreeProgram ??= CreateProgram(ref _refitOctreeShader, "Scene3D/RenderPipeline/OctreeGeneration/refit_octree.comp");
		}

		private bool EnsureMortonBufferCapacity(uint objectCount, out string? overflowReason)
		{
			overflowReason = null;
			uint padded = Math.Max(1u, XRMath.NextPowerOfTwo(Math.Max(1u, objectCount)));
			uint required = Math.Max(padded, 1u) * 2u;
			if (_mortonBuffer is null)
			{
				_mortonBuffer = CreateBuffer("GPUOctree.Morton", true);
				_mortonBuffer.SetDataRaw(new uint[required], (int)required);
				return true;
			}

			return TryEnsureBufferCapacity(_mortonBuffer, required, "Morton buffer", true, out overflowReason);
		}

		private bool EnsureBvhBuffers(uint objectCount, out string? overflowReason)
		{
			overflowReason = null;
			if (!_useBvh)
			{
				_lastBvhNodeCount = 0;
				return true;
			}

			// Skip buffer allocation entirely when there are no objects
			if (objectCount == 0)
			{
				_lastBvhNodeCount = 0;
				return true;
			}

			uint leafCount = (objectCount + _maxLeafPrimitives - 1u) / _maxLeafPrimitives;
			uint nodeCount = leafCount > 0 ? (leafCount * 2u) - 1u : 0u;
			_lastBvhNodeCount = nodeCount;

			// Ensure at least 1 node capacity for buffer sizing
			uint nodeScalars = GpuBvhLayout.NodeScalarCapacity(Math.Max(nodeCount, 1u));
			uint rangeScalars = GpuBvhLayout.RangeScalarCapacity(Math.Max(nodeCount, 1u));

			if (_bvhNodeBuffer is null)
			{
				_bvhNodeBuffer = CreateBvhBuffer("GPUOctree.BvhNodes", nodeScalars);
				_bvhNodeBuffer.SetBlockIndex(6);
			}
			else if (!TryEnsureBufferCapacity(_bvhNodeBuffer, nodeScalars, "BVH node buffer", true, out overflowReason))
				return false;

			if (_bvhRangeBuffer is null)
			{
				_bvhRangeBuffer = CreateBvhBuffer("GPUOctree.BvhRanges", rangeScalars);
				_bvhRangeBuffer.SetBlockIndex(7);
			}
			else if (!TryEnsureBufferCapacity(_bvhRangeBuffer, rangeScalars, "BVH range buffer", true, out overflowReason))
				return false;

			return true;
		}

		private bool EnsureNodeBufferCapacity(uint objectCount, out string? overflowReason)
		{
			overflowReason = null;
			uint nodeCount = objectCount == 0 ? 0 : (objectCount * 2u) - 1u;
			uint required = HeaderScalarCount + nodeCount * NodeStrideScalars;
			if (_nodeBuffer is null)
			{
				_nodeBuffer = CreateBuffer("GPUOctree.Nodes", true);
				_nodeBuffer.SetBlockIndex(2);
				_nodeBuffer.SetDataRaw(new uint[required == 0 ? HeaderScalarCount : required], (int)(required == 0 ? HeaderScalarCount : required));
			}
			else if (!TryEnsureBufferCapacity(_nodeBuffer, required, "Octree node buffer", true, out overflowReason))
				return false;

			return true;
		}

		private bool EnsureQueueBuffer(out string? overflowReason)
		{
			uint length = MaxQueueSize + 3u;
			overflowReason = null;
			if (_queueBuffer is null)
			{
				_queueBuffer = CreateBuffer("GPUOctree.Queue", true);
				_queueBuffer.SetBlockIndex(4);
				_queueBuffer.SetDataRaw(new uint[length], (int)length);
			}
			else if (!TryEnsureBufferCapacity(_queueBuffer, length, "Octree queue buffer", false, out overflowReason))
				return false;

			return true;
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
			// Match both plain const declarations and GLSL specialization constants
			// e.g., "const uint MAX_LEAF_PRIMITIVES = 1u;" or "layout (constant_id = 0) const uint MAX_LEAF_PRIMITIVES = 1u;"
			string patched = Regex.Replace(
				source,
				@"(layout\s*\(\s*constant_id\s*=\s*\d+\s*\)\s*)?const\s+uint\s+MAX_LEAF_PRIMITIVES\s*=\s*[0-9]+u?\s*;",
				$"const uint MAX_LEAF_PRIMITIVES = {maxLeafPrimitives}u;",
				RegexOptions.CultureInvariant);

			patched = Regex.Replace(
				patched,
				@"(layout\s*\(\s*constant_id\s*=\s*\d+\s*\)\s*)?const\s+uint\s+BVH_MODE\s*=\s*[0-9]+u?\s*;",
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

		private uint GetMortonCapacity()
			=> _mortonBuffer is null ? 0u : _mortonBuffer.ElementCount / 2u;

		private void EnsureOverflowFlagBuffer()
		{
			if (_overflowFlagBuffer is not null)
				return;

			_overflowFlagBuffer = CreateBuffer("GPUOctree.OverflowFlag", true);
			_overflowFlagBuffer.SetBlockIndex(OverflowFlagBinding);
			_overflowFlagBuffer.SetDataRaw(new uint[] { 0u }, 1);
		}

		private void ResetOverflowFlagBuffer()
		{
			EnsureOverflowFlagBuffer();
			if (_overflowFlagBuffer is null)
				return;

			if (_overflowFlagBuffer.ClientSideSource is null)
				_overflowFlagBuffer.SetDataRaw(new uint[] { 0u }, 1);
			else
				_overflowFlagBuffer.SetDataRawAtIndex(0, 0u);

			_overflowFlagBuffer.PushSubData();
		}

		private void ConsumeOverflowFlag(uint objectCount, uint nodeCount)
		{
			if (_overflowed)
				return;

			uint flags = ReadFirstUint(_overflowFlagBuffer);
			if (flags == 0)
				return;

			HandleOverflow(DescribeOverflow(flags, objectCount, nodeCount));
		}

		private string DescribeOverflow(uint flags, uint objectCount, uint nodeCount)
		{
			List<string> reasons = new();
			if ((flags & OverflowMortonBit) != 0)
				reasons.Add($"morton buffer capacity exceeded for {objectCount} objects");
			if ((flags & OverflowNodeBit) != 0)
				reasons.Add($"octree node buffer capacity exceeded for {nodeCount} nodes");
			if ((flags & OverflowQueueBit) != 0)
				reasons.Add($"queue capacity {MaxQueueSize} exceeded for {nodeCount} nodes");
			if ((flags & OverflowBvhBit) != 0)
				reasons.Add("BVH buffer capacity exceeded");

			return reasons.Count == 0
				? "GPU octree overflow sentinel flagged an unknown condition."
				: $"GPU octree overflow detected ({string.Join(", ", reasons)}).";
		}

		private void HandleOverflow(string reason)
		{
			_overflowed = true;
			_overflowReason = reason;
			if (!_overflowWarningIssued)
			{
				Debug.LogWarning($"[OctreeGPU] {reason} Falling back to CPU render tree.");
				_overflowWarningIssued = true;
			}

			_gpuDirty = true;
			_dirtyState = TreeDirtyState.Rebuild;
			_transformsDirty = true;
			UploadEmptyBuffers();
			RouteToCpuRender();
		}

		private void RouteToCpuRender()
		{
			if (Engine.Rendering.State.CurrentRenderingPipeline?.RenderState?.RenderingScene is VisualScene3D scene)
				scene.ApplyRenderDispatchPreference(false);
		}

		private void TryRecoverFromOverflow(uint objectCount, uint nodeCount)
		{
			if (!_overflowed)
				return;

			if (_items.Count > MaxObjects)
				return;

			if (EnsureCapacityForCounts(objectCount, nodeCount, out _))
			{
				_overflowed = false;
				_overflowReason = null;
				_overflowWarningIssued = false;
				ResetOverflowFlagBuffer();
			}
		}

		private bool EnsureCapacityForCounts(uint objectCount, uint nodeCount, out string? overflowReason)
		{
			EnsureOverflowFlagBuffer();
			overflowReason = null;

			if (_items.Count > MaxObjects)
			{
				overflowReason = $"Object count {_items.Count} exceeds shader MAX_OBJECTS={MaxObjects}.";
				return false;
			}

			if (objectCount > MaxObjects)
			{
				overflowReason = $"Object count {objectCount} exceeds shader MAX_OBJECTS={MaxObjects}.";
				return false;
			}

			if (nodeCount > MaxQueueSize)
			{
				uint queueCapacity = _queueBuffer is null
					? MaxQueueSize
					: Math.Min(MaxQueueSize, _queueBuffer.ElementCount > 3u ? _queueBuffer.ElementCount - 3u : 0u);
				overflowReason = $"Octree node count {nodeCount} exceeds queue capacity {queueCapacity}.";
				return false;
			}

			if (!EnsureMortonBufferCapacity(objectCount, out overflowReason))
				return false;

			if (!EnsureNodeBufferCapacity(objectCount, out overflowReason))
				return false;

			if (!EnsureQueueBuffer(out overflowReason))
				return false;

			if (_useBvh && !EnsureBvhBuffers(objectCount, out overflowReason))
				return false;

			ResetOverflowFlagBuffer();
			return true;
		}

		internal bool TryValidateCapacityForTests(uint objectCount, uint nodeCount, out string? overflowReason)
			=> EnsureCapacityForCounts(objectCount, nodeCount, out overflowReason);

		private static uint GetSafeElementSize(XRDataBuffer buffer)
			=> buffer.ElementSize == 0 ? sizeof(uint) : buffer.ElementSize;

		private static bool TryEnsureBufferCapacity(XRDataBuffer buffer, uint requiredElements, string label, bool alignToPowerOfTwo, out string? overflowReason)
		{
			overflowReason = null;
			uint stride = GetSafeElementSize(buffer);
			ulong requiredBytes = (ulong)requiredElements * stride;
			if (requiredBytes > int.MaxValue)
			{
				overflowReason = $"{label} requires {requiredBytes} bytes which exceeds supported allocation limits.";
				return false;
			}

			if (buffer.ElementCount >= requiredElements)
				return true;

			if (!buffer.Resizable)
			{
				overflowReason = $"{label} is not resizable (capacity={buffer.ElementCount}, required={requiredElements}).";
				return false;
			}

			buffer.Resize(requiredElements, false, alignToPowerOfTwo);
			if (buffer.ElementCount < requiredElements)
			{
				overflowReason = $"{label} could not be resized to {requiredElements} elements.";
				return false;
			}

			return true;
		}

		private static uint ReadFirstUint(XRDataBuffer? buffer)
		{
			if (buffer?.ClientSideSource is null)
				return 0u;

			unsafe
			{
				return ((uint*)buffer.Address.Pointer)[0];
			}
		}

		private static unsafe void ClearClientBuffer(XRDataBuffer buffer)
		{
			if (buffer.ClientSideSource is null)
				return;
			Span<byte> span = new(buffer.Address.Pointer, (int)buffer.Length);
			span.Clear();
		}

		private static uint ComputeGroups(uint workItems, uint localSize)
			=> Math.Max(1u, (workItems + localSize - 1u) / localSize);

		private static bool AreBoundsEqual(AABB a, AABB b, float epsilon = 0.0001f)
			=> Vector3.DistanceSquared(a.Min, b.Min) <= epsilon * epsilon
			&& Vector3.DistanceSquared(a.Max, b.Max) <= epsilon * epsilon;

		/// <summary>
		/// Marks the tree as requiring rebuild or refit. Thread-safe.
		/// </summary>
		private void MarkDirty(TreeDirtyState reason = TreeDirtyState.Rebuild)
		{
			lock (_syncRoot)
			{
				if (reason > _dirtyState)
					_dirtyState = reason;
				_gpuDirty = true;
				_transformsDirty = true;
			}
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

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (_disposed)
				return;

			if (disposing)
			{
				_aabbBuffer?.Dispose();
				_transformBuffer?.Dispose();
				_mortonBuffer?.Dispose();
				_nodeBuffer?.Dispose();
				_queueBuffer?.Dispose();
				_objectIdBuffer?.Dispose();
				_bvhNodeBuffer?.Dispose();
				_bvhRangeBuffer?.Dispose();
				_overflowFlagBuffer?.Dispose();

				// XRShader and XRRenderProgram inherit from GenericRenderObject which uses Destroy() instead of Dispose()
				_mortonShader?.Destroy();
				_smallSortShader?.Destroy();
				_padShader?.Destroy();
				_tileSortShader?.Destroy();
				_mergeShader?.Destroy();
				_buildShader?.Destroy();
				_propagateShader?.Destroy();
				_initQueueShader?.Destroy();
				_bvhBuildShader?.Destroy();
				_bvhRefineShader?.Destroy();
				_bvhRefitShader?.Destroy();
				_refitOctreeShader?.Destroy();

				_mortonProgram?.Destroy();
				_smallSortProgram?.Destroy();
				_padProgram?.Destroy();
				_tileSortProgram?.Destroy();
				_mergeProgram?.Destroy();
				_buildProgram?.Destroy();
				_propagateProgram?.Destroy();
				_initQueueProgram?.Destroy();
				_bvhBuildProgram?.Destroy();
				_bvhRefineProgram?.Destroy();
				_bvhRefitProgram?.Destroy();
				_refitOctreeProgram?.Destroy();
			}

			_disposed = true;
		}

		~OctreeGPU()
		{
			Dispose(false);
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
