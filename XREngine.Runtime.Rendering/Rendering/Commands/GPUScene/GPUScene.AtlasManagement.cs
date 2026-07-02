// =====================================================================================
// GPUScene.AtlasManagement.cs - Atlas build / upload / streaming / migration management.
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
            // Self-locking: external callers (e.g. GPURenderPassCollection.MakeIndirectRenderer)
            // race with Add(RenderInfo) on the legacy mirror state mutated by
            // SyncLegacyDynamicAtlasState. _lock is reentrant so internal callers already
            // holding it are unaffected.
            using (_lock.EnterScope())
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
            // Push only the single touched entry, not the whole buffer.
            // LodTableBuffer is touched once per logical-mesh-state update; pushing the
            // entire buffer here was the post-O-11 dominant PushSubData offender
            // (450 calls/sec Ã— full 3072-byte upload = ~1.4 MB/sec of redundant traffic).
            // GLDataBuffer.PushSubData falls back to a full PushData when the buffer
            // just grew, so this is safe after EnsureLodTableCapacity.
            uint elementSize = LODTableBuffer.ElementSize;
            LODTableBuffer.PushSubData((int)(state.LogicalMeshId * elementSize), elementSize);
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

        private const float DefaultLod0MinProjectedRadiusPixels = 128.0f;
        private const float MinimumDefaultLodMinProjectedRadiusPixels = 16.0f;

        private static List<(XRMesh mesh, float minProjectedRadiusPixels)> BuildFallbackLodSet(XRMesh mesh)
            => [(mesh, 0.0f)];

        private static List<(XRMesh mesh, float minProjectedRadiusPixels)> CollectRenderableLodSet(RenderableMesh renderable, uint submeshIndex, XRMesh fallbackMesh)
        {
            List<(XRMesh mesh, float minProjectedRadiusPixels)> lodMeshes = [];
            foreach (RenderableMesh.RenderableLOD lod in renderable.GetLodSnapshot())
            {
                var submeshes = lod.Renderer.GetMeshes();
                if (submeshIndex >= (uint)submeshes.Length)
                    continue;

                XRMesh? lodMesh = submeshes[submeshIndex].mesh;
                if (lodMesh is null)
                    continue;

                lodMeshes.Add((lodMesh, ResolveMinProjectedRadiusPixels(lod, lodMeshes.Count)));
                if (lodMeshes.Count >= MaxLogicalMeshLodCount)
                    break;
            }

            if (lodMeshes.Count == 0)
                lodMeshes.Add((fallbackMesh, 0.0f));

            int lastIndex = lodMeshes.Count - 1;
            lodMeshes[lastIndex] = (lodMeshes[lastIndex].mesh, 0.0f);
            return lodMeshes;
        }

        private static float ResolveMinProjectedRadiusPixels(RenderableMesh.RenderableLOD lod, int lodLevel)
            => float.IsFinite(lod.MinProjectedScreenRadiusPixels)
                ? MathF.Max(0.0f, lod.MinProjectedScreenRadiusPixels)
                : GetDefaultMinProjectedRadiusPixels(lodLevel);

        private static float GetDefaultMinProjectedRadiusPixels(int lodLevel)
        {
            float threshold = DefaultLod0MinProjectedRadiusPixels;
            for (int i = 0; i < lodLevel; i++)
                threshold *= 0.5f;

            return MathF.Max(MinimumDefaultLodMinProjectedRadiusPixels, threshold);
        }

        private bool TryPopulateLogicalMeshState(LogicalMeshState state, IEnumerable<(XRMesh mesh, float minProjectedRadiusPixels)> lodMeshes, string meshLabel, out string? failureReason)
            => TryPopulateLogicalMeshState(state, lodMeshes, meshLabel, null, out failureReason);

        private bool TryPopulateLogicalMeshState(LogicalMeshState state, IEnumerable<(XRMesh mesh, float minProjectedRadiusPixels)> lodMeshes, string meshLabel, XRMesh? requiredResidentMesh, out string? failureReason)
        {
            failureReason = null;
            state.DebugLabel = meshLabel;
            List<(XRMesh mesh, float minProjectedRadiusPixels)> levels = [];
            foreach ((XRMesh mesh, float minProjectedRadiusPixels) in lodMeshes)
            {
                if (mesh is null)
                    continue;

                levels.Add((mesh, minProjectedRadiusPixels));
                if (levels.Count >= MaxLogicalMeshLodCount)
                    break;
            }

            if (levels.Count == 0)
            {
                failureReason = "no valid LOD meshes were provided";
                return false;
            }

            int lastIndex = levels.Count - 1;
            levels[lastIndex] = (levels[lastIndex].mesh, 0.0f);

            uint[] previousMeshIds = new uint[MaxLogicalMeshLodCount];
            Array.Copy(state.MeshIds, previousMeshIds, state.MeshIds.Length);
            int previousRefCount = state.ReferenceCount;

            uint[] newMeshIds = new uint[MaxLogicalMeshLodCount];
            XRMesh?[] newMeshes = new XRMesh?[MaxLogicalMeshLodCount];
            float[] newMinProjectedRadiusPixels = new float[MaxLogicalMeshLodCount];

            bool deferNonEssentialLods = RuntimeEngine.Rendering.Settings.StreamMeshLodsOnDemand && levels.Count > 1;

            for (int i = 0; i < levels.Count; i++)
            {
                XRMesh mesh = levels[i].mesh;
                newMeshes[i] = mesh;
                newMinProjectedRadiusPixels[i] = i == lastIndex ? 0.0f : MathF.Max(0.0f, levels[i].minProjectedRadiusPixels);

                // LOD0 is the mandatory-resident fallback level; the command's own mesh must
                // also stay resident because indirect draws reference it before (or without)
                // the GPU LOD-select rewrite. Levels already resident stay resident so
                // re-registration does not thrash atlas contents.
                bool alreadyResident = i < state.MeshIds.Length
                    && state.MeshIds[i] != 0
                    && ReferenceEquals(state.Meshes[i], mesh);
                bool mustBeResident = i == 0
                    || !deferNonEssentialLods
                    || alreadyResident
                    || ReferenceEquals(mesh, requiredResidentMesh);

                if (!mustBeResident)
                {
                    // Deferred level: MeshIds stays 0 so GPURenderLODSelect clamps selection
                    // to the nearest resident level and raises a LODRequestBuffer bit that
                    // ServiceLodStreamingRequests turns into a RequestLODLoad.
                    continue;
                }

                GetOrCreateMeshID(mesh, out uint meshId);
                string lodLabel = levels.Count == 1 ? meshLabel : $"{meshLabel} LOD{i}";
                if (!EnsureSubmeshInAtlas(mesh, meshId, lodLabel, out failureReason))
                    return false;

                newMeshIds[i] = meshId;
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
            Array.Clear(state.MinProjectedRadiusPixels, 0, state.MinProjectedRadiusPixels.Length);
            Array.Copy(newMeshIds, state.MeshIds, newMeshIds.Length);
            Array.Copy(newMeshes, state.Meshes, newMeshes.Length);
            Array.Copy(newMinProjectedRadiusPixels, state.MinProjectedRadiusPixels, newMinProjectedRadiusPixels.Length);
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

            List<(XRMesh mesh, float minProjectedRadiusPixels)> lodMeshes;
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

            if (!TryPopulateLogicalMeshState(state, lodMeshes, meshLabel, mesh, out failureReason))
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
                    Debug.MeshesWarning($"Mesh '{meshLabel}' triangle index count {indices.Length} is not divisible by 3; trailing vertices will be ignored.");

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
            MarkMeshDataDirty(meshID);
            SetEmptyMeshletRange(meshID, 0UL);
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

                FlushMeshDataDirtyRange();

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
            // Push only the single touched entry. Full-buffer push here was a
            // streaming-residency-rate PushSubData offender post-O-11.
            // GLDataBuffer.PushSubData falls back to a full PushData when the
            // buffer just grew, so this is safe after EnsureMeshDataCapacity.
            uint mdbEntrySize = MeshDataBuffer.ElementSize;
            MeshDataBuffer.PushSubData((int)(meshID * mdbEntrySize), mdbEntrySize);
            SetEmptyMeshletRange(meshID, 0UL);
            FlushMeshletRangeDirtyRange();
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
            // Push only the single touched entry; see note in UpdateStreamingMesh.
            uint mdbEntrySizeU = MeshDataBuffer.ElementSize;
            MeshDataBuffer.PushSubData((int)(meshID * mdbEntrySizeU), mdbEntrySizeU);
            ClearMeshletRange(meshID);
            FlushMeshletRangeDirtyRange();
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
                    Debug.MeshesWarning($"[GPUScene] Failed to migrate mesh '{meshLabel}' from {fromTier} to {toTier}: {failureReason}");
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
            bool grew = false;
            if (state.Positions!.ElementCount < needed)
            {
                state.Positions.Resize((uint)XRMath.NextPowerOfTwo(needed));
                grew = true;
            }
            if (state.Normals!.ElementCount < needed)
            {
                state.Normals.Resize((uint)XRMath.NextPowerOfTwo(needed));
                grew = true;
            }
            if (state.Tangents!.ElementCount < needed)
            {
                state.Tangents.Resize((uint)XRMath.NextPowerOfTwo(needed));
                grew = true;
            }
            if (state.UV0!.ElementCount < needed)
            {
                state.UV0.Resize((uint)XRMath.NextPowerOfTwo(needed));
                grew = true;
            }

            // A Resize invalidates GPU-side storage on next push (PushSubData will
            // fall back to a full PushData when dataLength > _lastPushedLength).
            // Force a full re-upload by resetting our high-water mark so the next
            // rebuild pushes the full appended range and the GPU PushData covers it.
            if (grew)
                state.LastUploadedVertexCount = 0;
        }

        private static void PushVertexRange(XRDataBuffer buffer, int firstVertex, int newVertexCount)
        {
            if (newVertexCount <= firstVertex)
                return;
            uint elemSize = buffer.ElementSize;
            int offset = firstVertex * (int)elemSize;
            uint length = (uint)(newVertexCount - firstVertex) * elemSize;
            // When firstVertex == 0 and the buffer grew, GLDataBuffer.PushSubData
            // detects dataLength > _lastPushedLength and falls back to a full PushData,
            // which is the correct realloc+upload. For subsequent stable-capacity
            // rebuilds, NamedBufferSubData uploads only the appended tail.
            buffer.PushSubData(offset, length);
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
            using (_lock.EnterScope())
            {
                RebuildAtlasIfDirty(EAtlasTier.Static);
                RebuildAtlasIfDirty(EAtlasTier.Dynamic);
                RebuildAtlasIfDirty(EAtlasTier.Streaming);
            }
        }

        public void RebuildAtlasIfDirty(EAtlasTier tier)
        {
            // Self-locking for the same reason as EnsureAtlasBuffers. Reentrant.
            using (_lock.EnterScope())
            {
            AtlasTierState state = GetTierState(tier);
            if (!state.Dirty)
                return;

            EnsureAtlasBuffers(tier);

            // Grow buffers to required counts using power-of-two capacity so that
            // routine append (next submesh added to atlas) does NOT re-grow every
            // frame. Resizing to an exact count would invalidate GPU storage on
            // every append (PushSubData falls back to a full PushData when
            // dataLength > _lastPushedLength), turning the per-frame appended-tail
            // upload into a per-frame full-atlas re-upload â€” the root cause of the
            // PushSubData flood identified in Â§5.5 of the perf-debug plan.
            // VerifyBufferLengths/EnsureAtlasBuffers already use NextPowerOfTwo;
            // this path must agree with them.
            VerifyBufferLengths(state, state.VertexCount);
            if (state.Positions is null || state.Normals is null || state.Tangents is null || state.UV0 is null)
                return;

            // Push only the appended vertex range. Per-attribute subrange push:
            //   offset = lastUploaded * elementSize, length = (newCount - lastUploaded) * elementSize
            // After a grow, lastUploaded was reset to 0 so the full buffer is pushed
            // (which inside GLDataBuffer becomes a single full PushData realloc + upload).
            int lastV = state.LastUploadedVertexCount;
            int newV = state.VertexCount;
            if (newV > lastV)
            {
                PushVertexRange(state.Positions, lastV, newV);
                PushVertexRange(state.Normals, lastV, newV);
                PushVertexRange(state.Tangents, lastV, newV);
                PushVertexRange(state.UV0, lastV, newV);
                state.LastUploadedVertexCount = newV;
            }

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

                    uint priorIndexCapacity = state.Indices.ElementCount;
                    if (!TryPrepareAtlasIndexBuffer(state, requiredIndices))
                    {
                        Debug.MeshesWarning($"[GPUScene] Failed to prepare {GetTierLabel(tier)} atlas index buffer; skipping atlas upload to avoid memory corruption.");
                        return;
                    }
                    if (state.Indices.ElementCount > priorIndexCapacity)
                    {
                        // Buffer grew; GPU storage must be reallocated on next push.
                        // Force full re-upload of all triangles.
                        state.LastUploadedIndexCount = 0;
                    }

                    uint capacity = state.Indices.ElementCount;
                    int lastIdxCount = state.LastUploadedIndexCount;
                    int firstTriangle = lastIdxCount / 3;
                    uint writeIndex = (uint)lastIdxCount;
                    bool overflow = false;

                    for (int i = firstTriangle; i < faceSnapshot.Length; ++i)
                    {
                        if (writeIndex + 2 >= capacity)
                        {
                            Debug.MeshesWarning($"[GPUScene] Atlas index buffer overflow when rebuilding atlas (capacity={capacity}, required={requiredIndices}).");
                            overflow = true;
                            break;
                        }

                        var tri = faceSnapshot[i];
                        state.Indices.SetDataRawAtIndex(writeIndex++, (uint)tri.Point0);
                        state.Indices.SetDataRawAtIndex(writeIndex++, (uint)tri.Point1);
                        state.Indices.SetDataRawAtIndex(writeIndex++, (uint)tri.Point2);
                    }

                    state.IndexCount = (int)writeIndex;

                    // Push only the appended index range. After a grow, lastIdxCount==0
                    // and PushSubData(0, byteLength) becomes a full PushData (correct).
                    uint pushOffset = (uint)lastIdxCount * (uint)sizeof(uint);
                    uint pushLength = (writeIndex - (uint)lastIdxCount) * (uint)sizeof(uint);
                    if (pushLength > 0)
                        state.Indices.PushSubData((int)pushOffset, pushLength);

                    if (!overflow)
                        state.LastUploadedIndexCount = (int)writeIndex;
                }
                else
                {
                    state.IndexCount = 0;
                    state.LastUploadedIndexCount = 0;
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
                Debug.MeshesWarning($"[GPUScene] AtlasRebuilt event handler failed: {ex.Message}");
            }
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
                MarkMeshDataDirty(meshID);
                if (tier == EAtlasTier.Streaming)
                    SetEmptyMeshletRange(meshID, 0UL);
                else
                    EnsureMeshletRangeForMesh(meshID, mesh);
            }
            FlushMeshDataDirtyRange();
        }

    }
}
