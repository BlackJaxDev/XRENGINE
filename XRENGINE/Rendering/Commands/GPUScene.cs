// =====================================================================================
// GPUScene.cs - GPU-Resident Scene Data Management
// =====================================================================================
// 
// PURPOSE:
// This class manages all GPU-resident scene data for indirect rendering, including:
//   - Render commands converted to a GPU-friendly format (GPUIndirectRenderCommand)
//   - A unified mesh atlas containing all vertex/index data for bindless rendering
//   - Material and mesh ID mappings for GPU lookups
//   - Optional internal BVH for GPU-based culling
//
// THREADING MODEL:
// GPUScene uses double-buffering to safely coordinate between two threads:
//
//   1. UPDATE/COLLECT THREAD: Writes to "Updating" buffers
//      - Add() and Remove() modify _updatingCommandsBuffer and _updatingCommandCount
//      - These operations occur during the Update or PreCollectVisible phases
//
//   2. RENDER THREAD: Reads from "AllLoaded" buffers  
//      - Rendering reads from _allLoadedCommandsBuffer and _totalCommandCount
//      - These are stable snapshots that don't change during rendering
//
// BUFFER SWAP SEQUENCE:
//   PreCollectVisible (sequential) -> CollectVisible (parallel) -> SwapBuffers -> Render
//
//   During SwapBuffers(), the updating buffer contents are copied to the render buffer,
//   making the latest scene state visible to the render thread safely.
//
// KEY COMPONENTS:
//   - Command Buffers: Double-buffered GPUIndirectRenderCommand storage
//   - Mesh Atlas: Unified vertex/index data for all scene meshes
//   - MeshData Buffer: Per-mesh metadata (index offsets, vertex offsets)
//   - ID Maps: Material and mesh ID assignment for GPU lookups
//   - BVH Provider: Optional internal BVH for GPU-based frustum/occlusion culling
//
// =====================================================================================

using Extensions;
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
    public enum EAtlasTier : uint
    {
        Static = 0,
        Dynamic = 1,
        Streaming = 2,
    }

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
    /// <item><description>Command Buffer: Array of <see cref="GPUIndirectRenderCommand"/> structures (48 floats each)</description></item>
    /// <item><description>Mesh Atlas: Unified vertex attributes (positions, normals, tangents, UVs) and index data</description></item>
    /// <item><description>MeshData Buffer: Per-mesh metadata mapping mesh IDs to atlas offsets</description></item>
    /// </list>
    /// </remarks>
    public class GPUScene : XRBase, IGpuBvhProvider
    {
        #region Bounds Helpers

        private static float ComputeMaxAxisScale(in Matrix4x4 m)
        {
            // System.Numerics uses basis columns for Vector3.Transform:
            // x' = x*M11 + y*M21 + z*M31 + M41, etc.
            Vector3 xAxis = new(m.M11, m.M21, m.M31);
            Vector3 yAxis = new(m.M12, m.M22, m.M32);
            Vector3 zAxis = new(m.M13, m.M23, m.M33);

            float sx = xAxis.Length();
            float sy = yAxis.Length();
            float sz = zAxis.Length();

            float s = MathF.Max(sx, MathF.Max(sy, sz));
            if (float.IsNaN(s) || float.IsInfinity(s) || s < 0f)
                return 0f;

            return s;
        }

        private static float ComputeMaxAxisScale(in AffineMatrix4x3 m)
        {
            Vector3 xAxis = new(m.M11, m.M21, m.M31);
            Vector3 yAxis = new(m.M12, m.M22, m.M32);
            Vector3 zAxis = new(m.M13, m.M23, m.M33);

            float sx = xAxis.Length();
            float sy = yAxis.Length();
            float sz = zAxis.Length();

            float s = MathF.Max(sx, MathF.Max(sy, sz));
            if (float.IsNaN(s) || float.IsInfinity(s) || s < 0f)
                return 0f;

            return s;
        }

        private static void SetWorldSpaceBoundingSphere(ref GPUIndirectRenderCommand cmd, in AABB localBounds, in Matrix4x4 modelMatrix)
        {
            Vector3 localCenter = localBounds.Center;
            float localRadius = localBounds.HalfExtents.Length();

            Vector3 worldCenter;
            float maxScale;
            if (AffineMatrix4x3.TryFromMatrix4x4(modelMatrix, out AffineMatrix4x3 affineModelMatrix))
            {
                worldCenter = affineModelMatrix.TransformPosition(localCenter);
                maxScale = ComputeMaxAxisScale(affineModelMatrix);
            }
            else
            {
                worldCenter = Vector3.Transform(localCenter, modelMatrix);
                maxScale = ComputeMaxAxisScale(modelMatrix);
            }
            float worldRadius = localRadius * maxScale;

            if (float.IsNaN(worldRadius) || float.IsInfinity(worldRadius) || worldRadius < 0f)
                worldRadius = 0f;

            cmd.SetBoundingSphere(worldCenter, worldRadius);
        }

        #endregion

        #region Mesh Atlas State

        // -------------------------------------------------------------------------
        // Mesh Atlas: Consolidated vertex + index data for all meshes referenced by commands.
        // Meshes are linearly appended; future optimization could bin by vertex format/material.
        // -------------------------------------------------------------------------

        public const uint MeshDataFlagAtlasTierMask = 0x3u;
        public const int MaxLogicalMeshLodCount = 4;
        public const int LODTableEntryFloatCount = 12;
        private const uint MinLodTableEntries = 16;
        private const uint MinLodRequestEntries = 16;

        [StructLayout(LayoutKind.Sequential)]
        public struct LODTableEntry
        {
            public uint LODCount;
            public uint LOD0_MeshDataID;
            public uint LOD1_MeshDataID;
            public uint LOD2_MeshDataID;
            public uint LOD3_MeshDataID;
            public float LOD0_MaxDistance;
            public float LOD1_MaxDistance;
            public float LOD2_MaxDistance;
            public float LOD3_MaxDistance;
            public float Padding0;
            public float Padding1;
            public float Padding2;

            public readonly uint GetMeshDataId(int lodLevel)
                => lodLevel switch
                {
                    0 => LOD0_MeshDataID,
                    1 => LOD1_MeshDataID,
                    2 => LOD2_MeshDataID,
                    3 => LOD3_MeshDataID,
                    _ => 0,
                };

            public readonly float GetMaxDistance(int lodLevel)
                => lodLevel switch
                {
                    0 => LOD0_MaxDistance,
                    1 => LOD1_MaxDistance,
                    2 => LOD2_MaxDistance,
                    3 => LOD3_MaxDistance,
                    _ => float.MaxValue,
                };
        }

        private readonly struct AtlasAllocation(int firstVertex, int firstIndex, int vertexCount, int indexCount, int reservedVertexCount, int reservedIndexCount)
        {
            public int FirstVertex { get; } = firstVertex;
            public int FirstIndex { get; } = firstIndex;
            public int VertexCount { get; } = vertexCount;
            public int IndexCount { get; } = indexCount;
            public int ReservedVertexCount { get; } = reservedVertexCount;
            public int ReservedIndexCount { get; } = reservedIndexCount;
        }

        private sealed class LogicalMeshState(uint logicalMeshId, uint submeshIndex)
        {
            public uint LogicalMeshId { get; } = logicalMeshId;
            public uint SubmeshIndex { get; } = submeshIndex;
            public string DebugLabel { get; set; } = $"LogicalMesh_{logicalMeshId}";
            public int ReferenceCount;
            public readonly uint[] MeshIds = new uint[MaxLogicalMeshLodCount];
            public readonly XRMesh?[] Meshes = new XRMesh?[MaxLogicalMeshLodCount];
            public readonly float[] MaxDistances = new float[MaxLogicalMeshLodCount];
            public uint LODCount;

            public LODTableEntry ToEntry()
                => new()
                {
                    LODCount = LODCount,
                    LOD0_MeshDataID = MeshIds[0],
                    LOD1_MeshDataID = MeshIds[1],
                    LOD2_MeshDataID = MeshIds[2],
                    LOD3_MeshDataID = MeshIds[3],
                    LOD0_MaxDistance = MaxDistances[0],
                    LOD1_MaxDistance = MaxDistances[1],
                    LOD2_MaxDistance = MaxDistances[2],
                    LOD3_MaxDistance = MaxDistances[3],
                };
        }

        public readonly struct StreamingWritePointers(
            VoidPtr positions,
            VoidPtr normals,
            VoidPtr tangents,
            VoidPtr uv0,
            VoidPtr indices,
            int maxVertexCount,
            int maxIndexCount)
        {
            public VoidPtr Positions { get; } = positions;
            public VoidPtr Normals { get; } = normals;
            public VoidPtr Tangents { get; } = tangents;
            public VoidPtr UV0 { get; } = uv0;
            public VoidPtr Indices { get; } = indices;
            public int MaxVertexCount { get; } = maxVertexCount;
            public int MaxIndexCount { get; } = maxIndexCount;
        }

        private sealed class AtlasTierState(EAtlasTier tier)
        {
            public EAtlasTier Tier { get; } = tier;
            public XRDataBuffer? Positions;
            public XRDataBuffer? Normals;
            public XRDataBuffer? Tangents;
            public XRDataBuffer? UV0;
            public XRDataBuffer? Indices;
            public bool Dirty;
            public int VertexCount;
            public int IndexCount;
            public uint Version;
            public IndexSize IndexElementSize = IndexSize.FourBytes;
            public readonly Dictionary<XRMesh, AtlasAllocation> MeshOffsets = [];
            public readonly List<IndexTriangle> IndirectFaceIndices = [];
        }

        private readonly AtlasTierState _staticAtlas = new(EAtlasTier.Static);
        private readonly AtlasTierState _dynamicAtlas = new(EAtlasTier.Dynamic);
        private readonly AtlasTierState[] _streamingAtlases =
        [
            new AtlasTierState(EAtlasTier.Streaming),
            new AtlasTierState(EAtlasTier.Streaming),
            new AtlasTierState(EAtlasTier.Streaming),
        ];

        private readonly Dictionary<XRMesh, EAtlasTier> _activeAtlasTiers = [];
        private readonly Dictionary<XRMesh, (int maxVertexCount, int maxIndexCount)> _streamingReservations = [];
        private int _streamingWriteSlot;
        private int _streamingRenderSlot;

        /// <summary>Atlas buffer containing position vec3 data for all meshes.</summary>
        private XRDataBuffer? _atlasPositions;

        /// <summary>Atlas buffer containing normal vec3 data for all meshes.</summary>
        private XRDataBuffer? _atlasNormals;

        /// <summary>Atlas buffer containing tangent vec4 data for all meshes.</summary>
        private XRDataBuffer? _atlasTangents;

        /// <summary>Atlas buffer containing UV0 vec2 data for all meshes.</summary>
        private XRDataBuffer? _atlasUV0;

        /// <summary>Atlas buffer containing element indices.</summary>
        private XRDataBuffer? _atlasIndices;

        /// <summary>Flag indicating atlas needs rebuild after adds/removes.</summary>
        private bool _atlasDirty = false;

        /// <summary>Running vertex count for atlas packing.</summary>
        private int _atlasVertexCount = 0;

        /// <summary>Running index count for atlas packing.</summary>
        private int _atlasIndexCount = 0;

        /// <summary>Maps each mesh to its offsets within the atlas (firstVertex, firstIndex, indexCount).</summary>
        private readonly Dictionary<XRMesh, (int firstVertex, int firstIndex, int indexCount)> _atlasMeshOffsets = [];

        /// <summary>
        /// Tracks how many active GPU commands reference each mesh resident in the atlas.
        /// Atlas geometry is only removed when this reaches zero.
        /// </summary>
        private readonly Dictionary<XRMesh, int> _atlasMeshRefCounts = [];

        /// <summary>Version number incremented on each atlas rebuild for change detection.</summary>
        private uint _atlasVersion = 0;

        /// <summary>Current index element type (u8/u16/u32) used in atlas index buffer.</summary>
        private IndexSize _atlasIndexElementSize = IndexSize.FourBytes;

        /// <summary>List of triangle indices for indirect rendering.</summary>
        private readonly List<IndexTriangle> _indirectFaceIndices = [];

        #endregion

        #region Mesh Atlas Properties

        /// <summary>
        /// Raised after the atlas GPU buffers have been rebuilt (EBO updated).
        /// Listeners should re-bind their VAO index buffers.
        /// </summary>
        public event Action<GPUScene>? AtlasRebuilt;

        /// <summary>Gets the list of triangle indices for indirect rendering.</summary>
        public List<IndexTriangle> IndirectFaceIndices => _indirectFaceIndices;

        public IReadOnlyList<IndexTriangle> GetIndirectFaceIndices(EAtlasTier tier)
            => GetTierState(tier).IndirectFaceIndices;

        /// <summary>Gets the current total vertex count in the atlas.</summary>
        public int AtlasVertexCount => _atlasVertexCount;

        public int GetAtlasVertexCount(EAtlasTier tier)
            => GetTierState(tier).VertexCount;

        /// <summary>Gets the current total index count in the atlas.</summary>
        public int AtlasIndexCount => _atlasIndexCount;

        public int GetAtlasIndexCount(EAtlasTier tier)
            => GetTierState(tier).IndexCount;

        /// <summary>Gets the version number incremented on each atlas rebuild. Use for change detection.</summary>
        public uint AtlasVersion => _atlasVersion;

        public uint GetAtlasVersion(EAtlasTier tier)
            => GetTierState(tier).Version;

        /// <summary>Gets the index element size used in the atlas index buffer (u8, u16, or u32).</summary>
        public IndexSize AtlasIndexElementSize => _atlasIndexElementSize;

        public IndexSize GetAtlasIndexElementSize(EAtlasTier tier)
            => GetTierState(tier).IndexElementSize;

        /// <summary>Gets the atlas buffer containing position data.</summary>
        public XRDataBuffer? AtlasPositions => _atlasPositions;

        public XRDataBuffer? GetAtlasPositions(EAtlasTier tier)
            => GetTierState(tier).Positions;

        /// <summary>Gets the atlas buffer containing normal data.</summary>
        public XRDataBuffer? AtlasNormals => _atlasNormals;

        public XRDataBuffer? GetAtlasNormals(EAtlasTier tier)
            => GetTierState(tier).Normals;

        /// <summary>Gets the atlas buffer containing tangent data.</summary>
        public XRDataBuffer? AtlasTangents => _atlasTangents;

        public XRDataBuffer? GetAtlasTangents(EAtlasTier tier)
            => GetTierState(tier).Tangents;

        /// <summary>Gets the atlas buffer containing UV0 data.</summary>
        public XRDataBuffer? AtlasUV0 => _atlasUV0;

        public XRDataBuffer? GetAtlasUV0(EAtlasTier tier)
            => GetTierState(tier).UV0;

        /// <summary>Gets the atlas buffer containing index data.</summary>
        public XRDataBuffer? AtlasIndices => _atlasIndices;

        public XRDataBuffer? GetAtlasIndices(EAtlasTier tier)
            => GetTierState(tier).Indices;

        public EAtlasTier GetActiveAtlasTier(uint meshID)
        {
            if (meshID != 0 && _idToMesh.TryGetValue(meshID, out var mesh) && mesh is not null && _activeAtlasTiers.TryGetValue(mesh, out var tier))
                return tier;

            return EAtlasTier.Dynamic;
        }

        private AtlasTierState GetTierState(EAtlasTier tier)
            => tier switch
            {
                EAtlasTier.Static => _staticAtlas,
                EAtlasTier.Dynamic => _dynamicAtlas,
                EAtlasTier.Streaming => _streamingAtlases[_streamingRenderSlot],
                _ => _dynamicAtlas,
            };

        private AtlasTierState GetStreamingWriteTierState()
            => _streamingAtlases[_streamingWriteSlot];

        private static uint ComposeMeshDataFlags(EAtlasTier tier)
            => (uint)tier & MeshDataFlagAtlasTierMask;

        #endregion

        #region Material and Mesh ID Mapping

        // -------------------------------------------------------------------------
        // Material/Mesh ID Maps: Concurrent dictionaries for thread-safe ID assignment.
        // IDs start at 1 (0 is reserved/invalid).
        // -------------------------------------------------------------------------

        /// <summary>Maps XRMaterial instances to unique GPU IDs.</summary>
        private readonly ConcurrentDictionary<XRMaterial, uint> _materialIDMap = new();

        /// <summary>Reverse mapping from material ID to XRMaterial instance.</summary>
        private readonly ConcurrentDictionary<uint, XRMaterial> _idToMaterial = new();

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

        #endregion

        #region Debug Logging

        // -------------------------------------------------------------------------
        // Debug Logging: Budget-limited logging to prevent log spam during heavy operations.
        // -------------------------------------------------------------------------

        /// <summary>Remaining log entries for material assignment debug output.</summary>
        private int _materialDebugLogBudget = 16;

        /// <summary>Remaining log entries for command build debug output.</summary>
        private int _commandBuildLogBudget = 12;

        /// <summary>Remaining log entries for command roundtrip verification output.</summary>
        private int _commandRoundtripLogBudget = 8;

        /// <summary>Remaining log entries for command roundtrip mismatch warnings.</summary>
        private int _commandRoundtripMismatchLogBudget = 4;

        /// <summary>Remaining log entries for command update warnings/errors.</summary>
        private int _commandUpdateErrorLogBudget = 24;

        /// <summary>Checks if GPU scene logging is enabled in settings.</summary>
        private static bool IsGpuSceneLoggingEnabled()
            => Engine.EffectiveSettings.EnableGpuIndirectDebugLogging;

        /// <summary>Logs a message if GPU scene logging is enabled.</summary>
        private static void SceneLog(string message, params object[] args)
        {
            if (!IsGpuSceneLoggingEnabled())
                return;

            Debug.Out(message, args);
        }

        /// <summary>Logs a formatted message if GPU scene logging is enabled.</summary>
        private static void SceneLog(FormattableString message)
        {
            if (!IsGpuSceneLoggingEnabled())
                return;

            Debug.Out(message.ToString());
        }

        #endregion

        #region BVH Configuration

        // -------------------------------------------------------------------------
        // BVH Configuration: Settings for GPU-accelerated hierarchical culling.
        // -------------------------------------------------------------------------

        /// <summary>Whether to use GPU BVH traversal for culling.</summary>
        private bool _useGpuBvh = Engine.EffectiveSettings.UseGpuBvh;

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
                Debug.Out($"[GPUScene] Active traversal path set to {path}.");
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

        #endregion

        #region Material ID API

        /// <summary>
        /// Exposes a read-only view of the current material ID map (ID -> XRMaterial).
        /// </summary>
        public IReadOnlyDictionary<uint, XRMaterial> MaterialMap => _idToMaterial;

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

        public bool RegisterLogicalMeshLODs(IEnumerable<(XRMesh mesh, float maxDistance)> lodMeshes, out uint logicalMeshId, out string? failureReason)
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
                            Engine.Rendering.Stats.RecordGpuBufferMapped();
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

                                Engine.Rendering.Stats.RecordGpuReadbackBytes(sizeof(uint));
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

        #endregion

        #region Atlas Management

        private void SyncLegacyDynamicAtlasState()
        {
            _atlasPositions = _dynamicAtlas.Positions;
            _atlasNormals = _dynamicAtlas.Normals;
            _atlasTangents = _dynamicAtlas.Tangents;
            _atlasUV0 = _dynamicAtlas.UV0;
            _atlasIndices = _dynamicAtlas.Indices;
            _atlasDirty = _dynamicAtlas.Dirty;
            _atlasVertexCount = _dynamicAtlas.VertexCount;
            _atlasIndexCount = _dynamicAtlas.IndexCount;
            _atlasIndexElementSize = _dynamicAtlas.IndexElementSize;
            _indirectFaceIndices.Clear();
            _indirectFaceIndices.AddRange(_dynamicAtlas.IndirectFaceIndices);
            _atlasMeshOffsets.Clear();
            foreach (var kvp in _dynamicAtlas.MeshOffsets)
                _atlasMeshOffsets[kvp.Key] = (kvp.Value.FirstVertex, kvp.Value.FirstIndex, kvp.Value.IndexCount);
        }

        private static string GetTierLabel(EAtlasTier tier)
            => tier switch
            {
                EAtlasTier.Static => "Static",
                EAtlasTier.Dynamic => "Dynamic",
                EAtlasTier.Streaming => "Streaming",
                _ => "Unknown",
            };

        private static EBufferUsage GetTierUsage(EAtlasTier tier)
            => tier switch
            {
                EAtlasTier.Static => EBufferUsage.StaticDraw,
                EAtlasTier.Dynamic => EBufferUsage.DynamicDraw,
                EAtlasTier.Streaming => EBufferUsage.StreamDraw,
                _ => EBufferUsage.DynamicDraw,
            };

        private static void ConfigureStreamingBuffer(XRDataBuffer buffer)
        {
            buffer.StorageFlags |= EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Write | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent;
            buffer.RangeFlags |= EBufferMapRangeFlags.Write | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent;
            buffer.ShouldMap = true;
            buffer.Resizable = true;
        }

        private void EnsureTierBuffers(AtlasTierState state)
        {
            string tierLabel = GetTierLabel(state.Tier);
            EBufferUsage usage = GetTierUsage(state.Tier);

            state.Positions ??= new XRDataBuffer(ECommonBufferType.Position.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 3, false, false)
            {
                Name = $"MeshAtlas_{tierLabel}_Positions",
                Usage = usage,
                DisposeOnPush = false,
                BindingIndexOverride = 0,
            };
            state.Normals ??= new XRDataBuffer(ECommonBufferType.Normal.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 3, false, false)
            {
                Name = $"MeshAtlas_{tierLabel}_Normals",
                Usage = usage,
                DisposeOnPush = false,
                BindingIndexOverride = 1,
            };
            state.Tangents ??= new XRDataBuffer(ECommonBufferType.Tangent.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 4, false, false)
            {
                Name = $"MeshAtlas_{tierLabel}_Tangents",
                Usage = usage,
                DisposeOnPush = false,
                BindingIndexOverride = 2,
            };
            state.UV0 ??= new XRDataBuffer($"{ECommonBufferType.TexCoord}0", EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 2, false, false)
            {
                Name = $"MeshAtlas_{tierLabel}_UV0",
                Usage = usage,
                DisposeOnPush = false,
                BindingIndexOverride = 3,
            };
            state.Indices ??= new XRDataBuffer($"MeshAtlas_{tierLabel}_Indices", EBufferTarget.ElementArrayBuffer, 0, EComponentType.UInt, 1, false, true)
            {
                Usage = usage,
                DisposeOnPush = false,
                PadEndingToVec4 = false,
            };

            if (state.Tier == EAtlasTier.Streaming)
            {
                ConfigureStreamingBuffer(state.Positions);
                ConfigureStreamingBuffer(state.Normals);
                ConfigureStreamingBuffer(state.Tangents);
                ConfigureStreamingBuffer(state.UV0);
                ConfigureStreamingBuffer(state.Indices);
            }
        }

        /// <summary>
        /// Marks the atlas as dirty so it will be rebuilt before next render if needed.
        /// </summary>
        private void MarkAtlasDirty()
            => MarkAtlasDirty(EAtlasTier.Dynamic);

        private void MarkAtlasDirty(EAtlasTier tier)
        {
            AtlasTierState state = GetTierState(tier);
            state.Dirty = true;
            if (tier == EAtlasTier.Dynamic)
                _atlasDirty = true;
        }

        /// <summary>
        /// Ensures atlas buffers exist with minimal allocation on first use.
        /// Creates position, normal, tangent, UV0, and index buffers.
        /// </summary>
        public void EnsureAtlasBuffers()
            => EnsureAtlasBuffers(EAtlasTier.Dynamic);

        public void EnsureAtlasBuffers(EAtlasTier tier)
        {
            if (tier == EAtlasTier.Streaming)
            {
                foreach (AtlasTierState streamingState in _streamingAtlases)
                    EnsureTierBuffers(streamingState);
            }
            else
            {
                EnsureTierBuffers(GetTierState(tier));
            }

            SyncLegacyDynamicAtlasState();
        }

        private static XRDataBuffer MakeLodTableBuffer()
        {
            var buffer = new XRDataBuffer(
                "LodTableBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinLodTableEntries,
                EComponentType.Float,
                LODTableEntryFloatCount,
                false,
                false)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true
            };
            return buffer;
        }

        private static XRDataBuffer MakeLodRequestBuffer()
        {
            var buffer = new XRDataBuffer(
                "LodRequestBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinLodRequestEntries,
                EComponentType.UInt,
                1,
                false,
                false)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true
            };

            for (uint index = 0; index < buffer.ElementCount; index++)
                buffer.SetDataRawAtIndex(index, 0u);

            return buffer;
        }

        private void EnsureLodTableCapacity(uint requiredEntries)
        {
            XRDataBuffer buffer = LODTableBuffer;
            if (requiredEntries <= buffer.ElementCount)
                return;

            uint newCapacity = XRMath.NextPowerOfTwo(requiredEntries).ClampMin(MinLodTableEntries);
            buffer.Resize(newCapacity);
        }

        private void EnsureLodRequestCapacity(uint requiredEntries)
        {
            XRDataBuffer buffer = LODRequestBuffer;
            if (requiredEntries <= buffer.ElementCount)
                return;

            uint oldCount = buffer.ElementCount;
            uint newCapacity = XRMath.NextPowerOfTwo(requiredEntries).ClampMin(MinLodRequestEntries);
            buffer.Resize(newCapacity);
            for (uint index = oldCount; index < newCapacity; index++)
                buffer.SetDataRawAtIndex(index, 0u);
            buffer.PushSubData((int)(oldCount * sizeof(uint)), (newCapacity - oldCount) * sizeof(uint));
        }

        private void UpdateLogicalMeshTableEntry(LogicalMeshState state)
        {
            EnsureLodTableCapacity(state.LogicalMeshId + 1);
            EnsureLodRequestCapacity(state.LogicalMeshId + 1);
            LODTableBuffer.SetDataRawAtIndex(state.LogicalMeshId, state.ToEntry());
            LODTableBuffer.PushSubData();
        }

        private bool TryGetLogicalMeshState(uint logicalMeshId, int lodLevel, out LogicalMeshState? state, out string? failureReason)
        {
            state = null;
            failureReason = null;

            if (logicalMeshId == 0 || !_logicalMeshStates.TryGetValue(logicalMeshId, out state))
            {
                failureReason = $"logical mesh {logicalMeshId} is not registered";
                return false;
            }

            if (lodLevel < 0 || lodLevel >= MaxLogicalMeshLodCount || lodLevel >= state.LODCount)
            {
                failureReason = $"LOD {lodLevel} is outside the registered range for logical mesh {logicalMeshId}";
                state = null;
                return false;
            }

            return true;
        }

        private uint GetOrCreateRenderableLogicalMeshId(RenderableMesh renderable, uint submeshIndex)
        {
            if (!_renderableLogicalMeshIdMap.TryGetValue(renderable, out Dictionary<uint, uint>? submeshMap))
            {
                submeshMap = [];
                _renderableLogicalMeshIdMap[renderable] = submeshMap;
            }

            if (!submeshMap.TryGetValue(submeshIndex, out uint logicalMeshId))
            {
                logicalMeshId = Interlocked.Increment(ref _nextLogicalMeshID);
                submeshMap[submeshIndex] = logicalMeshId;
                _logicalMeshStates[logicalMeshId] = new LogicalMeshState(logicalMeshId, submeshIndex);
            }

            return logicalMeshId;
        }

        private uint GetOrCreateStandaloneLogicalMeshId(XRMesh mesh)
        {
            if (!_standaloneLogicalMeshIdMap.TryGetValue(mesh, out uint logicalMeshId))
            {
                logicalMeshId = Interlocked.Increment(ref _nextLogicalMeshID);
                _standaloneLogicalMeshIdMap[mesh] = logicalMeshId;
                _logicalMeshStates[logicalMeshId] = new LogicalMeshState(logicalMeshId, 0u);
            }

            return logicalMeshId;
        }

        private static List<(XRMesh mesh, float maxDistance)> BuildFallbackLodSet(XRMesh mesh)
            => [(mesh, float.MaxValue)];

        private static List<(XRMesh mesh, float maxDistance)> CollectRenderableLodSet(RenderableMesh renderable, uint submeshIndex, XRMesh fallbackMesh)
        {
            List<(XRMesh mesh, float maxDistance)> lodMeshes = [];
            foreach (RenderableMesh.RenderableLOD lod in renderable.GetLodSnapshot())
            {
                var submeshes = lod.Renderer.GetMeshes();
                if (submeshIndex >= (uint)submeshes.Length)
                    continue;

                XRMesh? lodMesh = submeshes[submeshIndex].mesh;
                if (lodMesh is null)
                    continue;

                lodMeshes.Add((lodMesh, lod.MaxVisibleDistance));
                if (lodMeshes.Count >= MaxLogicalMeshLodCount)
                    break;
            }

            if (lodMeshes.Count == 0)
                lodMeshes.Add((fallbackMesh, float.MaxValue));

            int lastIndex = lodMeshes.Count - 1;
            lodMeshes[lastIndex] = (lodMeshes[lastIndex].mesh, float.MaxValue);
            return lodMeshes;
        }

        private bool TryPopulateLogicalMeshState(LogicalMeshState state, IEnumerable<(XRMesh mesh, float maxDistance)> lodMeshes, string meshLabel, out string? failureReason)
        {
            failureReason = null;
            state.DebugLabel = meshLabel;
            List<(XRMesh mesh, float maxDistance)> levels = [];
            foreach ((XRMesh mesh, float maxDistance) in lodMeshes)
            {
                if (mesh is null)
                    continue;

                levels.Add((mesh, maxDistance));
                if (levels.Count >= MaxLogicalMeshLodCount)
                    break;
            }

            if (levels.Count == 0)
            {
                failureReason = "no valid LOD meshes were provided";
                return false;
            }

            int lastIndex = levels.Count - 1;
            levels[lastIndex] = (levels[lastIndex].mesh, float.MaxValue);

            uint[] previousMeshIds = new uint[MaxLogicalMeshLodCount];
            Array.Copy(state.MeshIds, previousMeshIds, state.MeshIds.Length);
            int previousRefCount = state.ReferenceCount;

            uint[] newMeshIds = new uint[MaxLogicalMeshLodCount];
            XRMesh?[] newMeshes = new XRMesh?[MaxLogicalMeshLodCount];
            float[] newDistances = new float[MaxLogicalMeshLodCount];

            for (int i = 0; i < levels.Count; i++)
            {
                XRMesh mesh = levels[i].mesh;
                GetOrCreateMeshID(mesh, out uint meshId);
                string lodLabel = levels.Count == 1 ? meshLabel : $"{meshLabel} LOD{i}";
                if (!EnsureSubmeshInAtlas(mesh, meshId, lodLabel, out failureReason))
                    return false;

                newMeshIds[i] = meshId;
                newMeshes[i] = mesh;
                newDistances[i] = i == lastIndex ? float.MaxValue : MathF.Max(0.0f, levels[i].maxDistance);
            }

            if (previousRefCount > 0)
            {
                HashSet<uint> previous = [];
                HashSet<uint> current = [];

                foreach (uint meshId in previousMeshIds)
                    if (meshId != 0)
                        previous.Add(meshId);

                foreach (uint meshId in newMeshIds)
                    if (meshId != 0)
                        current.Add(meshId);

                foreach (uint meshId in current)
                {
                    if (!previous.Contains(meshId))
                        IncrementAtlasMeshRefCount(meshId, previousRefCount, "TryPopulateLogicalMeshState");
                }

                foreach (uint meshId in previous)
                {
                    if (!current.Contains(meshId))
                        DecrementAtlasMeshRefCount(meshId, "TryPopulateLogicalMeshState", previousRefCount);
                }
            }

            Array.Clear(state.MeshIds, 0, state.MeshIds.Length);
            Array.Clear(state.Meshes, 0, state.Meshes.Length);
            Array.Clear(state.MaxDistances, 0, state.MaxDistances.Length);
            Array.Copy(newMeshIds, state.MeshIds, newMeshIds.Length);
            Array.Copy(newMeshes, state.Meshes, newMeshes.Length);
            Array.Copy(newDistances, state.MaxDistances, newDistances.Length);
            state.LODCount = (uint)levels.Count;

            UpdateLogicalMeshTableEntry(state);
            return true;
        }

        private bool ResolveLogicalMeshRegistration(RenderInfo renderInfo, XRMesh mesh, uint submeshIndex, string meshLabel, out uint meshId, out uint logicalMeshId, out uint lodCount, out string? failureReason)
        {
            meshId = 0;
            logicalMeshId = 0;
            lodCount = 0;
            failureReason = null;

            List<(XRMesh mesh, float maxDistance)> lodMeshes;
            LogicalMeshState state;

            if (renderInfo.Owner is RenderableMesh renderable)
            {
                lodMeshes = CollectRenderableLodSet(renderable, submeshIndex, mesh);
                logicalMeshId = GetOrCreateRenderableLogicalMeshId(renderable, submeshIndex);
                state = _logicalMeshStates[logicalMeshId];
            }
            else
            {
                lodMeshes = BuildFallbackLodSet(mesh);
                logicalMeshId = GetOrCreateStandaloneLogicalMeshId(mesh);
                state = _logicalMeshStates[logicalMeshId];
            }

            if (!TryPopulateLogicalMeshState(state, lodMeshes, meshLabel, out failureReason))
                return false;

            GetOrCreateMeshID(mesh, out meshId);
            lodCount = state.LODCount;
            return true;
        }

        private void AcquireLogicalMeshResidency(uint logicalMeshId)
        {
            if (logicalMeshId == 0 || !_logicalMeshStates.TryGetValue(logicalMeshId, out LogicalMeshState? state))
                return;

            state.ReferenceCount++;
            if (state.ReferenceCount != 1)
                return;

            HashSet<uint> uniqueMeshIds = [];
            foreach (uint meshId in state.MeshIds)
            {
                if (meshId != 0)
                    uniqueMeshIds.Add(meshId);
            }

            foreach (uint meshId in uniqueMeshIds)
                IncrementAtlasMeshRefCount(meshId, 1, "AcquireLogicalMeshResidency");
        }

        private void ReleaseLogicalMeshResidency(uint logicalMeshId, string context)
        {
            if (logicalMeshId == 0 || !_logicalMeshStates.TryGetValue(logicalMeshId, out LogicalMeshState? state) || state.ReferenceCount <= 0)
                return;

            state.ReferenceCount--;
            if (state.ReferenceCount != 0)
                return;

            HashSet<uint> uniqueMeshIds = [];
            foreach (uint meshId in state.MeshIds)
            {
                if (meshId != 0)
                    uniqueMeshIds.Add(meshId);
            }

            foreach (uint meshId in uniqueMeshIds)
                DecrementAtlasMeshRefCount(meshId, context, 1);
        }

        /// <summary>
        /// Incrementally appends mesh geometry into atlas client-side buffers.
        /// </summary>
        /// <remarks>
        /// For now we only pack index offsets (MeshDataBuffer keeps logical mapping). 
        /// Full vertex packing is a future optimization.
        /// </remarks>
        /// <param name="mesh">The mesh to append to the atlas.</param>
        /// <param name="meshLabel">Debug label for the mesh.</param>
        /// <param name="failureReason">Reason for failure if the method returns false.</param>
        /// <returns>True if the mesh was successfully appended; false otherwise.</returns>
        private bool AppendMeshToAtlas(XRMesh mesh, string meshLabel, out string? failureReason)
            => AppendMeshToAtlas(mesh, EAtlasTier.Dynamic, meshLabel, out failureReason);

        private bool AppendMeshToAtlas(XRMesh mesh, EAtlasTier tier, string meshLabel, out string? failureReason)
        {
            failureReason = null;
            AtlasTierState state = GetTierState(tier);

            //Make sure the buffers exist - positions, normals, etc
            EnsureAtlasBuffers(tier);

            if (state.MeshOffsets.ContainsKey(mesh))
                return true; // already packed

            int vertexCount = mesh.VertexCount;
            if (vertexCount <= 0)
            {
                failureReason = "contains no vertices";
                return false;
            }

            //Gather attributes
            //TODO: only do this when interleaved; direct memory copy otherwise
            Vector3[] positions = new Vector3[vertexCount];
            Vector3[] normals = new Vector3[vertexCount];
            Vector4[] tangents = new Vector4[vertexCount];
            Vector2[] uv0 = new Vector2[vertexCount];
            for (uint v = 0; v < vertexCount; v++)
            {
                positions[v] = mesh.GetPosition(v);
                normals[v] = mesh.GetNormal(v);
                var tan = mesh.GetTangentWithSign(v);
                tangents[v] = tan;
                uv0[v] = mesh.GetTexCoord(v, 0);
            }

            // Indices: expand mesh primitive lists into triangle list (only triangle topology supported here)
            int indexCountAdded = 0;
            if (mesh.Triangles is not null && mesh.Triangles.Count > 0)
            {
                foreach (IndexTriangle tri in mesh.Triangles)
                {
                    // Store indices relative to the mesh; DrawElementsIndirect will offset by baseVertex.
                    state.IndirectFaceIndices.Add(new IndexTriangle(
                        tri.Point0,
                        tri.Point1,
                        tri.Point2));
                    indexCountAdded += 3;
                }
            }
            else if (mesh.Type == EPrimitiveType.Triangles && mesh.IndexCount > 0)
            {
                int[]? indices = mesh.GetIndices(EPrimitiveType.Triangles);
                if (indices is null || indices.Length == 0)
                {
                    failureReason = "has no triangle indices";
                    return false;
                }

                if (indices.Length % 3 != 0)
                    Debug.LogWarning($"Mesh '{meshLabel}' triangle index count {indices.Length} is not divisible by 3; trailing vertices will be ignored.");

                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    state.IndirectFaceIndices.Add(new IndexTriangle(
                        indices[i],
                        indices[i + 1],
                        indices[i + 2]));
                    indexCountAdded += 3;
                }
            }
            else
            {
                failureReason = "has no index data";
                return false;
            }

            int firstVertex = state.VertexCount;
            int firstIndex = state.IndexCount;

            // Grow per-attribute buffers (power-of-two growth to reduce reallocs)
            VerifyBufferLengths(state, firstVertex + vertexCount);

            // Batched copy: direct memory move instead of per-element setters
            unsafe
            {
                // Helper local to copy an array of structs into a target XRDataBuffer at a starting element index.
                static void CopyArray<T>(XRDataBuffer? buffer, int startIndex, T[] src) where T : struct
                {
                    if (buffer is null || src.Length == 0)
                        return;

                    var spanBytes = (uint)(src.Length * Marshal.SizeOf<T>());
                    var handle = GCHandle.Alloc(src, GCHandleType.Pinned);
                    try
                    {
                        VoidPtr dst = buffer.Address + (int)(startIndex * buffer.ElementSize);
                        Memory.Move(dst, handle.AddrOfPinnedObject(), spanBytes);
                    }
                    finally
                    {
                        handle.Free();
                    }
                }

                CopyArray(state.Positions, firstVertex, positions);
                CopyArray(state.Normals, firstVertex, normals);
                CopyArray(state.Tangents, firstVertex, tangents);
                CopyArray(state.UV0, firstVertex, uv0);
            }

            state.VertexCount = firstVertex + positions.Length;
            state.IndexCount = firstIndex + indexCountAdded;
            state.MeshOffsets[mesh] = new AtlasAllocation(firstVertex, firstIndex, positions.Length, indexCountAdded, positions.Length, indexCountAdded);
            _activeAtlasTiers[mesh] = tier;
            MarkAtlasDirty(tier); // we've written client-side; PushSubData below in rebuild
            SyncLegacyDynamicAtlasState();
            return true;
        }

        private bool ReserveMeshInStreamingAtlas(XRMesh mesh, uint meshID, string meshLabel, int maxVertexCount, int maxIndexCount, out string? failureReason)
        {
            failureReason = null;
            if (maxVertexCount <= 0)
            {
                failureReason = "requires a positive streaming vertex capacity";
                return false;
            }

            maxIndexCount = Math.Max(maxIndexCount, 0);
            if ((maxIndexCount % 3) != 0)
                maxIndexCount += 3 - (maxIndexCount % 3);

            EnsureAtlasBuffers(EAtlasTier.Streaming);

            Vector3[] positions = new Vector3[mesh.VertexCount];
            Vector3[] normals = new Vector3[mesh.VertexCount];
            Vector4[] tangents = new Vector4[mesh.VertexCount];
            Vector2[] uv0 = new Vector2[mesh.VertexCount];
            for (uint v = 0; v < mesh.VertexCount; v++)
            {
                positions[v] = mesh.GetPosition(v);
                normals[v] = mesh.GetNormal(v);
                tangents[v] = mesh.GetTangentWithSign(v);
                uv0[v] = mesh.GetTexCoord(v, 0);
            }

            List<IndexTriangle> triangles = [];
            if (mesh.Triangles is not null && mesh.Triangles.Count > 0)
            {
                triangles.AddRange(mesh.Triangles);
            }
            else if (mesh.Type == EPrimitiveType.Triangles && mesh.IndexCount > 0)
            {
                int[]? indices = mesh.GetIndices(EPrimitiveType.Triangles);
                if (indices is null || indices.Length == 0)
                {
                    failureReason = "has no triangle indices";
                    return false;
                }

                for (int i = 0; i + 2 < indices.Length; i += 3)
                    triangles.Add(new IndexTriangle(indices[i], indices[i + 1], indices[i + 2]));
            }
            else
            {
                failureReason = "has no index data";
                return false;
            }

            int actualIndexCount = triangles.Count * 3;
            if (maxVertexCount < positions.Length)
            {
                failureReason = $"streaming vertex capacity {maxVertexCount} is smaller than mesh vertex count {positions.Length}";
                return false;
            }

            if (maxIndexCount < actualIndexCount)
            {
                failureReason = $"streaming index capacity {maxIndexCount} is smaller than mesh index count {actualIndexCount}";
                return false;
            }

            for (int slot = 0; slot < _streamingAtlases.Length; slot++)
            {
                AtlasTierState state = _streamingAtlases[slot];
                if (state.MeshOffsets.ContainsKey(mesh))
                    continue;

                int firstVertex = state.VertexCount;
                int firstIndex = state.IndexCount;
                VerifyBufferLengths(state, firstVertex + maxVertexCount);

                unsafe
                {
                    static void CopyArray<T>(XRDataBuffer? buffer, int startIndex, T[] src) where T : struct
                    {
                        if (buffer is null || src.Length == 0)
                            return;

                        uint spanBytes = (uint)(src.Length * Marshal.SizeOf<T>());
                        var handle = GCHandle.Alloc(src, GCHandleType.Pinned);
                        try
                        {
                            VoidPtr dst = buffer.Address + (int)(startIndex * buffer.ElementSize);
                            Memory.Move(dst, handle.AddrOfPinnedObject(), spanBytes);
                        }
                        finally
                        {
                            handle.Free();
                        }
                    }

                    CopyArray(state.Positions, firstVertex, positions);
                    CopyArray(state.Normals, firstVertex, normals);
                    CopyArray(state.Tangents, firstVertex, tangents);
                    CopyArray(state.UV0, firstVertex, uv0);
                }

                foreach (IndexTriangle triangle in triangles)
                    state.IndirectFaceIndices.Add(triangle);

                int reservedTriangleCount = maxIndexCount / 3;
                IndexTriangle streamingPaddingTriangle = new IndexTriangle(0, 0, 0)!;
                for (int i = triangles.Count; i < reservedTriangleCount; i++)
                    state.IndirectFaceIndices.Add(streamingPaddingTriangle);

                state.VertexCount = firstVertex + maxVertexCount;
                state.IndexCount = firstIndex + maxIndexCount;
                state.MeshOffsets[mesh] = new AtlasAllocation(firstVertex, firstIndex, positions.Length, actualIndexCount, maxVertexCount, maxIndexCount);
                state.Dirty = true;
            }

            _streamingReservations[mesh] = (maxVertexCount, maxIndexCount);
            _activeAtlasTiers[mesh] = EAtlasTier.Streaming;

            EnsureMeshDataCapacity(meshID + 1);
            MeshDataBuffer.Set(meshID, new MeshDataEntry
            {
                IndexCount = (uint)actualIndexCount,
                FirstIndex = (uint)_streamingAtlases[_streamingRenderSlot].MeshOffsets[mesh].FirstIndex,
                FirstVertex = (uint)_streamingAtlases[_streamingRenderSlot].MeshOffsets[mesh].FirstVertex,
                Flags = ComposeMeshDataFlags(EAtlasTier.Streaming)
            });
            _meshDataDirty = true;
            return true;
        }

        public void LoadStaticMeshBatch(IEnumerable<XRMesh> meshes)
        {
            if (meshes is null)
                return;

            using (_lock.EnterScope())
            {
                foreach (XRMesh mesh in meshes)
                {
                    if (mesh is null)
                        continue;

                    if (!ValidateMeshForGpu(mesh, out var validationFailure))
                    {
                        string invalidLabel = mesh.Name ?? "<unnamed>";
                        RecordUnsupportedMesh(mesh, invalidLabel, validationFailure);
                        continue;
                    }

                    string meshLabel = mesh.Name ?? "<unnamed>";
                    GetOrCreateMeshID(mesh, out uint meshID);
                    if (!AppendMeshToAtlas(mesh, EAtlasTier.Static, meshLabel, out var failureReason))
                    {
                        RecordUnsupportedMesh(mesh, meshLabel, failureReason ?? "static atlas registration failed");
                        continue;
                    }

                    EnsureMeshDataCapacity(meshID + 1);
                    _meshDataDirty = true;
                }

                if (_meshDataDirty)
                {
                    UpdateMeshDataBufferFromAtlas();
                    _meshDataDirty = false;
                }

                RebuildAtlasIfDirty(EAtlasTier.Static);
            }
        }

        public bool RegisterStreamingMesh(XRMesh mesh, int maxVertexCount, int maxIndexCount, out uint meshID, out string? failureReason)
        {
            meshID = 0;
            failureReason = null;
            if (mesh is null)
            {
                failureReason = "mesh is null";
                return false;
            }

            using (_lock.EnterScope())
            {
                if (!ValidateMeshForGpu(mesh, out var validationFailure))
                {
                    failureReason = validationFailure;
                    return false;
                }

                GetOrCreateMeshID(mesh, out meshID);
                string meshLabel = mesh.Name ?? $"Mesh_{meshID}";
                if (!ReserveMeshInStreamingAtlas(mesh, meshID, meshLabel, maxVertexCount, maxIndexCount, out failureReason))
                    return false;

                if (_meshDataDirty)
                {
                    MeshDataBuffer.PushSubData();
                    _meshDataDirty = false;
                }

                RebuildAtlasIfDirty(EAtlasTier.Streaming);
                return true;
            }
        }

        public bool TryGetStreamingWritePointers(uint meshID, out StreamingWritePointers pointers)
        {
            pointers = default;
            if (meshID == 0 || !_idToMesh.TryGetValue(meshID, out XRMesh? mesh) || mesh is null)
                return false;

            if (!_streamingReservations.TryGetValue(mesh, out var reservation))
                return false;

            AtlasTierState state = GetStreamingWriteTierState();
            if (!state.MeshOffsets.TryGetValue(mesh, out AtlasAllocation allocation))
                return false;

            state.Positions?.MapBufferData();
            state.Normals?.MapBufferData();
            state.Tangents?.MapBufferData();
            state.UV0?.MapBufferData();
            state.Indices?.MapBufferData();

            XRDataBuffer? positionsBuffer = state.Positions;
            XRDataBuffer? normalsBuffer = state.Normals;
            XRDataBuffer? tangentsBuffer = state.Tangents;
            XRDataBuffer? uv0Buffer = state.UV0;
            XRDataBuffer? indicesBuffer = state.Indices;
            if (positionsBuffer is null || normalsBuffer is null || tangentsBuffer is null || uv0Buffer is null || indicesBuffer is null)
                return false;

            pointers = new StreamingWritePointers(
                positionsBuffer.Address + (int)(allocation.FirstVertex * positionsBuffer.ElementSize),
                normalsBuffer.Address + (int)(allocation.FirstVertex * normalsBuffer.ElementSize),
                tangentsBuffer.Address + (int)(allocation.FirstVertex * tangentsBuffer.ElementSize),
                uv0Buffer.Address + (int)(allocation.FirstVertex * uv0Buffer.ElementSize),
                indicesBuffer.Address + (int)(allocation.FirstIndex * indicesBuffer.ElementSize),
                reservation.maxVertexCount,
                reservation.maxIndexCount);
            return true;
        }

        public bool CommitStreamingMesh(uint meshID, int vertexCount, int indexCount)
        {
            if (meshID == 0 || !_idToMesh.TryGetValue(meshID, out XRMesh? mesh) || mesh is null)
                return false;

            if (!_streamingReservations.TryGetValue(mesh, out var reservation))
                return false;

            if (vertexCount < 0 || vertexCount > reservation.maxVertexCount || indexCount < 0 || indexCount > reservation.maxIndexCount)
                return false;

            if ((indexCount % 3) != 0)
                return false;

            foreach (AtlasTierState state in _streamingAtlases)
            {
                if (!state.MeshOffsets.TryGetValue(mesh, out AtlasAllocation allocation))
                    continue;

                state.MeshOffsets[mesh] = new AtlasAllocation(
                    allocation.FirstVertex,
                    allocation.FirstIndex,
                    vertexCount,
                    indexCount,
                    allocation.ReservedVertexCount,
                    allocation.ReservedIndexCount);
            }

            EnsureMeshDataCapacity(meshID + 1);
            MeshDataBuffer.Set(meshID, new MeshDataEntry
            {
                IndexCount = (uint)indexCount,
                FirstIndex = (uint)_streamingAtlases[_streamingRenderSlot].MeshOffsets[mesh].FirstIndex,
                FirstVertex = (uint)_streamingAtlases[_streamingRenderSlot].MeshOffsets[mesh].FirstVertex,
                Flags = ComposeMeshDataFlags(EAtlasTier.Streaming)
            });
            MeshDataBuffer.PushSubData();
            return true;
        }

        public void AdvanceStreamingAtlasFrame()
        {
            _streamingRenderSlot = _streamingWriteSlot;
            _streamingWriteSlot = (_streamingWriteSlot + 1) % _streamingAtlases.Length;
        }

        public bool UnregisterStreamingMesh(uint meshID)
        {
            if (meshID == 0 || !_idToMesh.TryGetValue(meshID, out XRMesh? mesh) || mesh is null)
                return false;

            if (!_streamingReservations.Remove(mesh))
                return false;

            foreach (AtlasTierState state in _streamingAtlases)
                state.MeshOffsets.Remove(mesh);

            if (_activeAtlasTiers.TryGetValue(mesh, out var tier) && tier == EAtlasTier.Streaming)
                _activeAtlasTiers.Remove(mesh);

            MeshDataBuffer.Set(meshID, default(MeshDataEntry));
            MeshDataBuffer.PushSubData();
            return true;
        }

        public bool MigrateMesh(uint meshID, EAtlasTier fromTier, EAtlasTier toTier)
        {
            if (meshID == 0 || fromTier == toTier)
                return false;

            if (!_idToMesh.TryGetValue(meshID, out XRMesh? mesh) || mesh is null)
                return false;

            if (!_activeAtlasTiers.TryGetValue(mesh, out var currentTier) || currentTier != fromTier)
                return false;

            string meshLabel = _meshDebugLabels.TryGetValue(mesh, out var storedLabel)
                ? storedLabel
                : mesh.Name ?? $"Mesh_{meshID}";

            bool migrated;
            string? failureReason;
            if (toTier == EAtlasTier.Streaming)
            {
                int reserveVertexCount = mesh.VertexCount;
                IList<IndexTriangle>? triangles = mesh.Triangles;
                int triangleCount = triangles is null ? 0 : triangles.Count;
                int reserveIndexCount = mesh.IndexCount > 0 ? mesh.IndexCount : triangleCount * 3;
                migrated = ReserveMeshInStreamingAtlas(mesh, meshID, meshLabel, reserveVertexCount, reserveIndexCount, out failureReason);
            }
            else
            {
                migrated = AppendMeshToAtlas(mesh, toTier, meshLabel, out failureReason);
            }

            if (!migrated)
            {
                if (!string.IsNullOrWhiteSpace(failureReason))
                    Debug.LogWarning($"[GPUScene] Failed to migrate mesh '{meshLabel}' from {fromTier} to {toTier}: {failureReason}");
                return false;
            }

            if (fromTier == EAtlasTier.Streaming)
            {
                foreach (AtlasTierState state in _streamingAtlases)
                    state.MeshOffsets.Remove(mesh);
                _streamingReservations.Remove(mesh);
            }
            else
            {
                RemoveSubmeshFromAtlas(mesh, fromTier);
            }

            _activeAtlasTiers[mesh] = toTier;
            UpdateMeshDataBufferFromAtlas();
            RebuildAtlasIfDirty(toTier);
            return true;
        }

        public void PromoteDynamicToStatic(IEnumerable<uint> meshIds)
        {
            if (meshIds is null)
                return;

            using (_lock.EnterScope())
            {
                foreach (uint meshID in meshIds)
                    _ = MigrateMesh(meshID, EAtlasTier.Dynamic, EAtlasTier.Static);
            }
        }

        private void VerifyBufferLengths(AtlasTierState state, int needed)
        {
            if (state.Positions!.ElementCount < needed)
                state.Positions.Resize((uint)XRMath.NextPowerOfTwo(needed));
            if (state.Normals!.ElementCount < needed)
                state.Normals.Resize((uint)XRMath.NextPowerOfTwo(needed));
            if (state.Tangents!.ElementCount < needed)
                state.Tangents.Resize((uint)XRMath.NextPowerOfTwo(needed));
            if (state.UV0!.ElementCount < needed)
                state.UV0.Resize((uint)XRMath.NextPowerOfTwo(needed));
        }

        private static bool TryPrepareAtlasIndexBuffer(AtlasTierState state, int requiredIndices)
        {
            if (state.Indices is null)
                return false;

            if (requiredIndices <= 0)
                return false;

            uint desiredCapacity = XRMath.NextPowerOfTwo((uint)Math.Max(requiredIndices, 1));
            if (desiredCapacity < (uint)requiredIndices)
                desiredCapacity = (uint)requiredIndices;

            if (state.Indices.ElementCount < desiredCapacity)
                state.Indices.Resize(desiredCapacity);

            if (state.Indices.TryGetAddress(out var writeBase) && writeBase.IsValid)
                return true;

            state.Indices.ClientSideSource?.Dispose();
            state.Indices.ClientSideSource = DataSource.Allocate(desiredCapacity * (uint)sizeof(uint));

            return state.Indices.TryGetAddress(out writeBase) && writeBase.IsValid;
        }

        /// <summary>
        /// Rebuilds (uploads) atlas GPU buffers if marked dirty. Currently only adjusts counts.
        /// </summary>
        public void RebuildAtlasIfDirty()
            => RebuildAtlasIfDirty(EAtlasTier.Dynamic);

        public void RebuildAllAtlasesIfDirty()
        {
            RebuildAtlasIfDirty(EAtlasTier.Static);
            RebuildAtlasIfDirty(EAtlasTier.Dynamic);
            RebuildAtlasIfDirty(EAtlasTier.Streaming);
        }

        public void RebuildAtlasIfDirty(EAtlasTier tier)
        {
            AtlasTierState state = GetTierState(tier);
            if (!state.Dirty)
                return;

            EnsureAtlasBuffers(tier);

            // Grow buffers to required counts (no shrinking to avoid churn)
            if (state.Positions!.ElementCount < state.VertexCount)
                state.Positions.Resize((uint)state.VertexCount);

            if (state.Normals!.ElementCount < state.VertexCount)
                state.Normals.Resize((uint)state.VertexCount);

            if (state.Tangents!.ElementCount < state.VertexCount)
                state.Tangents.Resize((uint)state.VertexCount);

            if (state.UV0!.ElementCount < state.VertexCount)
                state.UV0.Resize((uint)state.VertexCount);

            state.Positions.PushSubData();
            state.Normals.PushSubData();
            state.Tangents.PushSubData();
            state.UV0.PushSubData();

            if (state.Indices is not null)
            {
                IndexTriangle[] faceSnapshot = state.IndirectFaceIndices.Count > 0
                    ? [.. state.IndirectFaceIndices]
                    : [];

                int requiredIndices = faceSnapshot.Length * 3;
                if (requiredIndices > 0)
                {
                    state.IndexCount = requiredIndices;

                    // Determine optimal index element size based on vertex count
                    // Note: For simplicity and MDI compatibility, we always use u32 indices.
                    // Future optimization: use u16 when state.VertexCount < 65536
                    state.IndexElementSize = IndexSize.FourBytes;

                    if (!TryPrepareAtlasIndexBuffer(state, requiredIndices))
                    {
                        Debug.LogWarning($"[GPUScene] Failed to prepare {GetTierLabel(tier)} atlas index buffer; skipping atlas upload to avoid memory corruption.");
                        return;
                    }

                    uint capacity = state.Indices.ElementCount;
                    uint writeIndex = 0;

                    for (int i = 0; i < faceSnapshot.Length; ++i)
                    {
                        if (writeIndex + 2 >= capacity)
                        {
                            Debug.LogWarning($"[GPUScene] Atlas index buffer overflow when rebuilding atlas (capacity={capacity}, required={requiredIndices}).");
                            break;
                        }

                        var tri = faceSnapshot[i];
                        state.Indices.SetDataRawAtIndex(writeIndex++, (uint)tri.Point0);
                        state.Indices.SetDataRawAtIndex(writeIndex++, (uint)tri.Point1);
                        state.Indices.SetDataRawAtIndex(writeIndex++, (uint)tri.Point2);
                    }

                    state.IndexCount = (int)writeIndex;
                    uint byteLength = writeIndex * (uint)sizeof(uint);
                    state.Indices.PushSubData(0, byteLength);
                }
                else
                {
                    state.IndexCount = 0;
                    state.Indices.PushSubData(0, 0);
                }
            }

            UpdateMeshDataBufferFromAtlas();

            state.Dirty = false;
            if (tier == EAtlasTier.Dynamic)
                _atlasDirty = false;
            _atlasVersion++;
            state.Version++;
            SyncLegacyDynamicAtlasState();

            // Notify listeners that atlas was rebuilt (EBO may have changed)
            try
            {
                AtlasRebuilt?.Invoke(this);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GPUScene] AtlasRebuilt event handler failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Update MeshDataBuffer (uint4 per flattened submesh) with atlas offsets.
        /// </summary>
        private void UpdateMeshDataBufferFromAtlas()
        {
            foreach (var kvp in _activeAtlasTiers)
            {
                XRMesh mesh = kvp.Key;
                EAtlasTier tier = kvp.Value;
                AtlasTierState state = GetTierState(tier);
                if (!state.MeshOffsets.TryGetValue(mesh, out AtlasAllocation allocation))
                    continue;

                GetOrCreateMeshID(mesh, out uint meshID);

                MeshDataEntry entry = new()
                {
                    IndexCount = (uint)allocation.IndexCount,
                    FirstIndex = (uint)allocation.FirstIndex,
                    FirstVertex = (uint)allocation.FirstVertex,
                    Flags = ComposeMeshDataFlags(tier)
                };
                MeshDataBuffer.Set(meshID, entry);
            }
            MeshDataBuffer.PushSubData();
        }

        #endregion

        #region Lifecycle Methods

        /// <summary>
        /// Initializes the GPU scene, creating all required buffers.
        /// </summary>
        public void Initialize()
        {
            _meshDataBuffer?.Destroy();
            _meshDataBuffer = MakeMeshDataBuffer();

            _lodTableBuffer?.Destroy();
            _lodTableBuffer = MakeLodTableBuffer();
            _lodRequestBuffer?.Destroy();
            _lodRequestBuffer = MakeLodRequestBuffer();

            _allLoadedCommandsBuffer?.Destroy();
            _allLoadedCommandsBuffer = MakeCommandsInputBuffer();

            _allLoadedTransparencyMetadataBuffer?.Destroy();
            _allLoadedTransparencyMetadataBuffer = MakeTransparencyMetadataBuffer();
            
            _updatingCommandsBuffer?.Destroy();
            _updatingCommandsBuffer = MakeCommandsInputBuffer();

            _updatingTransparencyMetadataBuffer?.Destroy();
            _updatingTransparencyMetadataBuffer = MakeTransparencyMetadataBuffer();
        }

        /// <summary>
        /// Destroys the GPU scene and releases all resources.
        /// </summary>
        public void Destroy()
        {
            static void DestroyTierBuffers(AtlasTierState state)
            {
                state.Positions?.Destroy();
                state.Positions = null;
                state.Normals?.Destroy();
                state.Normals = null;
                state.Tangents?.Destroy();
                state.Tangents = null;
                state.UV0?.Destroy();
                state.UV0 = null;
                state.Indices?.Destroy();
                state.Indices = null;
                state.Dirty = false;
                state.VertexCount = 0;
                state.IndexCount = 0;
                state.Version = 0;
                state.IndexElementSize = IndexSize.FourBytes;
                state.MeshOffsets.Clear();
                state.IndirectFaceIndices.Clear();
            }

            _meshDataBuffer?.Destroy();
            _meshDataBuffer = null;
            _lodTableBuffer?.Destroy();
            _lodTableBuffer = null;
            _lodRequestBuffer?.Destroy();
            _lodRequestBuffer = null;
            _allLoadedCommandsBuffer?.Destroy();
            _allLoadedCommandsBuffer = null;
            _allLoadedTransparencyMetadataBuffer?.Destroy();
            _allLoadedTransparencyMetadataBuffer = null;
            _updatingCommandsBuffer?.Destroy();
            _updatingCommandsBuffer = null;
            _updatingTransparencyMetadataBuffer?.Destroy();
            _updatingTransparencyMetadataBuffer = null;

            DestroyTierBuffers(_staticAtlas);
            DestroyTierBuffers(_dynamicAtlas);
            foreach (AtlasTierState streamingState in _streamingAtlases)
                DestroyTierBuffers(streamingState);

            _atlasPositions = null;
            _atlasNormals = null;
            _atlasTangents = null;
            _atlasUV0 = null;
            _atlasIndices = null;
            _atlasDirty = false;
            _atlasVertexCount = 0;
            _atlasIndexCount = 0;
            _atlasMeshOffsets.Clear();
            _atlasMeshRefCounts.Clear();
            _indirectFaceIndices.Clear();
            _atlasVersion = 0;
            _atlasIndexElementSize = IndexSize.FourBytes;
            _activeAtlasTiers.Clear();
            _streamingReservations.Clear();
            _streamingWriteSlot = 0;
            _streamingRenderSlot = 0;

            _commandAabbBuffer?.Destroy();
            _commandAabbBuffer = null;
            _commandAabbProgram?.Destroy();
            _commandAabbProgram = null;
            _commandAabbShader?.Destroy();
            _commandAabbShader = null;
            _gpuBvhTree?.Dispose();
            _gpuBvhTree = null;
            _meshIDMap.Clear();
            _materialIDMap.Clear();
            _idToMaterial.Clear();
            _idToMesh.Clear();
            _renderableLogicalMeshIdMap.Clear();
            _standaloneLogicalMeshIdMap.Clear();
            _logicalMeshStates.Clear();
            _nextMeshID = 1;
            _nextMaterialID = 1;
            _nextLogicalMeshID = 1;
            _totalCommandCount = 0;
            _updatingCommandCount = 0;
            _bounds = new AABB();
            _meshlets.Clear();
            _commandIndicesPerMeshCommand.Clear();
            _commandIndexLookup.Clear();
            _meshToIndexRemap.Clear();
            _meshDebugLabels.Clear();
            _unsupportedMeshMessages.Clear();
            _bvhReady = false;
            _bvhDirty = false;
            _bvhNodeCount = 0;
            _bvhPrimitiveCount = 0;
            _bvhRefitPending = false;
        }

        #endregion

        #region Double-Buffered Command Buffer Management

        // -------------------------------------------------------------------------
        // Double-Buffered Command Buffer:
        // - _updatingCommandsBuffer: Written by Add/Remove on update/collect threads
        // - _allLoadedCommandsBuffer: Read by render thread
        // - SwapCommandBuffers() copies updating -> render during the swap phase
        // -------------------------------------------------------------------------
        
        /// <summary>
        /// Swaps the updating command buffer with the render command buffer.
        /// Call this from the swap buffers callback to make newly added/removed commands visible to the render thread.
        /// </summary>
        /// <remarks>
        /// This method copies data from the updating buffer to the render buffer, ensuring the render
        /// thread always reads a consistent snapshot while the update thread can continue modifying
        /// the updating buffer.
        /// </remarks>
        public void SwapCommandBuffers()
        {
            using (_lock.EnterScope())
            {
                // Copy the updating buffer data to the render buffer
                // This ensures the render buffer has the latest commands while keeping
                // the updating buffer's indices consistent with _commandIndexLookup
                if (_updatingCommandsBuffer is not null && _allLoadedCommandsBuffer is not null)
                {
                    // Ensure render buffer has sufficient capacity
                    if (_allLoadedCommandsBuffer.ElementCount < _updatingCommandsBuffer.ElementCount)
                        _allLoadedCommandsBuffer.Resize(_updatingCommandsBuffer.ElementCount);
                    
                    // Copy command data from updating to render buffer
                    if (_updatingCommandCount > 0)
                    {
                        uint elementCount = _updatingCommandCount.ClampMax(_updatingCommandsBuffer.ElementCount);
                        uint elementSize = _updatingCommandsBuffer.ElementSize;
                        if (elementSize == 0)
                            elementSize = (uint)(CommandFloatCount * sizeof(float));

                        uint byteCount = elementCount * elementSize;

                        if (_updatingCommandsBuffer.TryGetAddress(out var src) &&
                            _allLoadedCommandsBuffer.TryGetAddress(out var dst))
                        {
                            Memory.Move(dst, src, byteCount);
                            _allLoadedCommandsBuffer.PushSubData(0, byteCount);
                        }
                        else
                        {
                            // Both buffers should always have client-side sources; if not, the copy cannot proceed.
                            Debug.LogWarning("GPUScene: Command buffer TryGetAddress failed during swap — client-side source missing.");
                        }
                    }
                }

                if (_updatingTransparencyMetadataBuffer is not null && _allLoadedTransparencyMetadataBuffer is not null)
                {
                    if (_allLoadedTransparencyMetadataBuffer.ElementCount < _updatingTransparencyMetadataBuffer.ElementCount)
                        _allLoadedTransparencyMetadataBuffer.Resize(_updatingTransparencyMetadataBuffer.ElementCount);

                    if (_updatingCommandCount > 0)
                    {
                        uint elementCount = _updatingCommandCount.ClampMax(_updatingTransparencyMetadataBuffer.ElementCount);
                        uint elementSize = _updatingTransparencyMetadataBuffer.ElementSize;
                        if (elementSize == 0)
                            elementSize = TransparencyMetadataUIntCount * sizeof(uint);

                        uint byteCount = elementCount * elementSize;

                        if (_updatingTransparencyMetadataBuffer.TryGetAddress(out var srcMeta) &&
                            _allLoadedTransparencyMetadataBuffer.TryGetAddress(out var dstMeta))
                        {
                            Memory.Move(dstMeta, srcMeta, byteCount);
                            _allLoadedTransparencyMetadataBuffer.PushSubData(0, byteCount);
                        }
                        else
                        {
                            // Both buffers should always have client-side sources; if not, the copy cannot proceed.
                            Debug.LogWarning("GPUScene: Transparency metadata buffer TryGetAddress failed during swap — client-side source missing.");
                        }
                    }
                }
                
                // Update the render count to match the updating count
                TotalCommandCount = _updatingCommandCount;

                // Update BVH
                if (_useInternalBvh)
                {
                    bool canRefit = _bvhReady && !_bvhDirty && _gpuBvhTree is not null && _bvhPrimitiveCount == _updatingCommandCount;
                    if (canRefit)
                        _bvhRefitPending = true;
                    else
                        MarkBvhDirty();
                }
            }
        }

        /// <summary>
        /// Creates a new command buffer for storing GPU indirect render commands.
        /// </summary>
        private static XRDataBuffer MakeCommandsInputBuffer()
        {
            var buffer = new XRDataBuffer(
                $"RenderCommandsBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinCommandCount,
                EComponentType.Float,
                CommandFloatCount, // 48 floats (192 bytes) per command
                false,
                false)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true
            };
            return buffer;
        }

        private static XRDataBuffer MakeTransparencyMetadataBuffer()
        {
            var buffer = new XRDataBuffer(
                "RenderTransparencyMetadataBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinCommandCount,
                EComponentType.UInt,
                TransparencyMetadataUIntCount,
                false,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true,
            };
            return buffer;
        }

        private static XRDataBuffer MakeLodTransitionBuffer()
        {
            var buffer = new XRDataBuffer(
                "RenderLodTransitionBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinCommandCount,
                EComponentType.UInt,
                LodTransitionUIntCount,
                false,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true,
                StorageFlags = EBufferMapStorageFlags.DynamicStorage | EBufferMapStorageFlags.Read | EBufferMapStorageFlags.Persistent | EBufferMapStorageFlags.Coherent,
                RangeFlags = EBufferMapRangeFlags.Read | EBufferMapRangeFlags.Persistent | EBufferMapRangeFlags.Coherent,
            };
            buffer.Generate();
            buffer.PushSubData();
            buffer.MapBufferData();
            return buffer;
        }

        private void EnsureLodTransitionBufferCapacity(uint requiredSize)
        {
            XRDataBuffer buffer = LodTransitionBuffer;
            if (requiredSize <= buffer.ElementCount)
                return;

            buffer.Resize(requiredSize);
            buffer.PushSubData();
        }

        private void SyncLodTransitionBufferFromGpu()
        {
            if (_lodTransitionBuffer is null)
                return;

            if (_lodTransitionBuffer.ActivelyMapping.Count == 0)
                _lodTransitionBuffer.MapBufferData();

            VoidPtr mapped = _lodTransitionBuffer.GetMappedAddresses().FirstOrDefault(ptr => ptr.IsValid);
            if (!mapped.IsValid || !_lodTransitionBuffer.TryGetAddress(out VoidPtr cpuAddress) || !cpuAddress.IsValid)
                return;

            AbstractRenderer.Current?.MemoryBarrier(EMemoryBarrierMask.ClientMappedBuffer | EMemoryBarrierMask.ShaderStorage);
            Memory.Move(cpuAddress, mapped, _lodTransitionBuffer.Length);
        }

        /// <summary>
        /// Creates a new mesh data buffer for storing per-mesh metadata.
        /// </summary>
        private static XRDataBuffer MakeMeshDataBuffer()
        {
            var buffer = new XRDataBuffer(
                "MeshDataBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinMeshDataEntries,
                EComponentType.UInt,
                4,
                false,
                true)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false
            };
            return buffer;
        }

        #endregion

        #region Command Buffer Constants

        /// <summary>The initial size of the command buffer. It will grow or shrink as needed at powers of two.</summary>
        public const uint MinCommandCount = 8;

        /// <summary>Number of float components per GPU command (192 bytes).</summary>
        public const int CommandFloatCount = 48;

        /// <summary>Number of uint components per hot GPU command (64 bytes).</summary>
        public const int CommandHotUIntCount = 16;

        /// <summary>Number of components in the visible count buffer.</summary>
        public const uint VisibleCountComponents = 3;

        /// <summary>Index for visible draw count in the visible count buffer.</summary>
        public const uint VisibleCountDrawIndex = 0;

        /// <summary>Index for visible instance count in the visible count buffer.</summary>
        public const uint VisibleCountInstanceIndex = 1;

        /// <summary>Index for overflow marker in the visible count buffer.</summary>
        public const uint VisibleCountOverflowIndex = 2;

        /// <summary>Number of uint components per transparency metadata entry.</summary>
        public const uint TransparencyMetadataUIntCount = 4;
        [StructLayout(LayoutKind.Sequential)]
        public struct GPULodTransitionState
        {
            public const uint ActiveFlag = 1u;

            public uint PreviousMeshID;
            public uint PreviousLODLevel;
            public uint Flags;
            public uint ProgressBits;

            public readonly float Progress
                => BitConverter.UInt32BitsToSingle(ProgressBits);

            public static GPULodTransitionState Active(uint previousMeshId, uint previousLodLevel, float progress)
                => new()
                {
                    PreviousMeshID = previousMeshId,
                    PreviousLODLevel = previousLodLevel,
                    Flags = ActiveFlag,
                    ProgressBits = BitConverter.SingleToUInt32Bits(progress),
                };
        }

        public const uint LodTransitionUIntCount = 4;

        /// <summary>Minimum capacity for mesh data entries buffer.</summary>
        private const uint MinMeshDataEntries = 16;

        #endregion

        #region Command Buffer State

        // -------------------------------------------------------------------------
        // Command Buffer State: Buffers, counts, and tracking structures
        // -------------------------------------------------------------------------

        /// <summary>Maps XRMesh instances to unique GPU IDs.</summary>
        private readonly ConcurrentDictionary<XRMesh, uint> _meshIDMap = new();

        /// <summary>Next mesh ID to assign (incremented atomically).</summary>
        private uint _nextMeshID = 1;

        /// <summary>Lock for thread-safe access to command buffers.</summary>
        private readonly Lock _lock = new();

        /// <summary>Debug labels for meshes (for logging/debugging).</summary>
        private readonly ConcurrentDictionary<XRMesh, string> _meshDebugLabels = new();

        /// <summary>Meshes that failed GPU validation with their error messages.</summary>
        private readonly ConcurrentDictionary<XRMesh, string> _unsupportedMeshMessages = new();

        /// <summary>Buffer storing per-mesh metadata (index/vertex offsets).</summary>
        private XRDataBuffer? _meshDataBuffer;

        /// <summary>
        /// Buffer storing mesh index and vertex data for all submeshes in reference to the global VAO.
        /// Layout per entry (uint4): [IndexCount, FirstIndex, FirstVertex, Flags].
        /// </summary>
        public XRDataBuffer MeshDataBuffer => _meshDataBuffer ??= MakeMeshDataBuffer();

        /// <summary>
        /// Per-mesh metadata entry stored in MeshDataBuffer.
        /// </summary>
        public struct MeshDataEntry
        {
            /// <summary>Number of indices in this submesh.</summary>
            public uint IndexCount;

            /// <summary>First index offset in the atlas index buffer.</summary>
            public uint FirstIndex;

            /// <summary>First vertex offset in the atlas vertex buffers.</summary>
            public uint FirstVertex;

            /// <summary>Per-entry flags. Low bits encode the active atlas tier.</summary>
            public uint Flags;
        }

        /// <summary>Render buffer - read by the render thread. Contains stable command data.</summary>
        private XRDataBuffer? _allLoadedCommandsBuffer;

        /// <summary>Render buffer - read by the render thread. Contains stable per-command transparency metadata.</summary>
        private XRDataBuffer? _allLoadedTransparencyMetadataBuffer;
    /// <summary>Per-command LOD transition state shared across frames.</summary>
    private XRDataBuffer? _lodTransitionBuffer;

        /// <summary>Updating buffer - written by Add/Remove operations. Swapped to render buffer.</summary>
        private XRDataBuffer? _updatingCommandsBuffer;

        /// <summary>Updating buffer - written by Add/Remove operations. Swapped to render buffer.</summary>
        private XRDataBuffer? _updatingTransparencyMetadataBuffer;

        /// <summary>
        /// Gets the render command buffer containing all commands for this scene.
        /// This buffer is read by the render thread and updated via <see cref="SwapCommandBuffers"/>.
        /// </summary>
        public XRDataBuffer AllLoadedCommandsBuffer => _allLoadedCommandsBuffer ??= MakeCommandsInputBuffer();
        public XRDataBuffer AllLoadedTransparencyMetadataBuffer => _allLoadedTransparencyMetadataBuffer ??= MakeTransparencyMetadataBuffer();
            public XRDataBuffer LodTransitionBuffer => _lodTransitionBuffer ??= MakeLodTransitionBuffer();
        
        /// <summary>
        /// Gets the updating command buffer being written to by Add/Remove operations.
        /// Swapped with AllLoadedCommandsBuffer via <see cref="SwapCommandBuffers"/>.
        /// </summary>
        private XRDataBuffer UpdatingCommandsBuffer => _updatingCommandsBuffer ??= MakeCommandsInputBuffer();
        private XRDataBuffer UpdatingTransparencyMetadataBuffer => _updatingTransparencyMetadataBuffer ??= MakeTransparencyMetadataBuffer();

        /// <summary>Collection of meshlets for meshlet-based rendering.</summary>
        private readonly MeshletCollection _meshlets = new();

        /// <summary>Gets the meshlet collection for this scene.</summary>
        public MeshletCollection Meshlets => _meshlets;

        /// <summary>Bounding box encompassing all scene geometry.</summary>
        private AABB _bounds;

        /// <summary>Gets or sets the bounding box encompassing all scene geometry.</summary>
        public AABB Bounds
        {
            get => _bounds;
            set => SetField(ref _bounds, value);
        }

        /// <summary>Command count for the render buffer (read by render thread).</summary>
        private uint _totalCommandCount = 0;

        /// <summary>Command count for the updating buffer (written by Add/Remove).</summary>
        private uint _updatingCommandCount = 0;

        /// <summary>
        /// Gets the number of commands currently in the render buffer.
        /// Each command represents one submesh - a single <see cref="IRenderCommandMesh"/> 
        /// may produce multiple commands if it has multiple submeshes.
        /// </summary>
        public uint TotalCommandCount
        {
            get => _totalCommandCount;
            private set => SetField(ref _totalCommandCount, value);
        }
        
        /// <summary>
        /// Gets or sets the command count for the updating buffer.
        /// This count is swapped to TotalCommandCount during <see cref="SwapCommandBuffers"/>.
        /// </summary>
        private uint UpdatingCommandCount
        {
            get => _updatingCommandCount;
            set => SetField(ref _updatingCommandCount, value);
        }

        /// <summary>Gets the current allocated capacity of the command buffer.</summary>
        public uint AllocatedMaxCommandCount => AllLoadedCommandsBuffer.ElementCount;

        /// <summary>
        /// Ensures command buffers can hold at least <paramref name="requiredCapacity"/> entries.
        /// Uses the existing power-of-two growth policy and never shrinks.
        /// </summary>
        public uint EnsureCommandCapacity(uint requiredCapacity)
        {
            uint safeRequired = Math.Max(requiredCapacity, MinCommandCount);
            using (_lock.EnterScope())
            {
                SyncLodTransitionBufferFromGpu();
                VerifyUpdatingBufferSize(safeRequired);
                VerifyCommandBufferSize(safeRequired);
                return AllLoadedCommandsBuffer.ElementCount;
            }
        }

        /// <summary>Maps mesh commands to their GPU command indices (for multi-submesh support).</summary>
        private readonly Dictionary<IRenderCommandMesh, List<uint>> _commandIndicesPerMeshCommand = [];

        #endregion

        #region Add/Remove Commands

        /// <summary>
        /// Adds a render command to the GPU scene.
        /// </summary>
        /// <remarks>
        /// This method writes to the updating buffer. Call <see cref="SwapCommandBuffers"/> 
        /// to make the changes visible to the render thread.
        /// </remarks>
        /// <param name="renderInfo">The render info containing commands to add.</param>
        public void Add(RenderInfo renderInfo)
        {
            if (renderInfo is null || renderInfo.RenderCommands.Count == 0)
                return;

            if (UpdatingCommandCount == uint.MaxValue)
            {
                Debug.LogWarning($"Command buffer full. Cannot add more commands.");
                return;
            }

            using (_lock.EnterScope())
            {
                SyncLodTransitionBufferFromGpu();
                uint startCommandCount = UpdatingCommandCount;
                bool anyAdded = false;
                SceneLog($"Adding commands for {renderInfo.Owner?.GetType().Name ?? "<null>"}");
                for (int i = 0; i < renderInfo.RenderCommands.Count; i++)
                {
                    RenderCommand command = renderInfo.RenderCommands[i];
                    if (command is not IRenderCommandMesh meshCmd)
                    {
                        SceneLog($"Skipping adding command of type {command.GetType().Name}");
                        continue; // Only mesh commands supported
                    }

                    // Skip commands that opt out of GPU indirect dispatch (e.g., skybox, fullscreen effects)
                    var material = meshCmd.MaterialOverride ?? meshCmd.Mesh?.Material;
                    if (material?.RenderOptions?.ExcludeFromGpuIndirect == true)
                    {
                        SceneLog($"Skipping mesh command due to ExcludeFromGpuIndirect flag. Renderable={ResolveOwnerLabel(renderInfo.Owner)}");
                        continue;
                    }

                    var subMeshes = meshCmd.Mesh?.GetMeshes();
                    if (subMeshes is null || subMeshes.Length == 0)
                    {
                        SceneLog($"Skipping mesh command with no submeshes. Renderable={ResolveOwnerLabel(renderInfo.Owner)} Mesh={(meshCmd.Mesh != null ? "present" : "null")} SubMeshes={(subMeshes != null ? $"empty array (length {subMeshes.Length})" : "null")}");
                        if (meshCmd.Mesh != null)
                        {
                            SceneLog($"  Mesh details: Name={meshCmd.Mesh.Mesh?.Name ?? "<null>"}, Submeshes.Count={meshCmd.Mesh.Submeshes.Count}");
                        }
                        continue;
                    }

                    // Ensure we have enough space for ALL submeshes of this command
                    VerifyUpdatingBufferSize(UpdatingCommandCount + (uint)subMeshes.Length);

                    if (!_commandIndicesPerMeshCommand.TryGetValue(meshCmd, out var indices))
                    {
                        indices = new List<uint>(subMeshes.Length);
                        _commandIndicesPerMeshCommand.Add(meshCmd, indices);
                    }

                    for (int subMeshIndex = 0; subMeshIndex < subMeshes.Length; subMeshIndex++)
                    {
                        (XRMesh? mesh, XRMaterial? mat) = subMeshes[subMeshIndex];
                        if (mesh is null)
                        {
                            SceneLog($"Skipping mesh command submesh {subMeshIndex} due to null mesh. Renderable={ResolveOwnerLabel(renderInfo.Owner)} Command={meshCmd.GetType().Name}");
                            continue;
                        }

                        XRMaterial? m = meshCmd.MaterialOverride ?? mat;
                        if (m is null)
                        {
                            SceneLog($"Skipping mesh command submesh {subMeshIndex} due to null material. Renderable={ResolveOwnerLabel(renderInfo.Owner)} Mesh={mesh.Name ?? "<unnamed>"}");
                            continue;
                        }

                        string meshLabel = EnsureMeshDebugLabel(mesh, meshCmd.Mesh, renderInfo, subMeshIndex);

                        if (_unsupportedMeshMessages.ContainsKey(mesh))
                        {
                            SceneLog($"Skipping mesh command submesh {subMeshIndex} ('{meshLabel}') because it was previously marked unsupported.");
                            continue;
                        }

                        if (!ValidateMeshForGpu(mesh, out var validationFailure))
                        {
                            RecordUnsupportedMesh(mesh, meshLabel, validationFailure);
                            continue;
                        }

                        if (!ResolveLogicalMeshRegistration(renderInfo, mesh, (uint)subMeshIndex, meshLabel, out uint meshID, out uint logicalMeshID, out uint lodCount, out var atlasFailure))
                        {
                            RecordUnsupportedMesh(mesh, meshLabel, atlasFailure ?? "atlas registration failed");
                            continue;
                        }

                        var gpuCommand = ConvertToGPUCommand(renderInfo, meshCmd, mesh, m, meshID, logicalMeshID, lodCount, (uint)subMeshIndex);
                        if (gpuCommand is null)
                        {
                            SceneLog($"Skipping adding mesh command submesh {subMeshIndex} due to conversion failure.");
                            continue;
                        }

                        uint index = UpdatingCommandCount++;
                        if (meshCmd.GPUCommandIndex == uint.MaxValue || indices.Count == 0)
                            meshCmd.GPUCommandIndex = index; // Store first index for legacy single-index usage

                        indices.Add(index);
                        _commandIndexLookup.Add(index, (meshCmd, subMeshIndex));

                        GPUIndirectRenderCommand commandValue = gpuCommand.Value;
                        // Preserve the source command index so post-cull stages can map back to CPU-side data.
                        commandValue.Reserved1 = index;
                        UpdatingCommandsBuffer.SetDataRawAtIndex(index, commandValue);
                        UpdatingTransparencyMetadataBuffer.SetDataRawAtIndex(index, GPUTransparencyMetadata.FromMaterial(m));
                                                LodTransitionBuffer.SetDataRawAtIndex(index, default(GPULodTransitionState));
                        AcquireLogicalMeshResidency(commandValue.LogicalMeshID);

                        if (IsGpuSceneLoggingEnabled())
                        {
                            if (_commandBuildLogBudget > 0 && Interlocked.Decrement(ref _commandBuildLogBudget) >= 0)
                            {
                                SceneLog($"[GPUScene/Build] idx={index} mesh={commandValue.MeshID} material={commandValue.MaterialID} pass={commandValue.RenderPass} instances={commandValue.InstanceCount}");
                            }

                            GPUIndirectRenderCommand roundTrip = UpdatingCommandsBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(index);
                            bool matches = roundTrip.MeshID == commandValue.MeshID
                                && roundTrip.MaterialID == commandValue.MaterialID
                                && roundTrip.RenderPass == commandValue.RenderPass;

                            if (!matches)
                            {
                                if (_commandRoundtripMismatchLogBudget > 0 && Interlocked.Decrement(ref _commandRoundtripMismatchLogBudget) >= 0)
                                    Debug.LogWarning($"[GPUScene/RoundTrip] mismatch idx={index} mesh(write={commandValue.MeshID}/read={roundTrip.MeshID}) material(write={commandValue.MaterialID}/read={roundTrip.MaterialID}) pass(write={commandValue.RenderPass}/read={roundTrip.RenderPass})");
                            }
                            else if (_commandRoundtripLogBudget > 0 && Interlocked.Decrement(ref _commandRoundtripLogBudget) >= 0)
                            {
                                SceneLog($"[GPUScene/RoundTrip] idx={index} pass={roundTrip.RenderPass} verified ok");
                            }
                        }

                        anyAdded = true;
                        _meshDebugLabels[mesh] = meshLabel;

                        // _meshlets.AddMesh(mesh, meshID, materialID, Matrix4x4.Identity);
                    }
                }
                if (_meshDataDirty)
                {
                    MeshDataBuffer.PushSubData();
                    _meshDataDirty = false;
                }
                if (anyAdded)
                {
                    // Upload only the newly appended command range.
                    // Pushing the full buffer can hitch when high-detail meshes/submeshes stream in later.
                    uint addedCount = UpdatingCommandCount - startCommandCount;
                    uint elementSize = UpdatingCommandsBuffer.ElementSize;
                    if (elementSize == 0)
                        elementSize = (uint)(CommandFloatCount * sizeof(float));

                    uint byteOffset = startCommandCount * elementSize;
                    uint byteCount = addedCount * elementSize;
                                        LodTransitionBuffer.PushSubData((int)(startCommandCount * LodTransitionBuffer.ElementSize), addedCount * LodTransitionBuffer.ElementSize);
                    UpdatingCommandsBuffer.PushSubData((int)byteOffset, byteCount);
                    SceneLog($"GPUScene.Add: Added commands, total now {UpdatingCommandCount} in UpdatingCommandsBuffer");

                    // Mark BVH dirty so it gets rebuilt before next cull pass
                    if (_useInternalBvh)
                        MarkBvhDirty();
                }
                RebuildAtlasIfDirty();
            }
        }

        private void RemoveSubmeshFromAtlas(XRMesh mesh)
        {
            if (mesh is null || !_activeAtlasTiers.TryGetValue(mesh, out var tier))
                return;

            RemoveSubmeshFromAtlas(mesh, tier);
        }

        private void RemoveSubmeshFromAtlas(XRMesh mesh, EAtlasTier tier)
        {
            AtlasTierState state = GetTierState(tier);
            if (!state.MeshOffsets.TryGetValue(mesh, out AtlasAllocation atlas))
                return;

            // Remove data from the atlas and compact client-side buffers so later meshes keep valid offsets.
            int indexOffset = atlas.FirstIndex;
            int indexCount = atlas.IndexCount;
            int vertexOffset = atlas.FirstVertex;
            int vertexCount = atlas.ReservedVertexCount;

            // Convert index counts from index-space (per uint) to triangle-space (per IndexTriangle).
            int triangleOffset = indexOffset / 3;
            int triangleCount = indexCount / 3;

            if (indexCount % 3 != 0)
            {
                Debug.LogWarning($"Mesh '{mesh.Name}' stored with a non-multiple-of-three index count ({indexCount}). Clamping removal to available triangles.");
                triangleCount = System.Math.Min(triangleCount + 1, state.IndirectFaceIndices.Count - triangleOffset);
            }

            if (triangleOffset < 0 || triangleOffset >= state.IndirectFaceIndices.Count)
            {
                Debug.LogWarning($"Atlas removal offset out of range for mesh '{mesh.Name}'. Offset={triangleOffset}, Count={triangleCount}, Total={state.IndirectFaceIndices.Count}.");
                triangleCount = 0;
            }
            else if (triangleOffset + triangleCount > state.IndirectFaceIndices.Count)
            {
                triangleCount = state.IndirectFaceIndices.Count - triangleOffset;
            }

            int removedTriangleCount = triangleCount > 0 ? triangleCount : 0;
            if (removedTriangleCount > 0)
                state.IndirectFaceIndices.RemoveRange(triangleOffset, removedTriangleCount);

            int removedIndexCount = atlas.ReservedIndexCount;

            // Compact vertex attribute buffers by sliding higher ranges down over the removed span.
            if (vertexCount > 0)
            {
                int verticesToMove = state.VertexCount - (vertexOffset + vertexCount);
                if (verticesToMove > 0)
                {
                    unsafe
                    {
                        static void SlideDown(XRDataBuffer? buffer, int startIndex, int removeCount, int elementsToMove)
                        {
                            if (buffer is null || elementsToMove <= 0)
                                return;

                            uint byteCount = (uint)(elementsToMove * buffer.ElementSize);
                            VoidPtr dst = buffer.Address + (int)(startIndex * buffer.ElementSize);
                            VoidPtr src = buffer.Address + (int)((startIndex + removeCount) * buffer.ElementSize);
                            Memory.Move(dst, src, byteCount);
                        }

                        SlideDown(state.Positions, vertexOffset, vertexCount, verticesToMove);
                        SlideDown(state.Normals, vertexOffset, vertexCount, verticesToMove);
                        SlideDown(state.Tangents, vertexOffset, vertexCount, verticesToMove);
                        SlideDown(state.UV0, vertexOffset, vertexCount, verticesToMove);
                    }
                }
            }

            // Update offsets for remaining meshes now that we compacted buffers.
            List<XRMesh> meshesToAdjust = [.. state.MeshOffsets.Keys];
            foreach (XRMesh otherMesh in meshesToAdjust)
            {
                if (otherMesh == mesh)
                    continue;

                AtlasAllocation entry = state.MeshOffsets[otherMesh];
                int adjustedFirstVertex = entry.FirstVertex;
                int adjustedFirstIndex = entry.FirstIndex;
                if (vertexCount > 0 && adjustedFirstVertex > vertexOffset)
                    adjustedFirstVertex -= vertexCount;
                if (removedIndexCount > 0 && adjustedFirstIndex > indexOffset)
                    adjustedFirstIndex -= removedIndexCount;
                state.MeshOffsets[otherMesh] = new AtlasAllocation(adjustedFirstVertex, adjustedFirstIndex, entry.VertexCount, entry.IndexCount, entry.ReservedVertexCount, entry.ReservedIndexCount);
            }

            state.MeshOffsets.Remove(mesh);
            _activeAtlasTiers.Remove(mesh);

            // Adjust aggregated counts after compaction.
            state.VertexCount -= vertexCount;
            state.IndexCount -= removedIndexCount;
            if (state.VertexCount < 0)
                state.VertexCount = 0;
            if (state.IndexCount < 0)
                state.IndexCount = 0;

            MarkAtlasDirty(tier);
            SyncLegacyDynamicAtlasState();
        }

        /// <summary>
        /// Ensures atlas geometry + MeshDataBuffer entry exist for a mesh, but does NOT change atlas lifetime.
        /// Use this for hydration and validation code paths.
        /// </summary>
        private bool EnsureSubmeshInAtlas(XRMesh mesh, uint meshID, string meshLabel, out string? failureReason)
        {
            failureReason = null;

            EAtlasTier tier = _activeAtlasTiers.TryGetValue(mesh, out var existingTier)
                ? existingTier
                : EAtlasTier.Dynamic;

            // Ensure mesh geometry recorded in atlas buffers: vertex + index data
            if (!AppendMeshToAtlas(mesh, tier, meshLabel, out failureReason))
            {
                EnsureMeshDataCapacity(meshID + 1);
                MeshDataBuffer.Set(meshID, default(MeshDataEntry));
                _meshDataDirty = true;
                return false;
            }

            AtlasTierState state = GetTierState(tier);
            if (!state.MeshOffsets.TryGetValue(mesh, out AtlasAllocation atlas))
            {
                failureReason = "did not produce atlas offsets";
                EnsureMeshDataCapacity(meshID + 1);
                MeshDataBuffer.Set(meshID, default(MeshDataEntry));
                _meshDataDirty = true;
                return false;
            }

            // Update length of mesh data buffer if needed - this provides indices into the atlas data
            EnsureMeshDataCapacity(meshID + 1); // +1 because index-based capacity
            int vFirst = atlas.FirstVertex;
            int iFirst = atlas.FirstIndex;
            int iCount = atlas.IndexCount;
            if (iCount <= 0)
            {
                failureReason = "produced zero indices after atlas packing";
                MeshDataBuffer.Set(meshID, default(MeshDataEntry));
                _meshDataDirty = true;
                return false;
            }

            MeshDataEntry entry = new()
            {
                IndexCount = (uint)iCount,
                FirstIndex = (uint)iFirst,
                FirstVertex = (uint)vFirst,
                Flags = ComposeMeshDataFlags(tier)
            };
            MeshDataBuffer.Set(meshID, entry);

            _meshDataDirty = true;
            return true;
        }

        private void IncrementAtlasMeshRefCount(XRMesh mesh)
            => IncrementAtlasMeshRefCount(mesh, 1);

        private void IncrementAtlasMeshRefCount(XRMesh mesh, int amount)
        {
            if (mesh is null || amount <= 0)
                return;

            if (!_atlasMeshRefCounts.TryGetValue(mesh, out int count))
                count = 0;

            _atlasMeshRefCounts[mesh] = count + amount;
        }

        private void IncrementAtlasMeshRefCount(uint meshID, int amount, string context)
        {
            if (meshID == 0 || amount <= 0)
                return;

            if (!_idToMesh.TryGetValue(meshID, out XRMesh? mesh) || mesh is null)
            {
                if (_commandUpdateErrorLogBudget > 0 && Interlocked.Decrement(ref _commandUpdateErrorLogBudget) >= 0)
                    Debug.LogWarning($"[GPUScene] {context}: unable to resolve MeshID={meshID} for atlas refcount increment.");
                return;
            }

            IncrementAtlasMeshRefCount(mesh, amount);
        }

        private void DecrementAtlasMeshRefCount(uint meshID, string context)
            => DecrementAtlasMeshRefCount(meshID, context, 1);

        private void DecrementAtlasMeshRefCount(uint meshID, string context, int amount)
        {
            if (meshID == 0 || amount <= 0)
                return;

            if (!_idToMesh.TryGetValue(meshID, out var mesh) || mesh is null)
            {
                if (_commandUpdateErrorLogBudget > 0 && Interlocked.Decrement(ref _commandUpdateErrorLogBudget) >= 0)
                    Debug.LogWarning($"[GPUScene] {context}: unable to resolve MeshID={meshID} for atlas refcount decrement.");
                return;
            }

            if (!_atlasMeshRefCounts.TryGetValue(mesh, out int count))
            {
                if (_commandUpdateErrorLogBudget > 0 && Interlocked.Decrement(ref _commandUpdateErrorLogBudget) >= 0)
                    Debug.LogWarning($"[GPUScene] {context}: atlas refcount missing for mesh '{mesh.Name ?? "<unnamed>"}' (MeshID={meshID}); treating as 0.");
                count = 0;
            }

            count -= amount;
            if (count > 0)
            {
                _atlasMeshRefCounts[mesh] = count;
                return;
            }

            _atlasMeshRefCounts.Remove(mesh);

            // Clear MeshData entry for safety (prevents stale atlas offsets from being consumed).
            EnsureMeshDataCapacity(meshID + 1);
            MeshDataBuffer.Set(meshID, default(MeshDataEntry));
            _meshDataDirty = true;

            // Remove atlas geometry only when no commands reference it.
            RemoveSubmeshFromAtlas(mesh);
        }

        private bool TryGetActiveAtlasAllocation(XRMesh mesh, out EAtlasTier tier, out AtlasAllocation allocation)
        {
            allocation = default;
            tier = EAtlasTier.Dynamic;
            if (!_activeAtlasTiers.TryGetValue(mesh, out tier))
                return false;

            AtlasTierState state = GetTierState(tier);
            return state.MeshOffsets.TryGetValue(mesh, out allocation);
        }

        private readonly Dictionary<uint, (IRenderCommandMesh command, int subMeshIndex)> _commandIndexLookup = [];
        private readonly Dictionary<XRMesh, uint> _meshToIndexRemap = []; // retained for future reverse lookups (unused by atlas sizing)
        private bool _meshDataDirty = false; // tracks pending GPU upload for mesh metadata

        /// <summary>
        /// Attempts to get the mesh data entry for a given mesh ID.
        /// If the mesh hasn't been added to the atlas yet, attempts to hydrate it.
        /// </summary>
        /// <param name="meshID">The mesh ID to look up.</param>
        /// <param name="entry">The mesh data entry if found.</param>
        /// <returns>True if the entry was found or successfully created; false otherwise.</returns>
        public bool TryGetMeshDataEntry(uint meshID, out MeshDataEntry entry)
        {
            entry = default;
            if (meshID == 0)
                return false;

            EnsureMeshDataCapacity(meshID + 1);

            // CPU-side lookup: atlas tier state is the authoritative source, avoiding GPU readback.
            if (_idToMesh.TryGetValue(meshID, out var mesh) && mesh is not null)
            {
                if (TryGetActiveAtlasAllocation(mesh, out EAtlasTier tier, out AtlasAllocation allocation) && allocation.IndexCount > 0)
                {
                    entry = new MeshDataEntry
                    {
                        IndexCount = (uint)allocation.IndexCount,
                        FirstIndex = (uint)allocation.FirstIndex,
                        FirstVertex = (uint)allocation.FirstVertex,
                        Flags = ComposeMeshDataFlags(tier)
                    };
                    return true;
                }

                if (_unsupportedMeshMessages.ContainsKey(mesh))
                {
                    SceneLog($"TryGetMeshDataEntry: meshID={meshID} already marked unsupported (mesh={mesh.Name ?? "<unnamed>"}).");
                    return false;
                }

                string meshLabel = _meshDebugLabels.TryGetValue(mesh, out var storedLabel)
                    ? storedLabel
                    : mesh.Name ?? $"Mesh_{meshID}";

                if (!EnsureSubmeshInAtlas(mesh, meshID, meshLabel, out var hydrationFailure))
                {
                    hydrationFailure ??= "atlas registration failed during lookup";
                    SceneLog($"TryGetMeshDataEntry: meshID={meshID} atlas hydration failed for '{meshLabel}' reason={hydrationFailure}.");
                    RecordUnsupportedMesh(mesh, meshLabel, hydrationFailure);
                    return false;
                }
                if (_meshDataDirty)
                {
                    MeshDataBuffer.PushSubData();
                    _meshDataDirty = false;
                }

                // Re-check CPU-side cache after hydration
                if (TryGetActiveAtlasAllocation(mesh, out tier, out allocation) && allocation.IndexCount > 0)
                {
                    entry = new MeshDataEntry
                    {
                        IndexCount = (uint)allocation.IndexCount,
                        FirstIndex = (uint)allocation.FirstIndex,
                        FirstVertex = (uint)allocation.FirstVertex,
                        Flags = ComposeMeshDataFlags(tier)
                    };
                    return true;
                }
            }

            entry = default;
            SceneLog($"TryGetMeshDataEntry: meshID={meshID} missing mesh data entry after hydration attempt.");
            return false;
        }

        /// <summary>
        /// Ensures MeshDataBuffer has capacity for at least the specified number of entries.
        /// </summary>
        /// <param name="requiredEntries">Minimum number of entries needed.</param>
        private void EnsureMeshDataCapacity(uint requiredEntries)
        {
            var buffer = MeshDataBuffer; // lazy create
            if (requiredEntries <= buffer.ElementCount)
                return;

            uint newCapacity = XRMath.NextPowerOfTwo(requiredEntries).ClampMin(MinMeshDataEntries);
            SceneLog($"Resizing MeshDataBuffer from {buffer.ElementCount} to {newCapacity} (need {requiredEntries}).");
            buffer.Resize(newCapacity);
        }

        /// <summary>
        /// Removes a render command from the GPU scene.
        /// </summary>
        /// <remarks>
        /// This method modifies the updating buffer. Call <see cref="SwapCommandBuffers"/> 
        /// to make the changes visible to the render thread.
        /// </remarks>
        /// <param name="info">The render info containing commands to remove.</param>
        public void Remove(RenderInfo info)
        {
            if (info is null || info.RenderCommands.Count == 0 || UpdatingCommandCount == 0)
                return;

            using (_lock.EnterScope())
            {
                SyncLodTransitionBufferFromGpu();
                bool anyRemoved = false;
                foreach (RenderCommand command in info.RenderCommands)
                {
                    if (command is not IRenderCommandMesh meshCmd)
                        continue;

                    if (!_commandIndicesPerMeshCommand.TryGetValue(meshCmd, out var indices) || indices.Count == 0)
                        continue; // Nothing to remove

                    foreach (uint idx in indices.OrderByDescending(v => v))
                    {
                        RemoveCommandAtIndex(idx);
                        anyRemoved = true;
                    }

                    indices.Clear();
                    _commandIndicesPerMeshCommand.Remove(meshCmd);
                    meshCmd.GPUCommandIndex = uint.MaxValue;
                }

                // Resize once after batch removals
                VerifyUpdatingBufferSize(UpdatingCommandCount);
                if (anyRemoved)
                {
                    UpdatingCommandsBuffer.PushSubData();
                    LodTransitionBuffer.PushSubData();

                    if (_meshDataDirty)
                    {
                        MeshDataBuffer.PushSubData();
                        _meshDataDirty = false;
                    }

                    // Mark BVH dirty so it gets rebuilt before next cull pass
                    if (_useInternalBvh)
                        MarkBvhDirty();

                    RebuildAtlasIfDirty();
                }
            }
        }

        private void RemoveCommandAtIndex(uint targetIndex)
        {
            if (targetIndex >= UpdatingCommandCount)
            {
                Debug.LogWarning($"Invalid command index {targetIndex} for removal. Total commands: {UpdatingCommandCount}");
                return;
            }

            // Capture removed command before we overwrite slots (swap-remove).
            GPUIndirectRenderCommand removedCommand = UpdatingCommandsBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(targetIndex);
            uint removedMeshId = removedCommand.MeshID;
            uint removedLogicalMeshId = removedCommand.LogicalMeshID;

            uint lastIndex = UpdatingCommandCount - 1;

            // Remove the lookup entry for the removed command early.
            _commandIndexLookup.Remove(targetIndex);
            if (targetIndex < lastIndex)
            {
                GPUIndirectRenderCommand lastCommand = UpdatingCommandsBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(lastIndex);
                GPUTransparencyMetadata lastMetadata = UpdatingTransparencyMetadataBuffer.GetDataRawAtIndex<GPUTransparencyMetadata>(lastIndex);
                GPULodTransitionState lastTransition = LodTransitionBuffer.GetDataRawAtIndex<GPULodTransitionState>(lastIndex);
                lastCommand.Reserved1 = targetIndex;
                UpdatingCommandsBuffer.SetDataRawAtIndex(targetIndex, lastCommand);
                UpdatingTransparencyMetadataBuffer.SetDataRawAtIndex(targetIndex, lastMetadata);
                LodTransitionBuffer.SetDataRawAtIndex(targetIndex, lastTransition);

                if (_commandIndexLookup.TryGetValue(lastIndex, out var movedCommand))
                {
                    _commandIndexLookup.Remove(lastIndex);
                    _commandIndexLookup[targetIndex] = movedCommand;

                    if (_commandIndicesPerMeshCommand.TryGetValue(movedCommand.command, out var movedList))
                    {
                        int pos = movedList.IndexOf(lastIndex);
                        if (pos >= 0)
                            movedList[pos] = targetIndex;
                    }

                    if (_commandIndicesPerMeshCommand.TryGetValue(movedCommand.command, out var list) && list.Count > 0)
                        movedCommand.command.GPUCommandIndex = list[0];
                }
                else
                {
                    if (_commandUpdateErrorLogBudget > 0 && Interlocked.Decrement(ref _commandUpdateErrorLogBudget) >= 0)
                        Debug.LogWarning($"[GPUScene] RemoveCommandAtIndex: missing lookup for moved lastIndex={lastIndex} -> targetIndex={targetIndex}.");
                }
            }
            else
            {
                _commandIndexLookup.Remove(lastIndex);
            }

            LodTransitionBuffer.SetDataRawAtIndex(lastIndex, default(GPULodTransitionState));

            // Update mesh atlas lifetime after structural changes.
            if (removedLogicalMeshId != 0)
                ReleaseLogicalMeshResidency(removedLogicalMeshId, "RemoveCommandAtIndex");
            else
                DecrementAtlasMeshRefCount(removedMeshId, "RemoveCommandAtIndex");
            --UpdatingCommandCount;
        }

        //TODO: Optimize to avoid frequent resizes (eg, remove and add right after each other at the boundary)
        private void VerifyUpdatingBufferSize(uint requiredSize)
        {
            uint currentCapacity = UpdatingCommandsBuffer.ElementCount;
            uint nextPowerOfTwo = XRMath.NextPowerOfTwo(requiredSize).ClampMin(MinCommandCount);
            if (nextPowerOfTwo == currentCapacity)
                return;

            SceneLog($"Resizing updating command buffer from {currentCapacity} to {nextPowerOfTwo}.");
            UpdatingCommandsBuffer.Resize(nextPowerOfTwo);
            UpdatingTransparencyMetadataBuffer.Resize(nextPowerOfTwo);
            EnsureLodTransitionBufferCapacity(nextPowerOfTwo);
            uint newCapacity = UpdatingCommandsBuffer.ElementCount;
            if (newCapacity > currentCapacity)
            {
                ZeroUpdatingCommandRange(currentCapacity, newCapacity - currentCapacity);
                ZeroUpdatingTransparencyMetadataRange(currentCapacity, newCapacity - currentCapacity);
                ZeroLodTransitionRange(currentCapacity, newCapacity - currentCapacity);
            }
        }

        private void ZeroUpdatingCommandRange(uint startIndex, uint count)
        {
            if (count == 0)
                return;

            var blank = default(GPUIndirectRenderCommand);
            uint end = startIndex + count;
            for (uint i = startIndex; i < end; ++i)
                UpdatingCommandsBuffer.SetDataRawAtIndex(i, blank);

            uint elementSize = UpdatingCommandsBuffer.ElementSize;
            if (elementSize == 0)
                elementSize = (uint)(CommandFloatCount * sizeof(float));

            uint byteOffset = startIndex * elementSize;
            uint byteCount = count * elementSize;
            UpdatingCommandsBuffer.PushSubData((int)byteOffset, byteCount);

            if (IsGpuSceneLoggingEnabled())
                SceneLog($"Zeroed updating command buffer range [{startIndex}, {end}) ({byteCount} bytes)");
        }

        private void ZeroUpdatingTransparencyMetadataRange(uint startIndex, uint count)
        {
            if (count == 0)
                return;

            var blank = default(GPUTransparencyMetadata);
            uint end = startIndex + count;
            for (uint i = startIndex; i < end; ++i)
                UpdatingTransparencyMetadataBuffer.SetDataRawAtIndex(i, blank);

            uint elementSize = UpdatingTransparencyMetadataBuffer.ElementSize;
            if (elementSize == 0)
                elementSize = TransparencyMetadataUIntCount * sizeof(uint);

            uint byteOffset = startIndex * elementSize;
            uint byteCount = count * elementSize;
            UpdatingTransparencyMetadataBuffer.PushSubData((int)byteOffset, byteCount);
        }

        //TODO: Optimize to avoid frequent resizes (eg, remove and add right after each other at the boundary)
        private void VerifyCommandBufferSize(uint requiredSize)
        {
            uint currentCapacity = AllLoadedCommandsBuffer.ElementCount;
            uint nextPowerOfTwo = XRMath.NextPowerOfTwo(requiredSize).ClampMin(MinCommandCount);
            if (nextPowerOfTwo == currentCapacity)
                return;

            SceneLog($"Resizing command buffer from {currentCapacity} to {nextPowerOfTwo}.");
            AllLoadedCommandsBuffer.Resize(nextPowerOfTwo);
            AllLoadedTransparencyMetadataBuffer.Resize(nextPowerOfTwo);
            uint newCapacity = AllLoadedCommandsBuffer.ElementCount;
            if (newCapacity > currentCapacity)
            {
                ZeroCommandRange(currentCapacity, newCapacity - currentCapacity);
                ZeroTransparencyMetadataRange(currentCapacity, newCapacity - currentCapacity);
            }
        }

        private void ZeroCommandRange(uint startIndex, uint count)
        {
            if (count == 0)
                return;

            var blank = default(GPUIndirectRenderCommand);
            uint end = startIndex + count;
            for (uint i = startIndex; i < end; ++i)
                AllLoadedCommandsBuffer.SetDataRawAtIndex(i, blank);

            uint elementSize = AllLoadedCommandsBuffer.ElementSize;
            if (elementSize == 0)
                elementSize = (uint)(CommandFloatCount * sizeof(float));

            uint byteOffset = startIndex * elementSize;
            uint byteCount = count * elementSize;
            AllLoadedCommandsBuffer.PushSubData((int)byteOffset, byteCount);

            if (IsGpuSceneLoggingEnabled())
                SceneLog($"Zeroed command buffer range [{startIndex}, {end}) ({byteCount} bytes)");
        }

        private void ZeroTransparencyMetadataRange(uint startIndex, uint count)
        {
            if (count == 0)
                return;

            var blank = default(GPUTransparencyMetadata);
            uint end = startIndex + count;
            for (uint i = startIndex; i < end; ++i)
                AllLoadedTransparencyMetadataBuffer.SetDataRawAtIndex(i, blank);

            uint elementSize = AllLoadedTransparencyMetadataBuffer.ElementSize;
            if (elementSize == 0)
                elementSize = TransparencyMetadataUIntCount * sizeof(uint);

            uint byteOffset = startIndex * elementSize;
            uint byteCount = count * elementSize;
            AllLoadedTransparencyMetadataBuffer.PushSubData((int)byteOffset, byteCount);
        }

        private void ZeroLodTransitionRange(uint startIndex, uint count)
        {
            if (count == 0)
                return;

            var blank = default(GPULodTransitionState);
            uint end = startIndex + count;
            for (uint i = startIndex; i < end; ++i)
                LodTransitionBuffer.SetDataRawAtIndex(i, blank);

            uint elementSize = LodTransitionBuffer.ElementSize;
            if (elementSize == 0)
                elementSize = LodTransitionUIntCount * sizeof(uint);

            uint byteOffset = startIndex * elementSize;
            uint byteCount = count * elementSize;
            LodTransitionBuffer.PushSubData((int)byteOffset, byteCount);
        }

        #endregion

        #region Command Conversion

        /// <summary>
        /// Converts a render command to a GPU-friendly format.
        /// </summary>
        /// <param name="renderInfo">The parent render info.</param>
        /// <param name="command">The mesh render command to convert.</param>
        /// <param name="mesh">The mesh to render.</param>
        /// <param name="material">The material to use.</param>
        /// <param name="submeshLocalIndex">The submesh index within the mesh renderer.</param>
        /// <returns>The GPU command, or null if conversion failed.</returns>
        private GPUIndirectRenderCommand? ConvertToGPUCommand(RenderInfo renderInfo, IRenderCommandMesh command, XRMesh? mesh, XRMaterial? material, uint meshID, uint logicalMeshID, uint lodCount, uint submeshLocalIndex)
        {
            if (mesh is null || material is null)
                return null;

            GetOrCreateMaterialID(material, out uint materialID);

            Matrix4x4 modelMatrix = command.WorldMatrixIsModelMatrix ? command.WorldMatrix : Matrix4x4.Identity;

            var gpuCommand = new GPUIndirectRenderCommand
            {
                MeshID = meshID,
                SubmeshID = (meshID << 16) | (submeshLocalIndex & 0xFFFF),
                MaterialID = materialID,
                RenderPass = (uint)command.RenderPass,
                InstanceCount = command.Instances == 0 ? 1u : command.Instances,
                LayerMask = 0xFFFFFFFF,
                RenderDistance = 0f,
                WorldMatrix = modelMatrix,
                PrevWorldMatrix = modelMatrix, // Initialize to current; will be updated on subsequent frames
                Flags = 0,
                LODLevel = 0,
                ShaderProgramID = 0,
                LogicalMeshID = logicalMeshID,
                Reserved1 = 0
            };

            // Bounds: world-space (center + radius), conservative for non-uniform scale.
            SetWorldSpaceBoundingSphere(ref gpuCommand, mesh.Bounds, modelMatrix);

            gpuCommand.RenderDistance = command.RenderDistance.ClampMin(0.0f);

            uint flags = 0;
            if (renderInfo is RenderInfo3D info3d && command is RenderCommandMesh3D)
            {
                if (material.IsTransparentLike())
                    flags |= (uint)GPUIndirectRenderFlags.Transparent;
                if (info3d.CastsShadows)
                    flags |= (uint)GPUIndirectRenderFlags.CastShadow;
                if (info3d.ReceivesShadows)
                    flags |= (uint)GPUIndirectRenderFlags.ReceiveShadows;

                // LayerMask is consumed by GPU culling paths (GPURenderCulling*.comp).
                gpuCommand.LayerMask = 1u << info3d.Layer;
            }

            if (lodCount > 1)
                flags |= (uint)GPUIndirectRenderFlags.LODEnabled;

            gpuCommand.Flags = flags;
            return gpuCommand;
        }

        #region Command Updates (Phase 1)

        /// <summary>
        /// Updates existing GPU commands for a single mesh render command.
        /// Intended to be called during the swap/collect phases (single-threaded) to keep GPU state correct
        /// under transform/material/pass churn without remove/re-add.
        /// </summary>
        public bool TryUpdateMeshCommand(RenderInfo renderInfo, IRenderCommandMesh meshCmd)
        {
            if (renderInfo is null || meshCmd is null)
                return false;

            bool rebuildRenderable = false;
            bool anyChanged = false;

            using (_lock.EnterScope())
            {
                SyncLodTransitionBufferFromGpu();
                if (!_commandIndicesPerMeshCommand.TryGetValue(meshCmd, out var indices) || indices.Count == 0)
                    return false;

                // If this command is now excluded from GPU indirect, remove its indices.
                var topMaterial = meshCmd.MaterialOverride ?? meshCmd.Mesh?.Material;
                if (topMaterial?.RenderOptions?.ExcludeFromGpuIndirect == true)
                {
                    if (_commandUpdateErrorLogBudget > 0 && Interlocked.Decrement(ref _commandUpdateErrorLogBudget) >= 0)
                        Debug.LogWarning($"[GPUScene] ExcludeFromGpuIndirect became true; removing mesh command. Renderable={ResolveOwnerLabel(renderInfo.Owner)}");

                    RemoveMeshCommandIndices(meshCmd, indices);
                    return true;
                }

                var subMeshes = meshCmd.Mesh?.GetMeshes();
                if (subMeshes is null || subMeshes.Length == 0)
                {
                    if (_commandUpdateErrorLogBudget > 0 && Interlocked.Decrement(ref _commandUpdateErrorLogBudget) >= 0)
                        Debug.LogWarning($"[GPUScene] Mesh command lost submeshes; removing. Renderable={ResolveOwnerLabel(renderInfo.Owner)}");

                    RemoveMeshCommandIndices(meshCmd, indices);
                    return true;
                }

                Matrix4x4 modelMatrix = meshCmd.WorldMatrixIsModelMatrix ? meshCmd.WorldMatrix : Matrix4x4.Identity;

                uint minIndex = uint.MaxValue;
                uint maxIndex = 0;

                for (int i = 0; i < indices.Count; i++)
                {
                    uint index = indices[i];
                    if (index >= UpdatingCommandCount)
                        continue;

                    if (!_commandIndexLookup.TryGetValue(index, out var lookup))
                        continue;

                    int subMeshIndex = lookup.subMeshIndex;
                    if ((uint)subMeshIndex >= (uint)subMeshes.Length)
                    {
                        rebuildRenderable = true;
                        break;
                    }

                    (XRMesh? mesh, XRMaterial? mat) = subMeshes[subMeshIndex];
                    XRMaterial? material = meshCmd.MaterialOverride ?? mat;
                    if (mesh is null || material is null)
                    {
                        rebuildRenderable = true;
                        break;
                    }

                    if (_unsupportedMeshMessages.ContainsKey(mesh))
                    {
                        RemoveMeshCommandIndices(meshCmd, indices);
                        return true;
                    }

                    if (!ValidateMeshForGpu(mesh, out var validationFailure))
                    {
                        string meshLabel = EnsureMeshDebugLabel(mesh, meshCmd.Mesh, renderInfo, subMeshIndex);
                        RecordUnsupportedMesh(mesh, meshLabel, validationFailure);

                        RemoveMeshCommandIndices(meshCmd, indices);
                        return true;
                    }

                    GetOrCreateMaterialID(material, out uint newMaterialID);

                    string resolvedMeshLabel = EnsureMeshDebugLabel(mesh, meshCmd.Mesh, renderInfo, subMeshIndex);
                    if (!ResolveLogicalMeshRegistration(renderInfo, mesh, (uint)subMeshIndex, resolvedMeshLabel, out uint newMeshID, out uint newLogicalMeshID, out uint lodCount, out var atlasFailure))
                    {
                        atlasFailure ??= "atlas registration failed";
                        RecordUnsupportedMesh(mesh, resolvedMeshLabel, atlasFailure);
                        RemoveMeshCommandIndices(meshCmd, indices);
                        return true;
                    }

                    var existing = UpdatingCommandsBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(index);
                    var updated = existing;

                    updated.PrevWorldMatrix = existing.WorldMatrix;
                    updated.WorldMatrix = modelMatrix;
                    SetWorldSpaceBoundingSphere(ref updated, mesh.Bounds, modelMatrix);

                    updated.MeshID = newMeshID;
                    updated.SubmeshID = (newMeshID << 16) | ((uint)subMeshIndex & 0xFFFF);
                    updated.MaterialID = newMaterialID;
                    updated.InstanceCount = meshCmd.Instances == 0 ? 1u : meshCmd.Instances;
                    updated.RenderPass = (uint)meshCmd.RenderPass;
                    updated.RenderDistance = meshCmd.RenderDistance.ClampMin(0.0f);
                    updated.LogicalMeshID = newLogicalMeshID;
                    updated.Reserved1 = index;

                    uint flags = 0;
                    if (renderInfo is RenderInfo3D info3d)
                    {
                        if (material.IsTransparentLike())
                            flags |= (uint)GPUIndirectRenderFlags.Transparent;
                        if (info3d.CastsShadows)
                            flags |= (uint)GPUIndirectRenderFlags.CastShadow;
                        if (info3d.ReceivesShadows)
                            flags |= (uint)GPUIndirectRenderFlags.ReceiveShadows;

                        updated.LayerMask = 1u << info3d.Layer;
                    }
                    if (lodCount > 1)
                        flags |= (uint)GPUIndirectRenderFlags.LODEnabled;
                    updated.Flags = flags;
                    UpdatingTransparencyMetadataBuffer.SetDataRawAtIndex(index, GPUTransparencyMetadata.FromMaterial(material));

                    if (existing.LogicalMeshID != newLogicalMeshID)
                    {
                        AcquireLogicalMeshResidency(newLogicalMeshID);
                        ReleaseLogicalMeshResidency(existing.LogicalMeshID, "TryUpdateMeshCommand(mesh changed)");
                    }

                    if (!existing.Equals(updated))
                    {
                        UpdatingCommandsBuffer.SetDataRawAtIndex(index, updated);
                        if (existing.MeshID != updated.MeshID || existing.LogicalMeshID != updated.LogicalMeshID)
                            LodTransitionBuffer.SetDataRawAtIndex(index, default(GPULodTransitionState));
                        anyChanged = true;
                        minIndex = Math.Min(minIndex, index);
                        maxIndex = Math.Max(maxIndex, index);
                    }
                }

                if (!rebuildRenderable)
                {
                    if (!anyChanged)
                        return false;

                    uint elementSize = UpdatingCommandsBuffer.ElementSize;
                    if (elementSize == 0)
                        elementSize = (uint)(CommandFloatCount * sizeof(float));

                    uint byteOffset = minIndex * elementSize;
                    uint byteCount = (maxIndex - minIndex + 1) * elementSize;
                    UpdatingCommandsBuffer.PushSubData((int)byteOffset, byteCount);
                    LodTransitionBuffer.PushSubData((int)(minIndex * LodTransitionBuffer.ElementSize), (maxIndex - minIndex + 1) * LodTransitionBuffer.ElementSize);

                    if (_meshDataDirty)
                    {
                        MeshDataBuffer.PushSubData();
                        _meshDataDirty = false;
                    }

                    if (_useInternalBvh)
                    {
                        bool canRefit = _bvhReady && !_bvhDirty && _gpuBvhTree is not null && _bvhPrimitiveCount == _updatingCommandCount;
                        if (canRefit)
                            _bvhRefitPending = true;
                        else
                            MarkBvhDirty();
                    }

                    RebuildAtlasIfDirty();
                }
            }

            if (rebuildRenderable)
            {
                if (_commandUpdateErrorLogBudget > 0 && Interlocked.Decrement(ref _commandUpdateErrorLogBudget) >= 0)
                    Debug.LogWarning($"[GPUScene] Rebuilding renderable GPU commands due to structural mismatch. Renderable={ResolveOwnerLabel(renderInfo.Owner)}");

                Remove(renderInfo);
                Add(renderInfo);
                return true;
            }

            return anyChanged;
        }

        private void RemoveMeshCommandIndices(IRenderCommandMesh meshCmd, List<uint> indices)
        {
            foreach (uint idx in indices.OrderByDescending(v => v))
                RemoveCommandAtIndex(idx);

            indices.Clear();
            _commandIndicesPerMeshCommand.Remove(meshCmd);
            meshCmd.GPUCommandIndex = uint.MaxValue;
        }

        #endregion

        private string EnsureMeshDebugLabel(XRMesh mesh, XRMeshRenderer? renderer, RenderInfo renderInfo, int subMeshIndex)
        {
            if (_meshDebugLabels.TryGetValue(mesh, out var existing))
                return existing;

            string baseName = !string.IsNullOrWhiteSpace(mesh.Name)
                ? mesh.Name!
                : !string.IsNullOrWhiteSpace(renderer?.Name)
                    ? renderer!.Name!
                    : !string.IsNullOrWhiteSpace(mesh.OriginalPath)
                        ? Path.GetFileName(mesh.OriginalPath) ?? string.Empty
                        : !string.IsNullOrWhiteSpace(mesh.FilePath)
                            ? Path.GetFileName(mesh.FilePath) ?? string.Empty
                            : ResolveOwnerLabel(renderInfo.Owner);

            if (string.IsNullOrWhiteSpace(baseName))
                baseName = $"mesh_{mesh.ID.ToString("N")[..8]}";

            string label = subMeshIndex >= 0 ? $"{baseName} (submesh {subMeshIndex})" : baseName;

            if (string.IsNullOrWhiteSpace(mesh.Name))
                mesh.Name = baseName;

            _meshDebugLabels[mesh] = label;
            return label;
        }

        /// <summary>
        /// Validates that a mesh can be used with GPU rendering.
        /// </summary>
        /// <param name="mesh">The mesh to validate.</param>
        /// <param name="reason">The reason for failure if validation fails.</param>
        /// <returns>True if the mesh is valid for GPU rendering; false otherwise.</returns>
        private bool ValidateMeshForGpu(XRMesh mesh, out string reason)
        {
            if (mesh.VertexCount <= 0)
            {
                reason = "contains no vertices";
                return false;
            }

            if (mesh.IndexCount <= 0)
            {
                reason = "contains no indices";
                return false;
            }

            if (mesh.Type != EPrimitiveType.Triangles)
            {
                reason = $"uses unsupported primitive topology '{mesh.Type}'";
                return false;
            }

            bool hasTriangleList = mesh.Triangles is not null && mesh.Triangles.Count > 0;
            bool hasIndexedTriangles = mesh.IndexCount >= 3 && mesh.GetIndices(EPrimitiveType.Triangles)?.Length >= 3;

            if (!hasTriangleList && !hasIndexedTriangles)
            {
                reason = "has no triangle faces";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Records that a mesh is unsupported for GPU rendering and logs a warning.
        /// </summary>
        private void RecordUnsupportedMesh(XRMesh mesh, string meshLabel, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "is not compatible with GPU rendering";

            string message = $"Skipping mesh '{meshLabel}': {reason}.";
            if (reason.IndexOf("unsupported primitive topology", StringComparison.OrdinalIgnoreCase) >= 0)
                message += " Convert the mesh to a triangle list before import.";

            string? sourceHint = !string.IsNullOrWhiteSpace(mesh.OriginalPath)
                ? mesh.OriginalPath
                : !string.IsNullOrWhiteSpace(mesh.FilePath)
                    ? mesh.FilePath
                    : null;

            if (sourceHint is not null)
                message += $" Source: {sourceHint}.";

            if (_unsupportedMeshMessages.TryAdd(mesh, message))
                Debug.LogWarning(message);
        }

        /// <summary>
        /// Resolves a human-readable label for a renderable owner.
        /// </summary>
        private static string ResolveOwnerLabel(IRenderable? owner)
        {
            if (owner is null)
                return string.Empty;

            if (owner is XRObjectBase obj && !string.IsNullOrWhiteSpace(obj.Name))
                return obj.Name!;

            if (owner is XRComponent component)
            {
                if (!string.IsNullOrWhiteSpace(component.Name))
                    return component.Name!;

                string? sceneNodeName = component.SceneNode?.Name;
                if (!string.IsNullOrWhiteSpace(sceneNodeName))
                    return sceneNodeName!;
            }

            return owner.GetType().Name;
        }

        #endregion

        #region Mesh/Material ID Management

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

        #endregion

        #region IGpuBvhProvider Implementation

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
        }

        /// <summary>
        /// Rebuilds the internal command BVH if it's dirty and enabled.
        /// This builds a simple CPU-side BVH and uploads to GPU buffers.
        /// </summary>
        public void RebuildBvhIfDirty()
        {
            if (!_useInternalBvh || !_bvhDirty)
                return;

            RebuildInternalBvh();
            _bvhDirty = false;
        }

        private void RebuildInternalBvh()
        {
            uint commandCount = _totalCommandCount;
            if (commandCount == 0 || _allLoadedCommandsBuffer is null)
            {
                _bvhReady = false;
                _bvhNodeCount = 0;
                _bvhPrimitiveCount = 0;
                _gpuBvhTree?.Clear();
                return;
            }

            EnsureGpuBvhResources(commandCount);
            DispatchCommandAabbBuild(commandCount);

            _gpuBvhTree!.Build(_commandAabbBuffer!, commandCount, _bounds);

            _bvhNodeCount = _gpuBvhTree.NodeCount;
            _bvhPrimitiveCount = _gpuBvhTree.PrimitiveCount;
            _bvhReady = _bvhNodeCount > 0 && _bvhPrimitiveCount == commandCount;

            if (IsGpuSceneLoggingEnabled())
                SceneLog($"[GPUScene] Built internal BVH with {_bvhNodeCount} nodes for {commandCount} commands");
        }

        private void RefitInternalBvh(uint commandCount)
        {
            if (_gpuBvhTree is null || _allLoadedCommandsBuffer is null || _commandAabbBuffer is null)
                return;

            if (commandCount == 0 || _bvhPrimitiveCount == 0)
                return;

            DispatchCommandAabbBuild(commandCount);
            _gpuBvhTree.Refit();

            _bvhNodeCount = _gpuBvhTree.NodeCount;
            _bvhPrimitiveCount = _gpuBvhTree.PrimitiveCount;
            _bvhReady = _bvhNodeCount > 0 && _bvhPrimitiveCount == commandCount;
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
                _gpuBvhTree?.Clear();
                return;
            }

            if (_bvhDirty || !_bvhReady || _bvhPrimitiveCount != commandCount || _gpuBvhTree is null)
            {
                RebuildInternalBvh();
                _bvhDirty = false;
                _bvhRefitPending = false;
                return;
            }

            if (_bvhRefitPending)
            {
                RefitInternalBvh(commandCount);
                _bvhRefitPending = false;
            }
        }

        private void EnsureGpuBvhResources(uint commandCount)
        {
            _gpuBvhTree ??= new GpuBvhTree();
            EnsureCommandAabbBuffer(commandCount);
            EnsureCommandAabbProgram();
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
            if (_commandAabbProgram is null || _allLoadedCommandsBuffer is null || _commandAabbBuffer is null)
                return;

            var program = _commandAabbProgram;
            program.BindBuffer(_allLoadedCommandsBuffer, 0);
            program.BindBuffer(_commandAabbBuffer, 1);
            program.Uniform("numCommands", commandCount);

            (uint x, uint y, uint z) = XRRenderProgram.ComputeDispatch.ForCommands(Math.Max(commandCount, 1u));
            program.DispatchCompute(x, y, z, EMemoryBarrierMask.ShaderStorage);
        }

        #endregion
    }
}
