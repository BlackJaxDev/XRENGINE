using Extensions;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
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
                    DisposeOnPush = false
                };
                _atlasPositions.Generate();
            }
            if (_atlasNormals is null)
            {
                _atlasNormals = new XRDataBuffer(ECommonBufferType.Normal.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 3, false, false)
                {
                    Name = "MeshAtlas_Normals",
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false
                };
                _atlasNormals.Generate();
            }
            if (_atlasTangents is null)
            {
                _atlasTangents = new XRDataBuffer(ECommonBufferType.Tangent.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 4, false, false)
                {
                    Name = "MeshAtlas_Tangents",
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false
                };
                _atlasTangents.Generate();
            }
            if (_atlasUV0 is null)
            {
                _atlasUV0 = new XRDataBuffer($"{ECommonBufferType.TexCoord}0", EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 2, false, false)
                {
                    Name = "MeshAtlas_UV0",
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false
                };
                _atlasUV0.Generate();
            }
            if (_atlasIndices is null)
            {
                _atlasIndices = new XRDataBuffer("MeshAtlas_Indices", EBufferTarget.ElementArrayBuffer, 0, EComponentType.UInt, 1, false, true)
                {
                    Usage = EBufferUsage.StreamDraw,
                    DisposeOnPush = false,
                    PadEndingToVec4 = false
                };
                _atlasIndices.Generate();
            }
        }

        /// <summary>
        /// Incrementally appends mesh geometry into atlas client-side buffers.
        /// For now we only pack index offsets (MeshDataBuffer keeps logical mapping). Vertex packing TODO.
        /// </summary>
        private void AppendMeshToAtlas(XRMesh mesh)
        {
            //Make sure the buffers exist - positions, normals, etc
            EnsureAtlasBuffers();

            if (_atlasMeshOffsets.ContainsKey(mesh))
                return; // already packed

            int vertexCount = mesh.VertexCount;
            if (vertexCount <= 0)
                return;

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
            if (mesh.Triangles != null)
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
            else if (mesh.IndexCount > 0)
            {
                // Other primitive types not yet supported for atlas packing.
                Debug.LogWarning($"Mesh '{mesh.Name}' uses unsupported primitive topology for atlas packing.");
                return;
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
                int requiredIndices = _indirectFaceIndices.Count * 3;
                if (requiredIndices > 0)
                {
                    _atlasIndexCount = requiredIndices;
                    uint desiredCapacity = (uint)XRMath.NextPowerOfTwo(requiredIndices);
                    if (_atlasIndices.ElementCount < desiredCapacity)
                        _atlasIndices.Resize(desiredCapacity);

                    uint writeIndex = 0;
                    foreach (var tri in _indirectFaceIndices)
                    {
                        _atlasIndices.SetDataRawAtIndex(writeIndex++, (uint)tri.Point0);
                        _atlasIndices.SetDataRawAtIndex(writeIndex++, (uint)tri.Point1);
                        _atlasIndices.SetDataRawAtIndex(writeIndex++, (uint)tri.Point2);
                    }

                    uint byteLength = (uint)(requiredIndices * sizeof(uint));
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
            buffer.Generate();
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
            buffer.Generate();
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
                Debug.Out($"Adding commands for {renderInfo.Owner?.GetType().Name ?? "<null>"}");
                for (int i = 0; i < renderInfo.RenderCommands.Count; i++)
                {
                    RenderCommand command = renderInfo.RenderCommands[i];
                    if (command is not IRenderCommandMesh meshCmd)
                    {
                        Debug.Out($"Skipping adding command of type {command.GetType().Name}");
                        continue; // Only mesh commands supported
                    }

                    var subMeshes = meshCmd.Mesh?.GetMeshes();
                    if (subMeshes is null || subMeshes.Length == 0)
                    {
                        Debug.Out($"Skipping adding mesh command with no submeshes.");
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
                            Debug.Out($"Skipping adding mesh command submesh {subMeshIndex} due to null mesh.");
                            continue;
                        }

                        XRMaterial? m = meshCmd.MaterialOverride ?? mat;
                        if (m is null)
                        {
                            Debug.Out($"Skipping adding mesh command submesh {subMeshIndex} due to null material.");
                            continue;
                        }

                        var gpuCommand = ConvertToGPUCommand(renderInfo, meshCmd, mesh, m, (uint)subMeshIndex);
                        if (gpuCommand is null)
                        {
                            Debug.Out($"Skipping adding mesh command submesh {subMeshIndex} due to conversion failure.");
                            continue;
                        }

                        uint index = _totalCommandCount++;
                        if (meshCmd.GPUCommandIndex == uint.MaxValue || indices.Count == 0)
                            meshCmd.GPUCommandIndex = index; // Store first index for legacy single-index usage

                        indices.Add(index);
                        _commandIndexLookup.Add(index, (meshCmd, subMeshIndex));
                        AllLoadedCommandsBuffer.SetDataRawAtIndex(index, gpuCommand.Value);
                        anyAdded = true;

                        // Acquire or assign meshID
                        GetOrCreateMeshID(mesh, out uint meshID);
                        GetOrCreateMaterialID(mat, out uint materialID);
                        AddSubmeshToAtlas(mesh, meshID);

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
                    Debug.Out($"GPUScene.Add: Added commands, total now {_totalCommandCount} in CommandsInputBuffer");
                }
                RebuildAtlasIfDirty();
            }
        }

        private void RemoveSubmeshFromAtlas(XRMesh mesh)
        {
            if (mesh is null || !_atlasMeshOffsets.TryGetValue(mesh, out var atlas))
                return;

            //Remove data from the atlas - currently we do not compact the buffers, just adjust counts
            int indexOffset = atlas.firstIndex;
            int indexCount = atlas.indexCount;
            while (indexCount-- > 0)
                _indirectFaceIndices.RemoveAt(indexOffset);

            int vertexOffset = atlas.firstVertex;
            int vertexCount = mesh.VertexCount;

            //TODO: memory management to reuse freed space. We'll just adjust counts for now.
            _atlasVertexCount -= mesh.VertexCount;
            _atlasIndexCount -= atlas.indexCount;
            if (_atlasVertexCount < 0)
                _atlasVertexCount = 0;
            if (_atlasIndexCount < 0)
                _atlasIndexCount = 0;

            _atlasMeshOffsets.Remove(mesh);
            MarkAtlasDirty();
        }

        private void AddSubmeshToAtlas(XRMesh mesh, uint meshID)
        {
            //Ensure mesh geometry recorded in atlas buffers: vertex + index data
            //Increasees vertex & index counts and adds offsets to _atlasMeshOffsets
            AppendMeshToAtlas(mesh);

            if (!_atlasMeshOffsets.TryGetValue(mesh, out var atlas))
            {
                Debug.LogWarning($"Atlas offsets missing for mesh '{mesh.Name}' after append.");
                return;
            }

            //Update length of mesh data buffer if needed - this provides indices into the atlas data
            EnsureMeshDataCapacity(meshID + 1); // +1 because index-based capacity
            (int vFirst, int iFirst, int iCount) = atlas;
            MeshDataEntry entry = new()
            {
                IndexCount = (uint)iCount,
                FirstIndex = (uint)iFirst,
                FirstVertex = (uint)vFirst,
                BaseInstance = 0
            };
            MeshDataBuffer.Set(meshID, entry);

            _meshDataDirty = true;
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
                AddSubmeshToAtlas(mesh, meshID);
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
            Debug.Out($"Resizing MeshDataBuffer from {buffer.ElementCount} to {newCapacity} (need {requiredEntries}).");
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
            uint nextPowerOfTwo = XRMath.NextPowerOfTwo(requiredSize).ClampMin(MinCommandCount);
            if (nextPowerOfTwo == AllLoadedCommandsBuffer.ElementCount)
                return;

            Debug.Out($"Resizing command buffer from {AllLoadedCommandsBuffer.ElementCount} to {nextPowerOfTwo}.");
            AllLoadedCommandsBuffer.Resize(nextPowerOfTwo);
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
            return contains;
        }
    }
}