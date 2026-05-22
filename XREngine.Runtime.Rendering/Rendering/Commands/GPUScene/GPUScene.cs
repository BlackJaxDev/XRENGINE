// =====================================================================================
// GPUScene.cs - GPU-Resident Scene Data Management (entry partial)
// =====================================================================================
//
// PURPOSE:
// GPUScene manages all GPU-resident scene data for indirect rendering, including:
//   - Render commands converted to a GPU-friendly format (GPUIndirectRenderCommand)
//   - A unified mesh atlas containing all vertex/index data for bindless rendering
//   - Material and mesh ID mappings for GPU lookups
//   - Optional internal BVH for GPU-based culling
//
// THREADING MODEL:
// GPUScene uses double-buffering to safely coordinate between two threads:
//
//   1. UPDATE/COLLECT THREAD: writes to "Updating" buffers
//      - Add() and Remove() mutate _updatingCommandsBuffer and _updatingCommandCount
//      - These operations occur during the Update or PreCollectVisible phases
//
//   2. RENDER THREAD: reads from "AllLoaded" buffers
//      - Rendering reads from _allLoadedCommandsBuffer and _totalCommandCount
//      - These are stable snapshots that don't change during rendering
//
// BUFFER SWAP SEQUENCE:
//   PreCollectVisible (sequential) -> CollectVisible (parallel) -> PostCollectVisible -> SwapBuffers -> Render
//
//   During SwapBuffers(), the updating buffer contents are copied to the render buffer,
//   making the latest scene state visible to the render thread safely.
//
// FILE LAYOUT:
// The implementation is split across several partial-class files to keep individual
// files manageable. This file owns only the class declaration plus tiny private
// nested helper types. See the per-file headers for what each partial owns:
//
//   GPUScene.BoundsHelpers.cs     - world-space bounds math + GPU-owned command AABB API
//   GPUScene.MeshAtlas.cs         - mesh atlas state, nested types and read-only properties
//   GPUScene.AtlasManagement.cs   - atlas build / upload / streaming / migration
//   GPUScene.Materials.cs         - material / mesh / LOD ID maps + lookup API
//   GPUScene.MeshMaterialIds.cs   - mesh / material ID allocation helpers
//   GPUScene.Logging.cs           - budget-limited debug logging
//   GPUScene.Bvh.cs               - BVH configuration + IGpuBvhProvider implementation
//   GPUScene.Lifecycle.cs         - init / dispose / per-frame hooks
//   GPUScene.CommandBuffers.cs    - double-buffered command SSBO management
//   GPUScene.AddRemove.cs         - Add / Remove / Update of draw commands
//   GPUScene.Soa.cs               - structure-of-arrays scene database
//   GPUScene.CommandConversion.cs - CPU -> GPU command conversion + Phase 1 updates
//
// Public top-level types live in their own files:
//   EAtlasTier.cs
//
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
    /// <summary>
    /// Manages GPU-resident scene data for indirect rendering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class holds all render commands converted to a GPU-friendly format, along with
    /// unified mesh atlas data, material/mesh ID mappings, and optional BVH structures for
    /// GPU-based culling.
    /// </para>
    /// <para>
    /// <b>Threading Model:</b> Uses double-buffering to safely coordinate between the
    /// update/collect thread (which writes to updating buffers) and the render thread
    /// (which reads from all-loaded buffers). Call <see cref="SwapCommandBuffers"/> during
    /// the swap phase to synchronize state between threads.
    /// </para>
    /// <para><b>Memory Layout:</b></para>
    /// <list type="bullet">
    /// <item><description>Command Buffer: compact <see cref="GPUIndirectRenderCommand"/> compatibility records (20 lanes each)</description></item>
    /// <item><description>Mesh Atlas: Unified vertex attributes (positions, normals, tangents, UVs) and index data</description></item>
    /// <item><description>MeshData Buffer: Per-mesh metadata mapping mesh IDs to atlas offsets</description></item>
    /// </list>
    /// <para>
    /// <b>File Layout:</b> implementation is split across several partial-class files; see the
    /// file header of <c>GPUScene.cs</c> for the per-partial responsibility map.
    /// </para>
    /// </remarks>
    public partial class GPUScene : XRBase, IGpuBvhProvider
    {
        /// <summary>
        /// Hands out stable per-renderable GPU IDs (transform / skinning / state-class slots),
        /// recycling freed IDs from a stack before bumping the next-ID watermark.
        /// IDs start at 1; 0 is reserved as the "unset" sentinel.
        /// </summary>
        private sealed class StableGpuIdAllocator
        {
            private readonly Stack<uint> _free = new();
            private uint _next = 1u;

            public uint Allocate()
                => _free.Count > 0 ? _free.Pop() : _next++;

            public void Release(uint id)
            {
                if (id == 0u || id >= _next)
                    return;

                _free.Push(id);
            }

            public void Clear()
            {
                _free.Clear();
                _next = 1u;
            }
        }

        /// <summary>
        /// Tracks the minimal [Min, MaxExclusive) range that has been mutated since the
        /// last GPU upload so we can issue a single contiguous PushSubData instead of
        /// re-uploading the whole buffer. Mark() unions in a new index/run; Clear() resets.
        /// </summary>
        private struct DirtyRange
        {
            public bool HasValue;
            public uint Min;
            public uint MaxExclusive;

            public void Mark(uint index)
            {
                if (!HasValue)
                {
                    HasValue = true;
                    Min = index;
                    MaxExclusive = index + 1u;
                    return;
                }

                Min = Math.Min(Min, index);
                MaxExclusive = Math.Max(MaxExclusive, index + 1u);
            }

            public void Mark(uint start, uint count)
            {
                if (count == 0u)
                    return;

                if (!HasValue)
                {
                    HasValue = true;
                    Min = start;
                    MaxExclusive = start + count;
                    return;
                }

                Min = Math.Min(Min, start);
                MaxExclusive = Math.Max(MaxExclusive, start + count);
            }

            public void Clear()
            {
                HasValue = false;
                Min = 0u;
                MaxExclusive = 0u;
            }
        }
    }
}
