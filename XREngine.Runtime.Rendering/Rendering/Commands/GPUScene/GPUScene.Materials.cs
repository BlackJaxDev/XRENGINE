// =====================================================================================
// GPUScene.Materials.cs - Material / mesh / LOD ID maps and the public Material/LOD lookup API.
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
        // Material/Mesh ID Maps: Concurrent dictionaries for thread-safe ID assignment.
        // IDs start at 1 (0 is reserved/invalid).
        // -------------------------------------------------------------------------

        /// <summary>Maps XRMaterial instances to unique GPU IDs.</summary>
        private readonly ConcurrentDictionary<XRMaterial, uint> _materialIDMap = new();

        /// <summary>Reverse mapping from material ID to XRMaterial instance.</summary>
        private readonly ConcurrentDictionary<uint, XRMaterial> _idToMaterial = new();

        private readonly Dictionary<uint, XRMaterial> _stateClassRepresentativeMaterials = [];
        private readonly Dictionary<uint, MaterialStateGpu> _materialStateByClass = [];

        /// <summary>Reverse mapping from mesh ID to XRMesh instance.</summary>
        private readonly ConcurrentDictionary<uint, XRMesh> _idToMesh = new();

        private readonly Dictionary<RenderableMesh, Dictionary<uint, uint>> _renderableLogicalMeshIdMap = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        private readonly Dictionary<XRMesh, uint> _standaloneLogicalMeshIdMap = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        private readonly Dictionary<uint, LogicalMeshState> _logicalMeshStates = [];
        private XRDataBuffer? _lodTableBuffer;
        private XRDataBuffer? _lodRequestBuffer;
        private uint _nextLogicalMeshID = 1;

        /// <summary>Next material ID to assign (incremented atomically).</summary>
        private uint _nextMaterialID = 1;

        private readonly StableGpuIdAllocator _transformIdAllocator = new();
        private readonly StableGpuIdAllocator _skinIdAllocator = new();
        private readonly StableGpuIdAllocator _stateClassIdAllocator = new();

        /// <summary>
        /// Exposes a read-only view of the current material ID map (ID -> XRMaterial).
        /// </summary>
        public IReadOnlyDictionary<uint, XRMaterial> MaterialMap => _idToMaterial;

        /// <summary>
        /// Representative material per coarse GPU state class. Phase C batches by these IDs;
        /// Phase D replaces the representative with real per-draw material-table fetches.
        /// </summary>
        public IReadOnlyDictionary<uint, XRMaterial> StateClassMaterialMap => _stateClassRepresentativeMaterials;

        /// <summary>
        /// Attempts to get a material by its ID.
        /// </summary>
        /// <param name="id">The material ID to look up.</param>
        /// <param name="material">The material if found; null otherwise.</param>
        /// <returns>True if the material was found; false otherwise.</returns>
        public bool TryGetMaterial(uint id, out XRMaterial? material)
        {
            bool ok = _idToMaterial.TryGetValue(id, out var mat);
            material = ok ? mat : null;
            return ok;
        }

        public bool TryGetTransform(uint transformId, out Matrix4x4 worldMatrix)
        {
            worldMatrix = Matrix4x4.Identity;
            XRDataBuffer? buffer = _allLoadedTransformBuffer ?? _updatingTransformBuffer;
            if (transformId >= (buffer?.ElementCount ?? 0u))
                return false;

            worldMatrix = buffer!.GetDataRawAtIndex<TransformGpu>(transformId).WorldMatrix;
            return true;
        }

        public XRDataBuffer LODTableBuffer => _lodTableBuffer ??= MakeLodTableBuffer();

        public XRDataBuffer LODRequestBuffer => _lodRequestBuffer ??= MakeLodRequestBuffer();

        public bool HasLogicalMeshEntries => _logicalMeshStates.Count > 0;

        public bool TryGetLodTableEntry(uint logicalMeshId, out LODTableEntry entry)
        {
            entry = default;
            if (logicalMeshId == 0 || !_logicalMeshStates.TryGetValue(logicalMeshId, out LogicalMeshState? state) || state.LODCount == 0)
                return false;

            entry = state.ToEntry();
            return true;
        }

        public bool RegisterLogicalMeshLODs(IEnumerable<(XRMesh mesh, float minProjectedRadiusPixels)> lodMeshes, out uint logicalMeshId, out string? failureReason)
        {
            logicalMeshId = 0;
            failureReason = null;
            if (lodMeshes is null)
            {
                failureReason = "LOD mesh set is null";
                return false;
            }

            using (_lock.EnterScope())
            {
                logicalMeshId = Interlocked.Increment(ref _nextLogicalMeshID);
                LogicalMeshState state = new(logicalMeshId, 0u);
                _logicalMeshStates[logicalMeshId] = state;
                if (!TryPopulateLogicalMeshState(state, lodMeshes, "LogicalMesh", out failureReason))
                {
                    _logicalMeshStates.Remove(logicalMeshId);
                    logicalMeshId = 0;
                    return false;
                }

                FlushMeshDataDirtyRange();
                return true;
            }
        }

        public bool RequestLODLoad(uint logicalMeshId, int lodLevel, out string? failureReason)
        {
            failureReason = null;
            using (_lock.EnterScope())
            {
                if (!TryGetLogicalMeshState(logicalMeshId, lodLevel, out LogicalMeshState? state, out failureReason))
                    return false;

                XRMesh? mesh = state!.Meshes[lodLevel];
                if (mesh is null)
                {
                    failureReason = $"logical mesh {logicalMeshId} has no mesh registered for LOD {lodLevel}";
                    return false;
                }

                if (state.MeshIds[lodLevel] != 0)
                    return true;

                GetOrCreateMeshID(mesh, out uint meshId);
                string lodLabel = state.LODCount <= 1
                    ? state.DebugLabel
                    : $"{state.DebugLabel} LOD{lodLevel}";

                if (!EnsureSubmeshInAtlas(mesh, meshId, lodLabel, out failureReason))
                    return false;

                state.MeshIds[lodLevel] = meshId;
                if (state.ReferenceCount > 0)
                    IncrementAtlasMeshRefCount(meshId, state.ReferenceCount, "RequestLODLoad");

                UpdateLogicalMeshTableEntry(state);
                FlushMeshDataDirtyRange();
                return true;
            }
        }

        public bool ReleaseLOD(uint logicalMeshId, int lodLevel, out string? failureReason)
        {
            failureReason = null;
            using (_lock.EnterScope())
            {
                if (!TryGetLogicalMeshState(logicalMeshId, lodLevel, out LogicalMeshState? state, out failureReason))
                    return false;

                if (lodLevel == 0)
                {
                    failureReason = "LOD0 must remain resident as the fallback mesh";
                    return false;
                }

                uint meshId = state!.MeshIds[lodLevel];
                if (meshId == 0)
                    return true;

                if (state.ReferenceCount > 0)
                    DecrementAtlasMeshRefCount(meshId, "ReleaseLOD", state.ReferenceCount);

                state.MeshIds[lodLevel] = 0;
                UpdateLogicalMeshTableEntry(state);
                FlushMeshDataDirtyRange();
                return true;
            }
        }

        public List<(uint logicalMeshId, uint lodMask)> DrainLODRequests()
        {
            using (_lock.EnterScope())
            {
                List<(uint logicalMeshId, uint lodMask)> requests = [];
                XRDataBuffer buffer = LODRequestBuffer;
                uint entryCount = Math.Min(buffer.ElementCount, _nextLogicalMeshID + 1);
                if (entryCount <= 1)
                    return requests;

                bool mappedTemporarily = false;
                bool usedMappedAccess = false;
                bool modified = false;

                try
                {
                    if (!buffer.IsMapped)
                    {
                        buffer.MapBufferData();
                        if (buffer.IsMapped)
                        {
                            mappedTemporarily = true;
                            RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
                        }
                    }

                    IntPtr mappedAddress = IntPtr.Zero;
                    if (buffer.IsMapped)
                    {
                        AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer);
                        mappedAddress = buffer.GetMappedAddresses().FirstOrDefault();
                    }

                    if (mappedAddress != IntPtr.Zero)
                    {
                        usedMappedAccess = true;
                        unsafe
                        {
                            uint* ptr = (uint*)(void*)mappedAddress;
                            for (uint logicalMeshId = 1; logicalMeshId < entryCount; logicalMeshId++)
                            {
                                uint lodMask = ptr[logicalMeshId];
                                if (lodMask == 0)
                                    continue;

                                RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(sizeof(uint));
                                requests.Add((logicalMeshId, lodMask));
                                ptr[logicalMeshId] = 0u;
                                modified = true;
                            }
                        }
                    }
                    else
                    {
                        for (uint logicalMeshId = 1; logicalMeshId < entryCount; logicalMeshId++)
                        {
                            uint lodMask = buffer.GetDataRawAtIndex<uint>(logicalMeshId);
                            if (lodMask == 0)
                                continue;

                            requests.Add((logicalMeshId, lodMask));
                            buffer.SetDataRawAtIndex(logicalMeshId, 0u);
                            modified = true;
                        }
                    }
                }
                finally
                {
                    if (usedMappedAccess && modified)
                        AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.Command);
                    else if (modified)
                    {
                        buffer.PushSubData();
                        AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.Command);
                    }

                    if (mappedTemporarily)
                        buffer.UnmapBufferData();
                }

                return requests;
            }
        }

        /// <summary>
        /// Attempts to resolve the original mesh render command for a GPU command index.
        /// </summary>
        public bool TryGetSourceCommand(uint commandIndex, out IRenderCommandMesh? command)
        {
            using (_lock.EnterScope())
            {
                if (_commandIndexLookup.TryGetValue(commandIndex, out var entry))
                {
                    command = entry.command;
                    return true;
                }
            }

            command = null;
            return false;
        }

    }
}
