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
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Components;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
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
    /// <item><description>Command Buffer: Array of <see cref="GPUIndirectRenderCommand"/> structures (48 floats each)</description></item>
    /// <item><description>Mesh Atlas: Unified vertex attributes (positions, normals, tangents, UVs) and index data</description></item>
    /// <item><description>MeshData Buffer: Per-mesh metadata mapping mesh IDs to atlas offsets</description></item>
    /// </list>
    /// </remarks>
    public class GPUScene : XRBase, IGpuBvhProvider
    {
        #region Internal Types

        /// <summary>
        /// Internal structure for tracking mesh index/vertex information.
        /// </summary>
        private struct MeshInfo
        {
            public uint IndexCount;
            public uint FirstIndex;
            public uint BaseVertex;
            //public XRMesh SourceMesh;
            //public uint MaterialID;
            //public Matrix4x4 Transform;
        }

        #endregion

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

        private static void SetWorldSpaceBoundingSphere(ref GPUIndirectRenderCommand cmd, in AABB localBounds, in Matrix4x4 modelMatrix)
        {
            Vector3 localCenter = localBounds.Center;
            float localRadius = localBounds.HalfExtents.Length();

            Vector3 worldCenter = Vector3.Transform(localCenter, modelMatrix);
            float maxScale = ComputeMaxAxisScale(modelMatrix);
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

        /// <summary>Gets the current total vertex count in the atlas.</summary>
        public int AtlasVertexCount => _atlasVertexCount;

        /// <summary>Gets the current total index count in the atlas.</summary>
        public int AtlasIndexCount => _atlasIndexCount;

        /// <summary>Gets the version number incremented on each atlas rebuild. Use for change detection.</summary>
        public uint AtlasVersion => _atlasVersion;

        /// <summary>Gets the index element size used in the atlas index buffer (u8, u16, or u32).</summary>
        public IndexSize AtlasIndexElementSize => _atlasIndexElementSize;

        /// <summary>Gets the atlas buffer containing position data.</summary>
        public XRDataBuffer? AtlasPositions => _atlasPositions;

        /// <summary>Gets the atlas buffer containing normal data.</summary>
        public XRDataBuffer? AtlasNormals => _atlasNormals;

        /// <summary>Gets the atlas buffer containing tangent data.</summary>
        public XRDataBuffer? AtlasTangents => _atlasTangents;

        /// <summary>Gets the atlas buffer containing UV0 data.</summary>
        public XRDataBuffer? AtlasUV0 => _atlasUV0;

        /// <summary>Gets the atlas buffer containing index data.</summary>
        public XRDataBuffer? AtlasIndices => _atlasIndices;

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

        /// <summary>Remaining log entries for command update details.</summary>
        private int _commandUpdateLogBudget = 16;

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

        /// <summary>
        /// Marks the atlas as dirty so it will be rebuilt before next render if needed.
        /// </summary>
        private void MarkAtlasDirty()
            => _atlasDirty = true;

        /// <summary>
        /// Ensures atlas buffers exist with minimal allocation on first use.
        /// Creates position, normal, tangent, UV0, and index buffers.
        /// </summary>
        public void EnsureAtlasBuffers()
        {
            _atlasPositions ??= new XRDataBuffer(ECommonBufferType.Position.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 3, false, false)
            {
                Name = "MeshAtlas_Positions",
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false,
                BindingIndexOverride = 0
            };
            _atlasNormals ??= new XRDataBuffer(ECommonBufferType.Normal.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 3, false, false)
            {
                Name = "MeshAtlas_Normals",
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false,
                BindingIndexOverride = 1
            };
            _atlasTangents ??= new XRDataBuffer(ECommonBufferType.Tangent.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 4, false, false)
            {
                Name = "MeshAtlas_Tangents",
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false,
                BindingIndexOverride = 2
            };
            _atlasUV0 ??= new XRDataBuffer($"{ECommonBufferType.TexCoord}0", EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 2, false, false)
            {
                Name = "MeshAtlas_UV0",
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false,
                BindingIndexOverride = 3
            };
            _atlasIndices ??= new XRDataBuffer("MeshAtlas_Indices", EBufferTarget.ElementArrayBuffer, 0, EComponentType.UInt, 1, false, true)
            {
                Usage = EBufferUsage.StreamDraw,
                DisposeOnPush = false,
                PadEndingToVec4 = false
            };
            _atlasPositions!.BindingIndexOverride ??= 0;
            _atlasNormals!.BindingIndexOverride ??= 1;
            _atlasTangents!.BindingIndexOverride ??= 2;
            _atlasUV0!.BindingIndexOverride ??= 3;
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
        {
            failureReason = null;
            //Make sure the buffers exist - positions, normals, etc
            EnsureAtlasBuffers();

            if (_atlasMeshOffsets.ContainsKey(mesh))
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
                var tan = mesh.GetTangent(v);
                tangents[v] = new Vector4(tan, 1.0f);
                uv0[v] = mesh.GetTexCoord(v, 0);
            }

            // Indices: expand mesh primitive lists into triangle list (only triangle topology supported here)
            int indexCountAdded = 0;
            if (mesh.Triangles is not null && mesh.Triangles.Count > 0)
            {
                foreach (IndexTriangle tri in mesh.Triangles)
                {
                    // Store indices relative to the mesh; DrawElementsIndirect will offset by baseVertex.
                    _indirectFaceIndices.Add(new IndexTriangle(
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
                    _indirectFaceIndices.Add(new IndexTriangle(
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

            int firstVertex = _atlasVertexCount;
            int firstIndex = _atlasIndexCount;

            // Grow per-attribute buffers (power-of-two growth to reduce reallocs)
            VerifyBufferLengths(firstVertex + vertexCount);

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

                CopyArray(_atlasPositions, firstVertex, positions);
                CopyArray(_atlasNormals, firstVertex, normals);
                CopyArray(_atlasTangents, firstVertex, tangents);
                CopyArray(_atlasUV0, firstVertex, uv0);
            }

            _atlasVertexCount = firstVertex + positions.Length;
            _atlasIndexCount = firstIndex + indexCountAdded;
            _atlasMeshOffsets[mesh] = (firstVertex, firstIndex, indexCountAdded);
            _atlasDirty = true; // we've written client-side; PushSubData below in rebuild
            return true;
        }

        private void VerifyBufferLengths(int needed)
        {
            if (_atlasPositions!.ElementCount < needed)
                _atlasPositions.Resize((uint)XRMath.NextPowerOfTwo(needed));
            if (_atlasNormals!.ElementCount < needed)
                _atlasNormals.Resize((uint)XRMath.NextPowerOfTwo(needed));
            if (_atlasTangents!.ElementCount < needed)
                _atlasTangents.Resize((uint)XRMath.NextPowerOfTwo(needed));
            if (_atlasUV0!.ElementCount < needed)
                _atlasUV0.Resize((uint)XRMath.NextPowerOfTwo(needed));
        }

        private bool TryPrepareAtlasIndexBuffer(int requiredIndices)
        {
            if (_atlasIndices is null)
                return false;

            if (requiredIndices <= 0)
                return false;

            uint desiredCapacity = XRMath.NextPowerOfTwo((uint)Math.Max(requiredIndices, 1));
            if (desiredCapacity < (uint)requiredIndices)
                desiredCapacity = (uint)requiredIndices;

            if (_atlasIndices.ElementCount < desiredCapacity)
                _atlasIndices.Resize(desiredCapacity);

            if (_atlasIndices.TryGetAddress(out var writeBase) && writeBase.IsValid)
                return true;

            _atlasIndices.ClientSideSource?.Dispose();
            _atlasIndices.ClientSideSource = DataSource.Allocate(desiredCapacity * (uint)sizeof(uint));

            return _atlasIndices.TryGetAddress(out writeBase) && writeBase.IsValid;
        }

        /// <summary>
        /// Rebuilds (uploads) atlas GPU buffers if marked dirty. Currently only adjusts counts.
        /// </summary>
        public void RebuildAtlasIfDirty()
        {
            if (!_atlasDirty)
                return;

            EnsureAtlasBuffers();

            // Grow buffers to required counts (no shrinking to avoid churn)
            if (_atlasPositions!.ElementCount < _atlasVertexCount)
                _atlasPositions.Resize((uint)_atlasVertexCount);

            if (_atlasNormals!.ElementCount < _atlasVertexCount)
                _atlasNormals.Resize((uint)_atlasVertexCount);

            if (_atlasTangents!.ElementCount < _atlasVertexCount)
                _atlasTangents.Resize((uint)_atlasVertexCount);

            if (_atlasUV0!.ElementCount < _atlasVertexCount)
                _atlasUV0.Resize((uint)_atlasVertexCount);

            _atlasPositions.PushSubData();
            _atlasNormals.PushSubData();
            _atlasTangents.PushSubData();
            _atlasUV0.PushSubData();

            if (_atlasIndices is not null)
            {
                IndexTriangle[] faceSnapshot = _indirectFaceIndices.Count > 0
                    ? [.. _indirectFaceIndices]
                    : [];

                int requiredIndices = faceSnapshot.Length * 3;
                if (requiredIndices > 0)
                {
                    _atlasIndexCount = requiredIndices;

                    // Determine optimal index element size based on vertex count
                    // Note: For simplicity and MDI compatibility, we always use u32 indices.
                    // Future optimization: use u16 when _atlasVertexCount < 65536
                    _atlasIndexElementSize = IndexSize.FourBytes;

                    if (!TryPrepareAtlasIndexBuffer(requiredIndices))
                    {
                        Debug.LogWarning("[GPUScene] Failed to prepare atlas index buffer; skipping atlas upload to avoid memory corruption.");
                        return;
                    }

                    uint capacity = _atlasIndices.ElementCount;
                    uint writeIndex = 0;

                    for (int i = 0; i < faceSnapshot.Length; ++i)
                    {
                        if (writeIndex + 2 >= capacity)
                        {
                            Debug.LogWarning($"[GPUScene] Atlas index buffer overflow when rebuilding atlas (capacity={capacity}, required={requiredIndices}).");
                            break;
                        }

                        var tri = faceSnapshot[i];
                        _atlasIndices.SetDataRawAtIndex(writeIndex++, (uint)tri.Point0);
                        _atlasIndices.SetDataRawAtIndex(writeIndex++, (uint)tri.Point1);
                        _atlasIndices.SetDataRawAtIndex(writeIndex++, (uint)tri.Point2);
                    }

                    _atlasIndexCount = (int)writeIndex;
                    uint byteLength = writeIndex * (uint)sizeof(uint);
                    _atlasIndices.PushSubData(0, byteLength);
                }
                else
                {
                    _atlasIndexCount = 0;
                    _atlasIndices.PushSubData(0, 0);
                }
            }

            UpdateMeshDataBufferFromAtlas();

            _atlasDirty = false;
            _atlasVersion++;

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
            foreach (var kvp in _atlasMeshOffsets)
            {
                GetOrCreateMeshID(kvp.Key, out uint meshID);

                var (vFirst, iFirst, iCount) = kvp.Value;
                MeshDataEntry entry = new()
                {
                    IndexCount = (uint)iCount,
                    FirstIndex = (uint)iFirst,
                    FirstVertex = (uint)vFirst,
                    BaseInstance = 0
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

            _allLoadedCommandsBuffer?.Destroy();
            _allLoadedCommandsBuffer = MakeCommandsInputBuffer();
            
            _updatingCommandsBuffer?.Destroy();
            _updatingCommandsBuffer = MakeCommandsInputBuffer();
        }

        /// <summary>
        /// Destroys the GPU scene and releases all resources.
        /// </summary>
        public void Destroy()
        {
            _meshDataBuffer?.Destroy();
            _meshDataBuffer = null;
            _allLoadedCommandsBuffer?.Destroy();
            _allLoadedCommandsBuffer = null;
            _updatingCommandsBuffer?.Destroy();
            _updatingCommandsBuffer = null;

            _atlasPositions?.Destroy();
            _atlasPositions = null;
            _atlasNormals?.Destroy();
            _atlasNormals = null;
            _atlasTangents?.Destroy();
            _atlasTangents = null;
            _atlasUV0?.Destroy();
            _atlasUV0 = null;
            _atlasIndices?.Destroy();
            _atlasIndices = null;
            _atlasDirty = false;
            _atlasVertexCount = 0;
            _atlasIndexCount = 0;
            _atlasMeshOffsets.Clear();
            _atlasMeshRefCounts.Clear();
            _indirectFaceIndices.Clear();
            _atlasVersion = 0;
            _atlasIndexElementSize = IndexSize.FourBytes;

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
            _nextMeshID = 1;
            _nextMaterialID = 1;
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
                            // Fallback: copy via struct array to avoid componentCount mismatch
                            var commands = _updatingCommandsBuffer.GetDataArrayRawAtIndex<GPUIndirectRenderCommand>(0, (int)elementCount);
                            _allLoadedCommandsBuffer.SetDataArrayRawAtIndex(0, commands);
                            _allLoadedCommandsBuffer.PushSubData(0, byteCount);
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

            /// <summary>Base instance for instanced rendering (usually 0).</summary>
            public uint BaseInstance;
        }

        /// <summary>Render buffer - read by the render thread. Contains stable command data.</summary>
        private XRDataBuffer? _allLoadedCommandsBuffer;

        /// <summary>Updating buffer - written by Add/Remove operations. Swapped to render buffer.</summary>
        private XRDataBuffer? _updatingCommandsBuffer;

        /// <summary>
        /// Gets the render command buffer containing all commands for this scene.
        /// This buffer is read by the render thread and updated via <see cref="SwapCommandBuffers"/>.
        /// </summary>
        public XRDataBuffer AllLoadedCommandsBuffer => _allLoadedCommandsBuffer ??= MakeCommandsInputBuffer();
        
        /// <summary>
        /// Gets the updating command buffer being written to by Add/Remove operations.
        /// Swapped with AllLoadedCommandsBuffer via <see cref="SwapCommandBuffers"/>.
        /// </summary>
        private XRDataBuffer UpdatingCommandsBuffer => _updatingCommandsBuffer ??= MakeCommandsInputBuffer();

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

                        GetOrCreateMeshID(mesh, out uint meshID);

                        if (!AddSubmeshToAtlas(mesh, meshID, meshLabel, out var atlasFailure))
                        {
                            RecordUnsupportedMesh(mesh, meshLabel, atlasFailure ?? "atlas registration failed");
                            continue;
                        }

                        var gpuCommand = ConvertToGPUCommand(renderInfo, meshCmd, mesh, m, (uint)subMeshIndex);
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
            if (mesh is null || !_atlasMeshOffsets.TryGetValue(mesh, out var atlas))
                return;

            // Remove data from the atlas and compact client-side buffers so later meshes keep valid offsets.
            int indexOffset = atlas.firstIndex;
            int indexCount = atlas.indexCount;
            int vertexOffset = atlas.firstVertex;
            int vertexCount = mesh.VertexCount;

            // Convert index counts from index-space (per uint) to triangle-space (per IndexTriangle).
            int triangleOffset = indexOffset / 3;
            int triangleCount = indexCount / 3;

            if (indexCount % 3 != 0)
            {
                Debug.LogWarning($"Mesh '{mesh.Name}' stored with a non-multiple-of-three index count ({indexCount}). Clamping removal to available triangles.");
                triangleCount = System.Math.Min(triangleCount + 1, _indirectFaceIndices.Count - triangleOffset);
            }

            if (triangleOffset < 0 || triangleOffset >= _indirectFaceIndices.Count)
            {
                Debug.LogWarning($"Atlas removal offset out of range for mesh '{mesh.Name}'. Offset={triangleOffset}, Count={triangleCount}, Total={_indirectFaceIndices.Count}.");
                triangleCount = 0;
            }
            else if (triangleOffset + triangleCount > _indirectFaceIndices.Count)
            {
                triangleCount = _indirectFaceIndices.Count - triangleOffset;
            }

            int removedTriangleCount = triangleCount > 0 ? triangleCount : 0;
            if (removedTriangleCount > 0)
                _indirectFaceIndices.RemoveRange(triangleOffset, removedTriangleCount);

            int removedIndexCount = removedTriangleCount * 3;

            // Compact vertex attribute buffers by sliding higher ranges down over the removed span.
            if (vertexCount > 0)
            {
                int verticesToMove = _atlasVertexCount - (vertexOffset + vertexCount);
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

                        SlideDown(_atlasPositions, vertexOffset, vertexCount, verticesToMove);
                        SlideDown(_atlasNormals, vertexOffset, vertexCount, verticesToMove);
                        SlideDown(_atlasTangents, vertexOffset, vertexCount, verticesToMove);
                        SlideDown(_atlasUV0, vertexOffset, vertexCount, verticesToMove);
                    }
                }
            }

            // Update offsets for remaining meshes now that we compacted buffers.
            List<XRMesh> meshesToAdjust = [.. _atlasMeshOffsets.Keys];
            foreach (XRMesh otherMesh in meshesToAdjust)
            {
                if (otherMesh == mesh)
                    continue;

                var entry = _atlasMeshOffsets[otherMesh];
                if (vertexCount > 0 && entry.firstVertex > vertexOffset)
                    entry.firstVertex -= vertexCount;
                if (removedIndexCount > 0 && entry.firstIndex > indexOffset)
                    entry.firstIndex -= removedIndexCount;
                _atlasMeshOffsets[otherMesh] = entry;
            }

            _atlasMeshOffsets.Remove(mesh);

            // Adjust aggregated counts after compaction.
            _atlasVertexCount -= vertexCount;
            _atlasIndexCount -= removedIndexCount;
            if (_atlasVertexCount < 0)
                _atlasVertexCount = 0;
            if (_atlasIndexCount < 0)
                _atlasIndexCount = 0;

            MarkAtlasDirty();
        }

        private bool AddSubmeshToAtlas(XRMesh mesh, uint meshID, string meshLabel, out string? failureReason)
        {
            if (!EnsureSubmeshInAtlas(mesh, meshID, meshLabel, out failureReason))
                return false;

            IncrementAtlasMeshRefCount(mesh);
            return true;
        }

        /// <summary>
        /// Ensures atlas geometry + MeshDataBuffer entry exist for a mesh, but does NOT change atlas lifetime.
        /// Use this for hydration and validation code paths.
        /// </summary>
        private bool EnsureSubmeshInAtlas(XRMesh mesh, uint meshID, string meshLabel, out string? failureReason)
        {
            failureReason = null;

            // Ensure mesh geometry recorded in atlas buffers: vertex + index data
            if (!AppendMeshToAtlas(mesh, meshLabel, out failureReason))
            {
                EnsureMeshDataCapacity(meshID + 1);
                MeshDataBuffer.Set(meshID, default(MeshDataEntry));
                _meshDataDirty = true;
                return false;
            }

            if (!_atlasMeshOffsets.TryGetValue(mesh, out var atlas))
            {
                failureReason = "did not produce atlas offsets";
                EnsureMeshDataCapacity(meshID + 1);
                MeshDataBuffer.Set(meshID, default(MeshDataEntry));
                _meshDataDirty = true;
                return false;
            }

            // Update length of mesh data buffer if needed - this provides indices into the atlas data
            EnsureMeshDataCapacity(meshID + 1); // +1 because index-based capacity
            (int vFirst, int iFirst, int iCount) = atlas;
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
                BaseInstance = 0
            };
            MeshDataBuffer.Set(meshID, entry);

            _meshDataDirty = true;
            return true;
        }

        private void IncrementAtlasMeshRefCount(XRMesh mesh)
        {
            if (mesh is null)
                return;

            if (!_atlasMeshRefCounts.TryGetValue(mesh, out int count))
                count = 0;

            _atlasMeshRefCounts[mesh] = count + 1;
        }

        private void DecrementAtlasMeshRefCount(uint meshID, string context)
        {
            if (meshID == 0)
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

            count -= 1;
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

            if (meshID < MeshDataBuffer.ElementCount)
            {
                entry = MeshDataBuffer.GetDataRawAtIndex<MeshDataEntry>(meshID);
                if (entry.IndexCount != 0)
                    return true;
            }

            if (_idToMesh.TryGetValue(meshID, out var mesh) && mesh is not null)
            {
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

                entry = MeshDataBuffer.GetDataRawAtIndex<MeshDataEntry>(meshID);
                if (entry.IndexCount != 0)
                    return true;
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

            uint lastIndex = UpdatingCommandCount - 1;

            // Remove the lookup entry for the removed command early.
            _commandIndexLookup.Remove(targetIndex);
            if (targetIndex < lastIndex)
            {
                GPUIndirectRenderCommand lastCommand = UpdatingCommandsBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(lastIndex);
                lastCommand.Reserved1 = targetIndex;
                UpdatingCommandsBuffer.SetDataRawAtIndex(targetIndex, lastCommand);

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

            // Update mesh atlas lifetime after structural changes.
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
            uint newCapacity = UpdatingCommandsBuffer.ElementCount;
            if (newCapacity > currentCapacity)
                ZeroUpdatingCommandRange(currentCapacity, newCapacity - currentCapacity);
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

        //TODO: Optimize to avoid frequent resizes (eg, remove and add right after each other at the boundary)
        private void VerifyCommandBufferSize(uint requiredSize)
        {
            uint currentCapacity = AllLoadedCommandsBuffer.ElementCount;
            uint nextPowerOfTwo = XRMath.NextPowerOfTwo(requiredSize).ClampMin(MinCommandCount);
            if (nextPowerOfTwo == currentCapacity)
                return;

            SceneLog($"Resizing command buffer from {currentCapacity} to {nextPowerOfTwo}.");
            AllLoadedCommandsBuffer.Resize(nextPowerOfTwo);
            uint newCapacity = AllLoadedCommandsBuffer.ElementCount;
            if (newCapacity > currentCapacity)
                ZeroCommandRange(currentCapacity, newCapacity - currentCapacity);
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
        private GPUIndirectRenderCommand? ConvertToGPUCommand(RenderInfo renderInfo, IRenderCommandMesh command, XRMesh? mesh, XRMaterial? material, uint submeshLocalIndex)
        {
            if (mesh is null || material is null)
                return null;

            GetOrCreateMeshID(mesh, out uint meshID);
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
                Reserved0 = 0,
                Reserved1 = 0
            };

            // Bounds: world-space (center + radius), conservative for non-uniform scale.
            SetWorldSpaceBoundingSphere(ref gpuCommand, mesh.Bounds, modelMatrix);

            gpuCommand.RenderDistance = command.RenderDistance.ClampMin(0.0f);

            uint flags = 0;
            if (renderInfo is RenderInfo3D info3d && command is RenderCommandMesh3D)
            {
                if (info3d.CastsShadows)
                    flags |= (uint)GPUIndirectRenderFlags.CastShadow;
                if (info3d.ReceivesShadows)
                    flags |= (uint)GPUIndirectRenderFlags.ReceiveShadows;

                // LayerMask is consumed by GPU culling paths (GPURenderCulling*.comp).
                gpuCommand.LayerMask = 1u << info3d.Layer;
            }

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

                    GetOrCreateMeshID(mesh, out uint newMeshID);
                    GetOrCreateMaterialID(material, out uint newMaterialID);

                    if (!_atlasMeshOffsets.ContainsKey(mesh))
                    {
                        string meshLabel = EnsureMeshDebugLabel(mesh, meshCmd.Mesh, renderInfo, subMeshIndex);
                        if (!EnsureSubmeshInAtlas(mesh, newMeshID, meshLabel, out var atlasFailure))
                        {
                            atlasFailure ??= "atlas registration failed";
                            RecordUnsupportedMesh(mesh, meshLabel, atlasFailure);
                            RemoveMeshCommandIndices(meshCmd, indices);
                            return true;
                        }
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
                    updated.Reserved1 = index;

                    uint flags = 0;
                    if (renderInfo is RenderInfo3D info3d)
                    {
                        if (info3d.CastsShadows)
                            flags |= (uint)GPUIndirectRenderFlags.CastShadow;
                        if (info3d.ReceivesShadows)
                            flags |= (uint)GPUIndirectRenderFlags.ReceiveShadows;

                        updated.LayerMask = 1u << info3d.Layer;
                    }
                    updated.Flags = flags;

                    if (existing.MeshID != newMeshID)
                    {
                        IncrementAtlasMeshRefCount(mesh);
                        DecrementAtlasMeshRefCount(existing.MeshID, "TryUpdateMeshCommand(mesh changed)");
                    }

                    if (!existing.Equals(updated))
                    {
                        UpdatingCommandsBuffer.SetDataRawAtIndex(index, updated);
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

            (uint x, uint y, uint z) = ComputeDispatch.ForCommands(Math.Max(commandCount, 1u));
            program.DispatchCompute(x, y, z, EMemoryBarrierMask.ShaderStorage);
        }

        #endregion
    }
}
