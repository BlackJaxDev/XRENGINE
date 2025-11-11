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
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Commands
{
    /// <summary>
    /// Holds all commands for the current scene, which the GPU will cull and render using <see cref="GPURenderPassCollection"/>."/>
    /// </summary>
    public class GPUScene : XRBase
    {
        private struct MeshInfo
        {
            public uint IndexCount;
            public uint FirstIndex;
            public uint BaseVertex;
            //public XRMesh SourceMesh;
            //public uint MaterialID;
            //public Matrix4x4 Transform;
        }

        // --- Mesh Atlas State ----------------------------------------------------
        // Consolidated (batched) vertex + index data for all meshes referenced by commands.
        // Currently linear appended; future optimization could bin by vertex format / material.
        private XRDataBuffer? _atlasPositions;     // Position vec3
        private XRDataBuffer? _atlasNormals;       // Normal vec3
        private XRDataBuffer? _atlasTangents;      // Tangent vec4
        private XRDataBuffer? _atlasUV0;           // UV0 vec2
        private XRDataBuffer? _atlasIndices;       // Element indices
        private bool _atlasDirty = false;          // Indicates atlas rebuild needed after adds/removes
        private int _atlasVertexCount = 0;        // Running vertex count for packing
        private int _atlasIndexCount = 0;         // Running index count for packing
        private readonly Dictionary<XRMesh, (int firstVertex, int firstIndex, int indexCount)> _atlasMeshOffsets = [];
        private readonly List<IndexTriangle> _indirectFaceIndices = [];

        public List<IndexTriangle> IndirectFaceIndices => _indirectFaceIndices;
        public int AtlasVertexCount => _atlasVertexCount;

        public XRDataBuffer? AtlasPositions => _atlasPositions;
        public XRDataBuffer? AtlasNormals => _atlasNormals;
        public XRDataBuffer? AtlasTangents => _atlasTangents;
        public XRDataBuffer? AtlasUV0 => _atlasUV0;
        public XRDataBuffer? AtlasIndices => _atlasIndices;

        /// <summary>
        /// Maps XRMaterial -> ID and reverse ID -> XRMaterial to resolve materials during rendering.
        /// </summary>
        private readonly ConcurrentDictionary<XRMaterial, uint> _materialIDMap = new();
        private readonly ConcurrentDictionary<uint, XRMaterial> _idToMaterial = new();
        private readonly ConcurrentDictionary<uint, XRMesh> _idToMesh = new();
        private uint _nextMaterialID = 1;
        private int _materialDebugLogBudget = 16;
    private int _commandBuildLogBudget = 12;
    private int _commandRoundtripLogBudget = 8;
    private int _commandRoundtripMismatchLogBudget = 4;

        private static bool IsGpuSceneLoggingEnabled()
            => Engine.UserSettings?.EnableGpuIndirectDebugLogging ?? false;

        private static void SceneLog(string message, params object[] args)
        {
            if (!IsGpuSceneLoggingEnabled())
                return;

            Debug.Out(message, args);
        }

        private static void SceneLog(FormattableString message)
        {
            if (!IsGpuSceneLoggingEnabled())
                return;

            Debug.Out(message.ToString());
        }

        /// <summary>
        /// Exposes a read-only view of the current material ID map (ID -> XRMaterial).
        /// </summary>
        public IReadOnlyDictionary<uint, XRMaterial> MaterialMap => _idToMaterial;

        /// <summary>
        /// Attempts to get a material by its ID.
        /// </summary>
        public bool TryGetMaterial(uint id, out XRMaterial? material)
        {
            bool ok = _idToMaterial.TryGetValue(id, out var mat);
            material = ok ? mat : null;
            return ok;
        }

        /// <summary>
        /// Marks the atlas as dirty so it will be rebuilt before next render if needed.
        /// </summary>
        private void MarkAtlasDirty()
            => _atlasDirty = true;

        /// <summary>
        /// Ensure atlas buffers exist (minimal allocation on first use).
        /// </summary>
        public void EnsureAtlasBuffers()
        {
            if (_atlasPositions is null)
            {
                _atlasPositions = new XRDataBuffer(ECommonBufferType.Position.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 3, false, false)
                {
                    Name = "MeshAtlas_Positions",
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false,
                    BindingIndexOverride = 0
                };
                //_atlasPositions.Generate();
            }
            if (_atlasNormals is null)
            {
                _atlasNormals = new XRDataBuffer(ECommonBufferType.Normal.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 3, false, false)
                {
                    Name = "MeshAtlas_Normals",
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false,
                    BindingIndexOverride = 1
                };
                //_atlasNormals.Generate();
            }
            if (_atlasTangents is null)
            {
                _atlasTangents = new XRDataBuffer(ECommonBufferType.Tangent.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 4, false, false)
                {
                    Name = "MeshAtlas_Tangents",
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false,
                    BindingIndexOverride = 2
                };
                //_atlasTangents.Generate();
            }
            if (_atlasUV0 is null)
            {
                _atlasUV0 = new XRDataBuffer($"{ECommonBufferType.TexCoord}0", EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 2, false, false)
                {
                    Name = "MeshAtlas_UV0",
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false,
                    BindingIndexOverride = 3
                };
                //_atlasUV0.Generate();
            }
            if (_atlasIndices is null)
            {
                _atlasIndices = new XRDataBuffer("MeshAtlas_Indices", EBufferTarget.ElementArrayBuffer, 0, EComponentType.UInt, 1, false, true)
                {
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false,
                    PadEndingToVec4 = false
                };
                //_atlasIndices.Generate();
            }
            _atlasPositions!.BindingIndexOverride ??= 0;
            _atlasNormals!.BindingIndexOverride ??= 1;
            _atlasTangents!.BindingIndexOverride ??= 2;
            _atlasUV0!.BindingIndexOverride ??= 3;
        }

        /// <summary>
        /// Incrementally appends mesh geometry into atlas client-side buffers.
        /// For now we only pack index offsets (MeshDataBuffer keeps logical mapping). Vertex packing TODO.
        /// </summary>
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
                uv0[v] = mesh.GetTexCoord(0, v);
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
                    ? _indirectFaceIndices.ToArray()
                    : Array.Empty<IndexTriangle>();

                int requiredIndices = faceSnapshot.Length * 3;
                if (requiredIndices > 0)
                {
                    _atlasIndexCount = requiredIndices;

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

        public void Initialize()
        {
            _meshDataBuffer?.Destroy();
            _meshDataBuffer = MakeMeshDataBuffer();

            _allLoadedCommandsBuffer?.Destroy();
            _allLoadedCommandsBuffer = MakeCommandsInputBuffer();
        }

        public void Destroy()
        {
            _meshDataBuffer?.Destroy();
            _meshDataBuffer = null;
            _allLoadedCommandsBuffer?.Destroy();
            _allLoadedCommandsBuffer = null;
            _meshIDMap.Clear();
            _materialIDMap.Clear();
            _idToMaterial.Clear();
            _idToMesh.Clear();
            _nextMeshID = 1;
            _nextMaterialID = 1;
            _totalCommandCount = 0;
            _bounds = new AABB();
            _meshlets.Clear();
            _commandIndicesPerMeshCommand.Clear();
            _commandIndexLookup.Clear();
            _meshToIndexRemap.Clear();
        }

        private static XRDataBuffer MakeCommandsInputBuffer()
        {
            var buffer = new XRDataBuffer(
                $"RenderCommandsBuffer",
                EBufferTarget.ShaderStorageBuffer,
                MinCommandCount,
                EComponentType.Float,
                CommandFloatCount, // 32 floats (128 bytes)
                false,
                false)
            {
                Usage = EBufferUsage.DynamicCopy,
                DisposeOnPush = false,
                Resizable = true
            };
            //buffer.Generate();
            return buffer;
        }

        private const uint MinMeshDataEntries = 16; // start with small capacity; grows with highest flattened submesh ID
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
            //buffer.Generate();
            return buffer;
        }

        /// <summary>
        /// The initial size of the command buffer. It will grow or shrink as needed at powers of two.
        /// </summary>
        public const uint MinCommandCount = 8;
        public const int CommandFloatCount = 32; // Updated: compact command (128 bytes)

        private readonly ConcurrentDictionary<XRMesh, uint> _meshIDMap = new();
        private uint _nextMeshID = 1;
        private readonly Lock _lock = new();
        private readonly ConcurrentDictionary<XRMesh, string> _meshDebugLabels = new();
        private readonly ConcurrentDictionary<XRMesh, string> _unsupportedMeshMessages = new();

        private XRDataBuffer? _meshDataBuffer;
        /// <summary>
        /// This buffer stores mesh index and vertex data for all submeshes used in this scene, in reference to the global VAO.
        /// Layout per entry (uint4): [IndexCount, FirstIndex, FirstVertex, Flags]. There are currently no flags defined.
        /// </summary>
        public XRDataBuffer MeshDataBuffer => _meshDataBuffer ??= MakeMeshDataBuffer();

        public struct MeshDataEntry
        {
            public uint IndexCount;
            public uint FirstIndex;
            public uint FirstVertex;
            public uint BaseInstance;
        }

        private XRDataBuffer? _allLoadedCommandsBuffer;
        /// <summary>
        /// This buffer holds all commands that are relevant to this scene.
        /// A render pipeline will take these and execute them with GPU culling using <see cref="GPURenderPassCollection"/>."/>
        /// </summary>
        public XRDataBuffer AllLoadedCommandsBuffer => _allLoadedCommandsBuffer ??= MakeCommandsInputBuffer();

        private readonly MeshletCollection _meshlets = new();
        public MeshletCollection Meshlets => _meshlets;

        private AABB _bounds;
        public AABB Bounds
        {
            get => _bounds;
            set => SetField(ref _bounds, value);
        }

        private uint _totalCommandCount = 0;
        /// <summary>
        /// The literal amount of commands currently in the buffer.
        /// Each command represents one submesh - a single <see cref="IRenderCommandMesh"> may produce multiple commands if it has multiple submeshes.
        /// </summary>
        public uint TotalCommandCount
        {
            get => _totalCommandCount;
            private set => SetField(ref _totalCommandCount, value);
        }

        /// <summary>
        /// The amount of commands the buffer can currently hold without resizing.
        /// </summary>
        public uint AllocatedMaxCommandCount => AllLoadedCommandsBuffer.ElementCount;

        /// <summary>
        /// Tracks all GPU command indices produced per mesh command (multi-submesh support).
        /// </summary>
        private readonly Dictionary<IRenderCommandMesh, List<uint>> _commandIndicesPerMeshCommand = [];

        /// <summary>
        /// Adds a render command to the collection.
        /// </summary>
        public void Add(RenderInfo renderInfo)
        {
            if (renderInfo is null || renderInfo.RenderCommands.Count == 0)
                return;

            if (_totalCommandCount == uint.MaxValue)
            {
                Debug.LogWarning($"Command buffer full. Cannot add more commands.");
                return;
            }

            using (_lock.EnterScope())
            {
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

                    var subMeshes = meshCmd.Mesh?.GetMeshes();
                    if (subMeshes is null || subMeshes.Length == 0)
                    {
                        SceneLog($"Skipping adding mesh command with no submeshes. Mesh={(meshCmd.Mesh != null ? "present" : "null")} SubMeshes={(subMeshes != null ? $"empty array (length {subMeshes.Length})" : "null")}");
                        if (meshCmd.Mesh != null)
                        {
                            SceneLog($"  Mesh details: Name={meshCmd.Mesh.Mesh?.Name ?? "<null>"}, Submeshes.Count={meshCmd.Mesh.Submeshes.Count}");
                        }
                        continue;
                    }

                    // Ensure we have enough space for ALL submeshes of this command
                    VerifyCommandBufferSize(_totalCommandCount + (uint)subMeshes.Length);

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
                            SceneLog($"Skipping adding mesh command submesh {subMeshIndex} due to null mesh.");
                            continue;
                        }

                        XRMaterial? m = meshCmd.MaterialOverride ?? mat;
                        if (m is null)
                        {
                            SceneLog($"Skipping adding mesh command submesh {subMeshIndex} due to null material.");
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

                        uint index = _totalCommandCount++;
                        if (meshCmd.GPUCommandIndex == uint.MaxValue || indices.Count == 0)
                            meshCmd.GPUCommandIndex = index; // Store first index for legacy single-index usage

                        indices.Add(index);
                        _commandIndexLookup.Add(index, (meshCmd, subMeshIndex));
                        AllLoadedCommandsBuffer.SetDataRawAtIndex(index, gpuCommand.Value);

                        if (IsGpuSceneLoggingEnabled())
                        {
                            if (_commandBuildLogBudget > 0 && Interlocked.Decrement(ref _commandBuildLogBudget) >= 0)
                            {
                                SceneLog($"[GPUScene/Build] idx={index} mesh={gpuCommand.Value.MeshID} material={gpuCommand.Value.MaterialID} pass={gpuCommand.Value.RenderPass} instances={gpuCommand.Value.InstanceCount}");
                            }

                            GPUIndirectRenderCommand roundTrip = AllLoadedCommandsBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(index);
                            bool matches = roundTrip.MeshID == gpuCommand.Value.MeshID
                                && roundTrip.MaterialID == gpuCommand.Value.MaterialID
                                && roundTrip.RenderPass == gpuCommand.Value.RenderPass;

                            if (!matches)
                            {
                                if (_commandRoundtripMismatchLogBudget > 0 && Interlocked.Decrement(ref _commandRoundtripMismatchLogBudget) >= 0)
                                    Debug.LogWarning($"[GPUScene/RoundTrip] mismatch idx={index} mesh(write={gpuCommand.Value.MeshID}/read={roundTrip.MeshID}) material(write={gpuCommand.Value.MaterialID}/read={roundTrip.MaterialID}) pass(write={gpuCommand.Value.RenderPass}/read={roundTrip.RenderPass})");
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
                    // Upload modified command entries (full buffer for simplicity)
                    AllLoadedCommandsBuffer.PushSubData();
                    SceneLog($"GPUScene.Add: Added commands, total now {_totalCommandCount} in CommandsInputBuffer");
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
            List<XRMesh> meshesToAdjust = new(_atlasMeshOffsets.Keys);
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
            failureReason = null;
            //Ensure mesh geometry recorded in atlas buffers: vertex + index data
            //Increasees vertex & index counts and adds offsets to _atlasMeshOffsets
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

            //Update length of mesh data buffer if needed - this provides indices into the atlas data
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

        private readonly Dictionary<uint, (IRenderCommandMesh command, int subMeshIndex)> _commandIndexLookup = [];
        private readonly Dictionary<XRMesh, uint> _meshToIndexRemap = []; // retained for future reverse lookups (unused by atlas sizing)
        private bool _meshDataDirty = false; // tracks pending GPU upload for mesh metadata

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
                    return false;

                string meshLabel = _meshDebugLabels.TryGetValue(mesh, out var storedLabel)
                    ? storedLabel
                    : mesh.Name ?? $"Mesh_{meshID}";

                if (!AddSubmeshToAtlas(mesh, meshID, meshLabel, out var hydrationFailure))
                {
                    hydrationFailure ??= "atlas registration failed during lookup";
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
            return false;
        }

        // Ensures MeshDataBuffer has capacity for at least requiredEntries elements (each element is uint4 for a flattened submesh).
        private void EnsureMeshDataCapacity(uint requiredEntries)
        {
            var buffer = MeshDataBuffer; // lazy create
            if (requiredEntries <= buffer.ElementCount)
                return;

            uint newCapacity = XRMath.NextPowerOfTwo(requiredEntries).ClampMin(MinMeshDataEntries);
            SceneLog($"Resizing MeshDataBuffer from {buffer.ElementCount} to {newCapacity} (need {requiredEntries}).");
            buffer.Resize(newCapacity);
        }

        public void Remove(RenderInfo info)
        {
            if (info is null || info.RenderCommands.Count == 0 || _totalCommandCount == 0)
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
                VerifyCommandBufferSize(_totalCommandCount);
                if (anyRemoved)
                    AllLoadedCommandsBuffer.PushSubData();
            }
        }

        private void RemoveCommandAtIndex(uint targetIndex)
        {
            if (targetIndex >= _totalCommandCount)
            {
                Debug.LogWarning($"Invalid command index {targetIndex} for removal. Total commands: {_totalCommandCount}");
                return;
            }

            uint lastIndex = _totalCommandCount - 1;
            if (targetIndex < lastIndex)
            {
                GPUIndirectRenderCommand lastCommand = AllLoadedCommandsBuffer.GetDataRawAtIndex<GPUIndirectRenderCommand>(lastIndex);
                AllLoadedCommandsBuffer.SetDataRawAtIndex(targetIndex, lastCommand);

                RemoveSubmeshFromAtlasAt(targetIndex);

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
            }
            else
            {
                _commandIndexLookup.Remove(lastIndex);
            }

            _commandIndexLookup.Remove(targetIndex);
            --_totalCommandCount;
        }

        private void RemoveSubmeshFromAtlasAt(uint targetIndex)
        {
            if (!_commandIndexLookup.TryGetValue(targetIndex, out var targetCommand))
                return;

            var submeshIndex = targetCommand.subMeshIndex;
            var command = targetCommand.command;

            var meshes = command.Mesh?.GetMeshes();
            if (meshes is null || submeshIndex < 0 || submeshIndex >= meshes.Length)
                return;

            var mesh = meshes[submeshIndex].mesh;
            if (mesh is null)
                return;
            
            RemoveSubmeshFromAtlas(mesh);
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

        private GPUIndirectRenderCommand? ConvertToGPUCommand(RenderInfo renderInfo, IRenderCommandMesh command, XRMesh? mesh, XRMaterial? material, uint submeshLocalIndex)
        {
            if (mesh is null || material is null)
                return null;

            GetOrCreateMeshID(mesh, out uint meshID);
            GetOrCreateMaterialID(material, out uint materialID);

            var gpuCommand = new GPUIndirectRenderCommand
            {
                MeshID = meshID,
                SubmeshID = (meshID << 16) | (submeshLocalIndex & 0xFFFF),
                MaterialID = materialID,
                RenderPass = (uint)command.RenderPass,
                InstanceCount = command.Instances == 0 ? 1u : command.Instances,
                LayerMask = 0xFFFFFFFF,
                RenderDistance = 0f,
                WorldMatrix = command.WorldMatrix,
                Flags = 0,
                LODLevel = 0,
                ShaderProgramID = 0,
                Reserved0 = 0,
                Reserved1 = 0
            };

            if (command.Mesh != null)
            {
                AABB? bounds = command.Mesh?.Mesh?.Bounds;
                if (bounds.HasValue)
                {
                    var center = bounds.Value.Center;
                    var radius = bounds.Value.HalfExtents.Length();
                    gpuCommand.SetBoundingSphere(center, radius);
                }
                else
                    gpuCommand.SetBoundingSphere(Vector3.Zero, 0.0f);
            }

            gpuCommand.RenderDistance = command.RenderDistance.ClampMin(0.0f);

            uint flags = 0;
            if (renderInfo is RenderInfo3D info3d && command is RenderCommandMesh3D)
            {
                if (info3d.CastsShadows)
                    flags |= (uint)GPUIndirectRenderFlags.CastShadow;
                if (info3d.ReceivesShadows)
                    flags |= (uint)GPUIndirectRenderFlags.ReceiveShadows;
            }

            gpuCommand.Flags = flags;
            return gpuCommand;
        }

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

        /// <summary>
        /// Gets or creates a unique mesh ID for the given mesh.
        /// Incremental IDs start at 1. Returns true if the mesh was already known, false if newly added.
        /// </summary>
        /// <param name="mesh"></param>
        /// <param name="index"></param>
        /// <returns></returns>
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
        /// Incremental IDs start at 1. Returns true if the material was already known, false if newly added.
        /// </summary>
        /// <param name="material"></param>
        /// <param name="index"></param>
        /// <returns></returns>
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