// =====================================================================================
// GPUScene.MeshMaterialIds.cs - Mesh / material ID allocation helpers.
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

        /// <summary>
        /// Gets or creates a unique mesh ID for the given mesh.
        /// </summary>
        /// <remarks>
        /// Incremental IDs start at 1 (0 is reserved/invalid).
        /// </remarks>
        /// <param name="mesh">The mesh to get an ID for.</param>
        /// <param name="index">The assigned mesh ID.</param>
        /// <returns>True if the mesh was already known; false if newly added.</returns>
        private bool GetOrCreateMeshID(XRMesh? mesh, out uint index)
        {
            index = uint.MaxValue;
            if (mesh is null)
                return false;

            bool contains = _meshIDMap.ContainsKey(mesh);
            index = _meshIDMap.GetOrAdd(mesh, _ => Interlocked.Increment(ref _nextMeshID));
            _idToMesh.TryAdd(index, mesh);
            return contains;
        }

        /// <summary>
        /// Gets or creates a unique material ID for the given material.
        /// </summary>
        /// <remarks>
        /// Incremental IDs start at 1 (0 is reserved/invalid).
        /// </remarks>
        /// <param name="material">The material to get an ID for.</param>
        /// <param name="index">The assigned material ID.</param>
        /// <returns>True if the material was already known; false if newly added.</returns>
        private bool GetOrCreateMaterialID(XRMaterial? material, out uint index)
        {
            index = uint.MaxValue;
            if (material is null)
                return false;

            bool contains = _materialIDMap.ContainsKey(material);
            index = _materialIDMap.GetOrAdd(material, _ => Interlocked.Increment(ref _nextMaterialID));
            // Maintain reverse mapping for render-time lookups
            _idToMaterial.TryAdd(index, material);

            if (!contains)
            {
                int remaining = Interlocked.Decrement(ref _materialDebugLogBudget);
                if (remaining >= 0)
                {
                    string matName = material.Name ?? "<unnamed>";
                    SceneLog($"[GPUScene] Assigned MaterialID={index} to '{matName}' (hash=0x{material.GetHashCode():X8}). Remaining logs: {remaining}");
                    if (remaining == 0)
                        SceneLog("[GPUScene] MaterialID assignment log budget exhausted; suppressing further logs.");
                }
            }
            return contains;
        }

    }
}
