// =====================================================================================
// GPUScene.Bvh.cs - BVH configuration and IGpuBvhProvider implementation.
// Part of the GPUScene partial class. See GPUScene.cs for the canonical class summary.
// =====================================================================================

using XREngine.Extensions;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Info;
using XREngine.Rendering.Meshlets;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands
{
    public partial class GPUScene
    {

        // -------------------------------------------------------------------------
        // BVH Configuration: Settings for GPU-accelerated hierarchical culling.
        // -------------------------------------------------------------------------

        /// <summary>Whether to use GPU BVH traversal for culling.</summary>
        private bool _useGpuBvh = RuntimeEngine.EffectiveSettings.UseGpuBvh;

        /// <summary>External BVH provider for GPU-accelerated culling (optional).</summary>
        private IGpuBvhProvider? _externalBvhProvider;

        /// <summary>Whether to use the internal command-based BVH.</summary>
        private bool _useInternalBvh = false;

        /// <summary>
        /// Indicates whether the GPU BVH traversal path should be used when available.
        /// </summary>
        public bool UseGpuBvh
        {
            get => _useGpuBvh;
            set
            {
                if (!SetField(ref _useGpuBvh, value))
                    return;

                string path = value ? "GPU BVH" : "GPU octree";
                Debug.Meshes($"[GPUScene] Active traversal path set to {path}.");
            }
        }

        /// <summary>
        /// Gets or sets the BVH provider for GPU-accelerated culling.
        /// When set, the BVH culling path will use the provider's buffers for traversal.
        /// If null and UseGpuBvh is true, falls back to internal command BVH.
        /// </summary>
        public IGpuBvhProvider? BvhProvider
        {
            get => _externalBvhProvider ?? (_useInternalBvh ? this as IGpuBvhProvider : null);
            set => SetField(ref _externalBvhProvider, value);
        }

        /// <summary>
        /// Enables or disables the internal command-based BVH.
        /// When enabled, GPUScene builds a BVH over command bounding spheres.
        /// </summary>
        public bool UseInternalBvh
        {
            get => _useInternalBvh;
            set
            {
                if (!SetField(ref _useInternalBvh, value))
                    return;

                if (value)
                    MarkBvhDirty();
            }
        }

        // -------------------------------------------------------------------------
        // BVH Implementation: Provides GPU-accessible BVH for hierarchical culling.
        // The internal BVH is built over command bounding spheres.
        // -------------------------------------------------------------------------

        /// <summary>BVH node buffer containing the tree structure.</summary>
        private GpuBvhTree? _gpuBvhTree;

        /// <summary>BVH range buffer mapping nodes to primitive ranges.</summary>
        private XRDataBuffer? _commandAabbBuffer;

        /// <summary>BVH morton buffer containing morton codes and object IDs.</summary>
        private XRShader? _commandAabbShader;

        private XRRenderProgram? _commandAabbProgram;

        /// <summary>Flag indicating the BVH needs to be rebuilt.</summary>
        private bool _bvhDirty = false;

        /// <summary>Flag indicating the BVH has been built and is ready for use.</summary>
        private bool _bvhReady = false;

        /// <summary>Total number of nodes in the BVH.</summary>
        private uint _bvhNodeCount = 0;

        /// <summary>Total number of primitives currently represented by the BVH.</summary>
        private uint _bvhPrimitiveCount = 0;

        /// <summary>Flag indicating a BVH refit is pending on the render thread.</summary>
        private volatile bool _bvhRefitPending = false;

        /// <summary>True after a failed BVH build until scene command data changes.</summary>
        private bool _bvhBuildSuppressed = false;

        /// <summary>Command count represented by the suppressed failed BVH build.</summary>
        private uint _bvhSuppressedCommandCount = 0;

        /// <inheritdoc/>
        XRDataBuffer? IGpuBvhProvider.BvhNodeBuffer => _useInternalBvh ? _gpuBvhTree?.NodeBuffer : null;

        /// <inheritdoc/>
        XRDataBuffer? IGpuBvhProvider.BvhRangeBuffer => _useInternalBvh ? _gpuBvhTree?.RangeBuffer : null;

        /// <inheritdoc/>
        XRDataBuffer? IGpuBvhProvider.BvhMortonBuffer => _useInternalBvh ? _gpuBvhTree?.MortonBuffer : null;

        /// <inheritdoc/>
        uint IGpuBvhProvider.BvhNodeCount => _useInternalBvh ? _bvhNodeCount : 0u;

        /// <inheritdoc/>
        bool IGpuBvhProvider.IsBvhReady => _useInternalBvh && _bvhReady && !_bvhDirty && _bvhNodeCount > 0;

        /// <summary>
        /// Marks the internal BVH as needing a rebuild.
        /// </summary>
        public void MarkBvhDirty()
        {
            _bvhDirty = true;
            _bvhBuildSuppressed = false;
            _bvhSuppressedCommandCount = 0;
        }

        private void MarkBvhDirtyUnlessSuppressed(uint commandCount)
        {
            if (_bvhBuildSuppressed && _bvhSuppressedCommandCount == commandCount)
            {
                _bvhReady = false;
                _bvhRefitPending = false;
                return;
            }

            MarkBvhDirty();
        }

        /// <summary>
        /// Rebuilds the internal command BVH if it's dirty and enabled.
        /// This builds a simple CPU-side BVH and uploads to GPU buffers.
        /// </summary>
        public void RebuildBvhIfDirty()
        {
            if (!_useInternalBvh || !_bvhDirty)
                return;

            if (RebuildInternalBvh())
                _bvhDirty = false;
        }

        private bool RebuildInternalBvh()
        {
            uint commandCount = _totalCommandCount;
            if (commandCount == 0 || _allLoadedCommandsBuffer is null)
            {
                _bvhReady = false;
                _bvhNodeCount = 0;
                _bvhPrimitiveCount = 0;
                _bvhBuildSuppressed = false;
                _bvhSuppressedCommandCount = 0;
                _gpuBvhTree?.Clear();
                return true;
            }

            EnsureGpuBvhResources(commandCount);
            if (!EnsureBvhProgramsReady(commandCount))
            {
                _bvhReady = false;
                return false;
            }

            // Tight per-command world AABBs are populated CPU-side at command insert/update
            // time via WriteTightCommandAabb (see #region Bounds Helpers). The legacy
            // sphere-derived GPU AABB build pass (bvh_aabb_from_commands.comp) is no longer
            // dispatched: it inflated each AABB by ~sqrt(3) per axis (cube of the bounding
            // sphere), which compounded up the BVH and produced loose visualization /
            // suboptimal cull bounds.

            _gpuBvhTree!.Build(_commandAabbBuffer!, commandCount, _bounds);

            _bvhNodeCount = _gpuBvhTree.NodeCount;
            _bvhPrimitiveCount = _gpuBvhTree.PrimitiveCount;
            _bvhReady = _bvhNodeCount > 0 && _bvhPrimitiveCount == commandCount;
            _bvhBuildSuppressed = !_bvhReady;
            _bvhSuppressedCommandCount = _bvhBuildSuppressed ? commandCount : 0u;

            if (IsGpuSceneLoggingEnabled())
            {
                if (_bvhReady)
                    SceneLog($"[GPUScene] Built internal BVH with {_bvhNodeCount} nodes for {commandCount} commands");
                else
                    SceneLog($"[GPUScene] Internal BVH build failed for {commandCount} commands; suppressing rebuilds until command data changes.");
            }

            return true;
        }

        private bool RefitInternalBvh(uint commandCount)
        {
            if (_gpuBvhTree is null || _allLoadedCommandsBuffer is null || _commandAabbBuffer is null)
            {
                _bvhReady = false;
                return false;
            }

            if (commandCount == 0 || _bvhPrimitiveCount == 0)
                return true;

            if (!EnsureBvhProgramsReady(commandCount))
            {
                _bvhReady = false;
                return false;
            }

            // Tight per-command world AABBs are maintained via WriteTightCommandAabb on
            // every insert/update; nothing to refresh here.
            _gpuBvhTree.Refit();

            _bvhNodeCount = _gpuBvhTree.NodeCount;
            _bvhPrimitiveCount = _gpuBvhTree.PrimitiveCount;
            _bvhReady = _bvhNodeCount > 0 && _bvhPrimitiveCount == commandCount;
            return true;
        }

        public void PrepareBvhForCulling(uint commandCount)
        {
            if (!_useInternalBvh)
                return;

            if (commandCount == 0 || _allLoadedCommandsBuffer is null)
            {
                _bvhReady = false;
                _bvhNodeCount = 0;
                _bvhPrimitiveCount = 0;
                _bvhBuildSuppressed = false;
                _bvhSuppressedCommandCount = 0;
                _gpuBvhTree?.Clear();
                return;
            }

            if (_gpuBvhTree?.PollPendingOverflow() == true)
            {
                _bvhReady = false;
                _bvhNodeCount = 0;
                _bvhPrimitiveCount = 0;
                _bvhDirty = false;
                _bvhRefitPending = false;
                _bvhBuildSuppressed = true;
                _bvhSuppressedCommandCount = commandCount;
                return;
            }

            if (_bvhBuildSuppressed && !_bvhDirty)
            {
                if (_bvhSuppressedCommandCount == commandCount)
                    return;

                _bvhBuildSuppressed = false;
                _bvhSuppressedCommandCount = 0;
            }

            // Even when _bvhDirty was just set (e.g. by Add/Remove that didn't
            // actually change the post-mutation count), if the last build attempt
            // overflowed at this exact command count, retrying with the same
            // primitive count + same node-capacity algorithm will overflow again.
            // Skip rebuild and let the next *actual* count change unsuppress us.
            // This stops the per-frame Build -> overflow -> log -> suppress loop
            // that was costing us ~120 fps of headroom on small scenes.
            if (_bvhBuildSuppressed && _bvhSuppressedCommandCount == commandCount)
            {
                _bvhDirty = false;
                _bvhRefitPending = false;
                return;
            }

            if (_bvhDirty || !_bvhReady || _bvhPrimitiveCount != commandCount || _gpuBvhTree is null)
            {
                if (RebuildInternalBvh())
                {
                    _bvhDirty = false;
                    _bvhRefitPending = false;
                }
                return;
            }

            if (_bvhRefitPending)
            {
                if (RefitInternalBvh(commandCount))
                    _bvhRefitPending = false;
            }
        }

        private void EnsureGpuBvhResources(uint commandCount)
        {
            _gpuBvhTree ??= new GpuBvhTree();
            EnsureCommandAabbBuffer(commandCount);
            // The legacy GPU AABB build program (EnsureCommandAabbProgram) is intentionally
            // not initialized here: per-command world AABBs are now CPU-populated tight
            // bounds via WriteTightCommandAabb. The program/shader remain on disk only as
            // a fallback for future paths that want a GPU-only path.
        }

        private bool EnsureBvhProgramsReady(uint commandCount)
        {
            // _commandAabbProgram is no longer required (see EnsureGpuBvhResources).
            bool ready = true;
            if (_gpuBvhTree is not null)
                ready &= _gpuBvhTree.EnsureProgramsReady(commandCount);

            return ready;
        }

        private static bool EnsureProgramReady(XRRenderProgram? program)
        {
            if (program is null)
                return false;

            if (program.IsLinked)
                return true;

            if (!program.LinkReady)
                program.Link();

            return false;
        }

        private void EnsureCommandAabbBuffer(uint commandCount)
        {
            if (_commandAabbBuffer is null)
            {
                _commandAabbBuffer = new XRDataBuffer(
                    "GPUScene_CommandAabbs",
                    EBufferTarget.ShaderStorageBuffer,
                    commandCount,
                    EComponentType.Float,
                    8,
                    false,
                    true)
                {
                    Usage = EBufferUsage.DynamicCopy,
                    Resizable = true,
                    DisposeOnPush = false,
                    PadEndingToVec4 = true,
                    ShouldMap = false
                };
            }
            else if (_commandAabbBuffer.ElementCount < commandCount)
            {
                _commandAabbBuffer.Resize(commandCount, false, true);
            }
        }

        private void EnsureCommandAabbProgram()
        {
            if (_commandAabbProgram is not null)
                return;

            _commandAabbShader ??= ShaderHelper.LoadEngineShader("Scene3D/RenderPipeline/bvh_aabb_from_commands.comp", EShaderType.Compute);
            _commandAabbProgram = new XRRenderProgram(true, false, _commandAabbShader);
        }

        private void DispatchCommandAabbBuild(uint commandCount)
        {
            if (_commandAabbProgram is null || _commandAabbBuffer is null)
                return;

            var program = _commandAabbProgram;
            program.BindBuffer(BoundsBuffer, 0);
            program.BindBuffer(_commandAabbBuffer, 1);
            program.Uniform("numCommands", commandCount);

            (uint x, uint y, uint z) = XRRenderProgram.ComputeDispatch.ForCommands(Math.Max(commandCount, 1u));
            program.DispatchCompute(x, y, z, EMemoryBarrierMask.ShaderStorage);
        }

    }
}
