// =====================================================================================
// GPUScene.AddRemove.cs - Add / Remove / Update of GPU draw commands.
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
                Debug.MeshesWarning($"Command buffer full. Cannot add more commands.");
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
                    VerifyUpdatingBufferSize(GpuDrivenValidationCapacity.Apply(UpdatingCommandCount + (uint)subMeshes.Length));

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

                        Matrix4x4 modelMatrix = meshCmd.WorldMatrixIsModelMatrix ? meshCmd.WorldMatrix : Matrix4x4.Identity;
                        uint transformId = AllocateTransformId(modelMatrix);
                        uint skinId = AllocateSkinId(false);
                        GetOrCreateMaterialID(m, out uint materialIDForState);
                        uint stateClassId = ResolveStateClassId(m, meshCmd.RenderPass, materialIDForState);
                        uint index = UpdatingCommandCount++;
                        uint boundsId = index;

                        var stageNativeRecords = CreateStageNativeDrawRecords(
                            renderInfo,
                            meshCmd,
                            mesh,
                            m,
                            meshID,
                            logicalMeshID,
                            lodCount,
                            (uint)subMeshIndex,
                            transformId,
                            skinId,
                            stateClassId,
                            boundsId);
                        if (stageNativeRecords is null)
                        {
                            ReleaseTransformId(transformId);
                            ReleaseSkinId(skinId);
                            --UpdatingCommandCount;
                            SceneLog($"Skipping mesh-command submesh {subMeshIndex} because stage-native publication failed.");
                            continue;
                        }

                        if (meshCmd.GPUCommandIndex == uint.MaxValue || indices.Count == 0)
                            meshCmd.GPUCommandIndex = index; // Store first index for legacy single-index usage

                        indices.Add(index);
                        _commandIndexLookup.Add(index, (meshCmd, subMeshIndex));

                        DrawMetadata commandValue = stageNativeRecords.Value.Metadata;
                        commandValue.DrawID = index;
                        WriteDrawMetadata(index, commandValue);
                        WriteBounds(boundsId, stageNativeRecords.Value.Bounds);
                        UpdatingTransparencyMetadataBuffer.SetDataRawAtIndex(index, GPUTransparencyMetadata.FromMaterial(m));
                        if (_useInternalBvh)
                            WriteTightCommandAabb(index, renderInfo, mesh.Bounds, modelMatrix);
                                                LodTransitionBuffer.SetDataRawAtIndex(index, default(GPULodTransitionState));
                        AcquireLogicalMeshResidency(commandValue.LogicalMeshID);

                        if (IsGpuSceneLoggingEnabled())
                        {
                            if (_commandBuildLogBudget > 0 && Interlocked.Decrement(ref _commandBuildLogBudget) >= 0)
                            {
                                SceneLog($"[GPUScene/Build] idx={index} mesh={commandValue.MeshID} material={commandValue.MaterialID} pass={commandValue.RenderPass} instances={commandValue.InstanceCount}");
                            }

                            DrawMetadata roundTrip = UpdatingDrawMetadataBuffer.GetDataRawAtIndex<DrawMetadata>(index);
                            bool matches = roundTrip.MeshID == commandValue.MeshID
                                && roundTrip.MaterialID == commandValue.MaterialID
                                && roundTrip.RenderPass == commandValue.RenderPass;

                            if (!matches)
                            {
                                if (_commandRoundtripMismatchLogBudget > 0 && Interlocked.Decrement(ref _commandRoundtripMismatchLogBudget) >= 0)
                                    Debug.MeshesWarning($"[GPUScene/RoundTrip] mismatch idx={index} mesh(write={commandValue.MeshID}/read={roundTrip.MeshID}) material(write={commandValue.MaterialID}/read={roundTrip.MaterialID}) pass(write={commandValue.RenderPass}/read={roundTrip.RenderPass})");
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
                FlushMeshDataDirtyRange();
                if (anyAdded)
                {
                    // Upload only the newly appended command range.
                    // Pushing the full buffer can hitch when high-detail meshes/submeshes stream in later.
                    uint addedCount = UpdatingCommandCount - startCommandCount;
                    FlushDrawIndexedSoARange(startCommandCount, addedCount);
                    LodTransitionBuffer.PushSubData((int)(startCommandCount * LodTransitionBuffer.ElementSize), addedCount * LodTransitionBuffer.ElementSize);
                    MarkUpdatingCommandsDirty();
                    SceneLog($"GPUScene.Add: Added commands, total now {UpdatingCommandCount} in draw-indexed streams");

                    // Mark BVH dirty so it gets rebuilt before next cull pass
                    if (_gpuBvhTree is not null)
                        MarkBvhDirty();

                    _meshletsDirty = true;
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
                Debug.MeshesWarning($"Mesh '{mesh.Name}' stored with a non-multiple-of-three index count ({indexCount}). Clamping removal to available triangles.");
                triangleCount = System.Math.Min(triangleCount + 1, state.IndirectFaceIndices.Count - triangleOffset);
            }

            if (triangleOffset < 0 || triangleOffset >= state.IndirectFaceIndices.Count)
            {
                Debug.MeshesWarning($"Atlas removal offset out of range for mesh '{mesh.Name}'. Offset={triangleOffset}, Count={triangleCount}, Total={state.IndirectFaceIndices.Count}.");
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

            // Compaction slid later vertices forward over the removed span, rewriting the front
            // of the buffer. Invalidate the upload high-water marks so the next rebuild
            // re-pushes the full (now smaller) atlas. This is correct but pessimal; future work
            // could push only [vertexOffset .. state.VertexCount).
            state.LastUploadedVertexCount = 0;
            state.LastUploadedIndexCount = 0;

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
                MarkMeshDataDirty(meshID);
                ClearMeshletRange(meshID);
                return false;
            }

            AtlasTierState state = GetTierState(tier);
            if (!state.MeshOffsets.TryGetValue(mesh, out AtlasAllocation atlas))
            {
                failureReason = "did not produce atlas offsets";
                EnsureMeshDataCapacity(meshID + 1);
                MeshDataBuffer.Set(meshID, default(MeshDataEntry));
                MarkMeshDataDirty(meshID);
                ClearMeshletRange(meshID);
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
                MarkMeshDataDirty(meshID);
                SetEmptyMeshletRange(meshID, 0UL);
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

            MarkMeshDataDirty(meshID);
            if (tier == EAtlasTier.Streaming)
                SetEmptyMeshletRange(meshID, 0UL);
            else
                EnsureMeshletRangeForMesh(meshID, mesh);
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
                    Debug.MeshesWarning($"[GPUScene] {context}: unable to resolve MeshID={meshID} for atlas refcount increment.");
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
                    Debug.MeshesWarning($"[GPUScene] {context}: unable to resolve MeshID={meshID} for atlas refcount decrement.");
                return;
            }

            if (!_atlasMeshRefCounts.TryGetValue(mesh, out int count))
            {
                if (_commandUpdateErrorLogBudget > 0 && Interlocked.Decrement(ref _commandUpdateErrorLogBudget) >= 0)
                    Debug.MeshesWarning($"[GPUScene] {context}: atlas refcount missing for mesh '{mesh.Name ?? "<unnamed>"}' (MeshID={meshID}); treating as 0.");
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
            MarkMeshDataDirty(meshID);
            ClearMeshletRange(meshID);

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
        // Dirty-range tracker for MeshDataBuffer. Updated by MarkMeshDataDirty and drained by
        // FlushMeshDataDirtyRange so the full-buffer PushSubData() pattern collapses to a
        // single contiguous range upload (extends O-11 tail-append to scattered Set sites).
        private uint _meshDataDirtyMinIndex = uint.MaxValue;
        private uint _meshDataDirtyMaxIndexExclusive = 0u;

        private void MarkMeshDataDirty(uint meshID)
        {
            if (meshID < _meshDataDirtyMinIndex)
                _meshDataDirtyMinIndex = meshID;
            uint upper = meshID + 1;
            if (upper > _meshDataDirtyMaxIndexExclusive)
                _meshDataDirtyMaxIndexExclusive = upper;
            _meshDataDirty = true;
        }

        private void FlushMeshDataDirtyRange()
        {
            if (_meshDataDirty)
            {
                var buffer = MeshDataBuffer;
                uint count = buffer.ElementCount;
                uint min = _meshDataDirtyMinIndex;
                uint maxExclusive = _meshDataDirtyMaxIndexExclusive;
                if (count > 0 && min < maxExclusive)
                {
                    if (maxExclusive > count)
                        maxExclusive = count;
                    if (min < maxExclusive)
                    {
                        uint elementSize = buffer.ElementSize;
                        buffer.PushSubData((int)(min * elementSize), (maxExclusive - min) * elementSize);
                    }
                }
                else if (count > 0)
                {
                    // Range was never narrowed (e.g. atlas-wide rebuild path). Fall back to full push
                    // so callers that flipped `_meshDataDirty` without populating a range still upload.
                    buffer.PushSubData();
                }

                _meshDataDirtyMinIndex = uint.MaxValue;
                _meshDataDirtyMaxIndexExclusive = 0u;
                _meshDataDirty = false;
            }

            FlushMeshletRangeDirtyRange();
        }

        private static void EnsureSceneBufferCapacity(XRDataBuffer buffer, uint requiredEntries, uint minimumEntries)
        {
            if (requiredEntries <= buffer.ElementCount)
                return;

            uint newCapacity = XRMath.NextPowerOfTwo(requiredEntries).ClampMin(minimumEntries);
            buffer.Resize(newCapacity);
        }

        private void EnsureMeshletRangeCapacity(uint requiredEntries)
        {
            // Capacity belongs to a coherent meshlet-buffer generation. It is sized
            // and uploaded by SwapCommandBuffers, not resized under an in-flight draw.
            _ = requiredEntries;
        }

        private void EnsureMeshletDescriptorCapacity(uint requiredEntries)
            => EnsureSceneBufferCapacity(MeshletDescriptorBuffer, requiredEntries.ClampMin(MinMeshletDescriptorEntries), MinMeshletDescriptorEntries);

        private void EnsureMeshletVertexIndexCapacity(uint requiredEntries)
            => EnsureSceneBufferCapacity(MeshletVertexIndexBuffer, requiredEntries.ClampMin(MinMeshletIndexEntries), MinMeshletIndexEntries);

        private void EnsureMeshletTriangleIndexCapacity(uint requiredEntries)
            => EnsureSceneBufferCapacity(MeshletTriangleIndexBuffer, requiredEntries.ClampMin(MinMeshletIndexEntries), MinMeshletIndexEntries);

        private static void PushBufferElementRange(XRDataBuffer buffer, uint startIndex, uint count)
        {
            if (count == 0u)
                return;

            uint elementSize = buffer.ElementSize;
            buffer.PushSubData((int)(startIndex * elementSize), count * elementSize);
        }

        private void FlushMeshletRangeDirtyRange()
        {
            // See EnsureMeshletRangeCapacity: the range table is published with its
            // descriptor/index siblings as one generation at the frame boundary.
        }

        public bool TryGetMeshletRange(uint meshDataId, out GpuMeshletRange range)
        {
            range = default;
            return meshDataId != 0u && _meshletRangesByMeshId.TryGetValue(meshDataId, out range);
        }

        public bool HasRenderableMeshlets(uint meshDataId)
            => TryGetMeshletRange(meshDataId, out GpuMeshletRange range) && range.HasMeshlets;

        public bool RequiresTraditionalIndirectForMeshlets(uint meshDataId)
            => TryGetMeshletRange(meshDataId, out GpuMeshletRange range) && range.RequiresTraditionalIndirectFallback;

        public bool TryValidateResidentLodMeshletRanges(uint logicalMeshId, out uint missingMeshDataId, out int missingLodLevel)
        {
            missingMeshDataId = 0u;
            missingLodLevel = -1;

            if (!TryGetLodTableEntry(logicalMeshId, out LODTableEntry entry))
                return false;

            uint lodCount = Math.Min(entry.LODCount, (uint)MaxLogicalMeshLodCount);
            for (int lodLevel = 0; lodLevel < lodCount; lodLevel++)
            {
                uint meshDataId = entry.GetMeshDataId(lodLevel);
                if (meshDataId == 0u)
                    continue;

                if (TryGetMeshletRange(meshDataId, out _))
                    continue;

                missingMeshDataId = meshDataId;
                missingLodLevel = lodLevel;
                return false;
            }

            return true;
        }

        private static GpuMeshletDescriptor ToGpuSceneMeshletDescriptor(
            CpuMeshletDescriptor descriptor,
            uint vertexIndexOffset,
            uint triangleByteOffset)
            => new()
            {
                BoundsSphere = descriptor.BoundsSphere,
                VertexOffset = descriptor.VertexOffset + vertexIndexOffset,
                TriangleByteOffset = descriptor.TriangleOffset + triangleByteOffset,
                VertexCount = descriptor.VertexCount,
                TriangleCount = descriptor.TriangleCount,
                Cone = descriptor.Cone,
                ConeApex = descriptor.ConeApex,
                PackedCone = descriptor.PackedCone,
            };

        private void SetMeshletRange(uint meshID, GpuMeshletRange range)
        {
            EnsureMeshletRangeCapacity(meshID + 1u);
            bool changed = !_meshletRangesByMeshId.TryGetValue(meshID, out GpuMeshletRange existing) || !existing.Equals(range);
            _meshletRangesByMeshId[meshID] = range;
            if (range.HasMeshlets)
                _meshletIneligibleResidentMeshIds.TryRemove(meshID, out _);
            else
                _meshletIneligibleResidentMeshIds.TryAdd(meshID, 0);
            if (!changed)
                return;
            _meshletBufferGenerationDirty = true;
        }

        private void SetEmptyMeshletRange(uint meshID, ulong freshnessHash)
        {
            if (meshID == 0u)
                return;

            _meshletFreshnessByMeshId[meshID] = freshnessHash;
            SetMeshletRange(meshID, default);
        }

        private void ClearMeshletRange(uint meshID)
        {
            if (meshID == 0u)
                return;

            EnsureMeshletRangeCapacity(meshID + 1u);
            bool changed = _meshletRangesByMeshId.TryGetValue(meshID, out GpuMeshletRange existing) && !existing.Equals(default(GpuMeshletRange));
            _meshletRangesByMeshId.Remove(meshID);
            _meshletFreshnessByMeshId.Remove(meshID);
            _meshletValidationRevisionByMeshId.Remove(meshID);
            _meshletIneligibleResidentMeshIds.TryRemove(meshID, out _);
            if (!changed)
                return;
            _meshletBufferGenerationDirty = true;
        }

        private void EnsureMeshletRangeForMesh(uint meshID, XRMesh mesh)
        {
            if (meshID == 0u || mesh is null)
                return;

            EnsureMeshletRangeCapacity(meshID + 1u);

            // Meshlet payloads are derived asset data. Import/cache hydration validates
            // their source identity and compatibility before the mesh reaches GPUScene;
            // this render-adjacent registration path must remain O(1) and must never
            // rehash source geometry, touch disk, or invoke the cooker.
            MeshletPayload? payload = mesh.MeshletPayload;
            bool compatible = payload?.IsValidatedFor(mesh) == true && payload.OwnerValidationToken != 0UL;
            if (IsMeshletRangeCurrent(meshID, payload, compatible))
            {
                RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletCacheHit(1);
                return;
            }

            if (payload is not { HasMeshlets: true } || !compatible)
            {
                if (payload is null)
                {
                    RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletCacheMiss(1);
                    Debug.Meshes($"Meshlet.CacheMissing meshDataId={meshID} source='{mesh.Name ?? "<unnamed>"}' cachePath='<runtime-meshlet-payload>' commandCount={TotalCommandCount}");
                }
                else if (!compatible)
                {
                    RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletCacheStale(1);
                    Debug.MeshesWarning($"Meshlet.CacheIncompatible meshDataId={meshID} source='{mesh.Name ?? "<unnamed>"}' cachePath='<attached-payload>' commandCount={TotalCommandCount}");
                }
                else
                {
                    RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletCacheMiss(1);
                    Debug.Meshes($"Meshlet.CacheMissing meshDataId={meshID} source='{mesh.Name ?? "<unnamed>"}' cachePath='<runtime-meshlet-payload>' reason='payload has no meshlets' commandCount={TotalCommandCount}");
                }

                SetEmptyMeshletRange(meshID, payload?.FreshnessHash ?? 0UL);
                _meshletValidationRevisionByMeshId[meshID] = payload?.ValidationRevision ?? 0L;
                return;
            }

            uint meshletCount = (uint)payload.Meshlets.Length;
            if (_meshletFreshnessByMeshId.TryGetValue(meshID, out ulong oldFreshness) &&
                oldFreshness == payload.FreshnessHash &&
                _meshletRangesByMeshId.TryGetValue(meshID, out GpuMeshletRange existingRange) &&
                existingRange.MeshletCount == meshletCount)
            {
                RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletCacheHit(1);
                return;
            }

            uint meshletOffset = (uint)_meshletDescriptors.Count;
            uint vertexIndexOffset = (uint)_meshletVertexIndices.Count;
            uint triangleByteOffset = (uint)_meshletTriangleIndices.Count;
            uint vertexIndexCount = (uint)payload.VertexIndices.Length;
            uint triangleByteCount = (uint)payload.TriangleIndices.Length;

            for (int index = 0; index < payload.Meshlets.Length; index++)
            {
                GpuMeshletDescriptor descriptor = ToGpuSceneMeshletDescriptor(payload.Meshlets[index], vertexIndexOffset, triangleByteOffset);
                _meshletDescriptors.Add(descriptor);
            }

            _meshletVertexIndices.AddRange(payload.VertexIndices);

            _meshletTriangleIndices.AddRange(payload.TriangleIndices);

            _meshletFreshnessByMeshId[meshID] = payload.FreshnessHash;
            _meshletValidationRevisionByMeshId[meshID] = payload.ValidationRevision;
            SetMeshletRange(meshID, new GpuMeshletRange
            {
                MeshletOffset = meshletOffset,
                MeshletCount = meshletCount,
                VertexIndexOffset = vertexIndexOffset,
                TriangleIndexOffset = triangleByteOffset,
            });
            _meshletBufferGenerationDirty = true;
            RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletCacheHit(1);
            RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletBufferBytesResident(MeshletBufferBytesResident);
            Debug.Meshes(
                $"Meshlet.SceneBufferUpload meshDataId={meshID} source='{mesh.Name ?? "<unnamed>"}' cachePath='<runtime-meshlet-payload>' meshletCount={meshletCount} vertexIndexCount={vertexIndexCount} triangleByteCount={triangleByteCount} capacity={MeshletDescriptorBuffer.ElementCount} residentBytes={MeshletBufferBytesResident}");
        }

        private bool IsMeshletRangeCurrent(uint meshID, MeshletPayload? payload, bool fresh)
        {
            if (!_meshletFreshnessByMeshId.TryGetValue(meshID, out ulong existingFreshness) ||
                !_meshletRangesByMeshId.TryGetValue(meshID, out GpuMeshletRange existingRange))
            {
                return false;
            }

            if (payload is null)
                return existingFreshness == 0UL && !existingRange.HasMeshlets;

            if (!fresh || existingFreshness != payload.FreshnessHash
                || !_meshletValidationRevisionByMeshId.TryGetValue(meshID, out long existingRevision)
                || existingRevision != payload.ValidationRevision)
                return false;

            return payload.HasMeshlets
                ? existingRange.MeshletCount == (uint)payload.Meshlets.Length
                : !existingRange.HasMeshlets;
        }

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
                FlushMeshDataDirtyRange();

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
            EnsureMeshletRangeCapacity(newCapacity);
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
                    FlushDrawIndexedSoARange(0u, UpdatingCommandCount);
                    FlushCpuLodTransitionWrites();
                    MarkUpdatingCommandsDirty();

                    FlushMeshDataDirtyRange();

                    // Mark BVH dirty so it gets rebuilt before next cull pass
                    if (_gpuBvhTree is not null)
                        MarkBvhDirty();

                    _meshletsDirty = true;

                    RebuildAtlasIfDirty();
                }
            }
        }

        private void RemoveCommandAtIndex(uint targetIndex)
        {
            if (targetIndex >= UpdatingCommandCount)
            {
                Debug.MeshesWarning($"Invalid command index {targetIndex} for removal. Total commands: {UpdatingCommandCount}");
                return;
            }

            // Capture removed command before we overwrite slots (swap-remove).
            DrawMetadata removedCommand = UpdatingDrawMetadataBuffer.GetDataRawAtIndex<DrawMetadata>(targetIndex);
            uint removedMeshId = removedCommand.MeshID;
            uint removedLogicalMeshId = removedCommand.LogicalMeshID;
            uint removedTransformId = removedCommand.TransformID;
            uint removedSkinId = removedCommand.SkinID;

            uint lastIndex = UpdatingCommandCount - 1;

            // Remove the lookup entry for the removed command early.
            _commandIndexLookup.Remove(targetIndex);
            if (targetIndex < lastIndex)
            {
                DrawMetadata lastCommand = UpdatingDrawMetadataBuffer.GetDataRawAtIndex<DrawMetadata>(lastIndex);
                GPUTransparencyMetadata lastMetadata = UpdatingTransparencyMetadataBuffer.GetDataRawAtIndex<GPUTransparencyMetadata>(lastIndex);
                lastCommand.DrawID = targetIndex;
                lastCommand.BoundsID = targetIndex;
                WriteDrawMetadata(targetIndex, lastCommand);

                BoundsGpu lastBounds = UpdatingBoundsBuffer.GetDataRawAtIndex<BoundsGpu>(lastIndex);
                WriteBounds(targetIndex, lastBounds);
                MoveCommandAabb(lastIndex, targetIndex);
                UpdatingTransparencyMetadataBuffer.SetDataRawAtIndex(targetIndex, lastMetadata);
                LodTransitionBuffer.SetDataRawAtIndex(targetIndex, default(GPULodTransitionState));
                QueueCpuLodTransitionWrite(targetIndex);

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
                        Debug.MeshesWarning($"[GPUScene] RemoveCommandAtIndex: missing lookup for moved lastIndex={lastIndex} -> targetIndex={targetIndex}.");
                }
            }
            else
            {
                _commandIndexLookup.Remove(lastIndex);
            }

            LodTransitionBuffer.SetDataRawAtIndex(lastIndex, default(GPULodTransitionState));
            QueueCpuLodTransitionWrite(lastIndex);
            ClearDrawIndexedSoA(lastIndex);
            ReleaseTransformId(removedTransformId);
            ReleaseSkinId(removedSkinId);

            // Update mesh atlas lifetime after structural changes.
            if (removedLogicalMeshId != 0)
                ReleaseLogicalMeshResidency(removedLogicalMeshId, "RemoveCommandAtIndex");
            else
                DecrementAtlasMeshRefCount(removedMeshId, "RemoveCommandAtIndex");
            _meshletsDirty = true;
            --UpdatingCommandCount;
        }

        // Command buffers grow at powers of two and never shrink during a scene lifetime.
        private void VerifyUpdatingBufferSize(uint requiredSize)
        {
            uint currentCapacity = UpdatingDrawMetadataBuffer.ElementCount;
            uint nextPowerOfTwo = XRMath.NextPowerOfTwo(requiredSize).ClampMin(MinCommandCount);
            if (nextPowerOfTwo <= currentCapacity)
                return;

            SceneLog($"Resizing updating draw-indexed streams from {currentCapacity} to {nextPowerOfTwo}.");
            UpdatingTransparencyMetadataBuffer.Resize(nextPowerOfTwo);
            UpdatingDrawMetadataBuffer.Resize(nextPowerOfTwo);
            UpdatingBoundsBuffer.Resize(nextPowerOfTwo);
            UpdatingClassificationBuffer.Resize(nextPowerOfTwo);
            UpdatingVisibilityBuffer.Resize(nextPowerOfTwo);
            EnsureLodTransitionBufferCapacity(nextPowerOfTwo);
            uint newCapacity = UpdatingDrawMetadataBuffer.ElementCount;
            if (newCapacity > currentCapacity)
            {
                ZeroUpdatingDrawMetadataRange(currentCapacity, newCapacity - currentCapacity);
                ZeroUpdatingTransparencyMetadataRange(currentCapacity, newCapacity - currentCapacity);
                for (uint i = currentCapacity; i < newCapacity; ++i)
                    ClearDrawIndexedSoA(i);
                ZeroLodTransitionRange(currentCapacity, newCapacity - currentCapacity);
            }
        }

        private void ZeroUpdatingDrawMetadataRange(uint startIndex, uint count)
        {
            if (count == 0)
                return;

            var blank = default(DrawMetadata);
            uint end = startIndex + count;
            for (uint i = startIndex; i < end; ++i)
                UpdatingDrawMetadataBuffer.SetDataRawAtIndex(i, blank);

            uint elementSize = UpdatingDrawMetadataBuffer.ElementSize;

            uint byteOffset = startIndex * elementSize;
            uint byteCount = count * elementSize;
            UpdatingDrawMetadataBuffer.PushSubData((int)byteOffset, byteCount);
            MarkUpdatingCommandsDirty();

            if (IsGpuSceneLoggingEnabled())
                SceneLog($"Zeroed updating draw metadata range [{startIndex}, {end}) ({byteCount} bytes)");
        }

        private void FlushDrawIndexedSoARange(uint startIndex, uint count)
        {
            if (count == 0u)
                return;

            static void Flush(XRDataBuffer buffer, uint start, uint length)
                => buffer.PushSubData((int)(start * buffer.ElementSize), length * buffer.ElementSize);

            Flush(UpdatingDrawMetadataBuffer, startIndex, count);
            Flush(UpdatingBoundsBuffer, startIndex, count);
            Flush(UpdatingClassificationBuffer, startIndex, count);
            Flush(UpdatingVisibilityBuffer, startIndex, count);
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
            MarkUpdatingCommandsDirty();
        }

        // Command buffers grow at powers of two and never shrink during a scene lifetime.
        private void VerifyCommandBufferSize(uint requiredSize)
        {
            uint currentCapacity = DrawMetadataBuffer.ElementCount;
            uint nextPowerOfTwo = XRMath.NextPowerOfTwo(requiredSize).ClampMin(MinCommandCount);
            if (nextPowerOfTwo <= currentCapacity)
                return;

            SceneLog($"Resizing published draw-indexed streams from {currentCapacity} to {nextPowerOfTwo}.");
            AllLoadedTransparencyMetadataBuffer.Resize(nextPowerOfTwo);
            DrawMetadataBuffer.Resize(nextPowerOfTwo);
            BoundsBuffer.Resize(nextPowerOfTwo);
            ClassificationBuffer.Resize(nextPowerOfTwo);
            VisibilityBuffer.Resize(nextPowerOfTwo);
            uint newCapacity = DrawMetadataBuffer.ElementCount;
            if (newCapacity > currentCapacity)
            {
                ZeroDrawMetadataRange(currentCapacity, newCapacity - currentCapacity);
                ZeroTransparencyMetadataRange(currentCapacity, newCapacity - currentCapacity);
            }
        }

        private void ZeroDrawMetadataRange(uint startIndex, uint count)
        {
            if (count == 0)
                return;

            var blank = default(DrawMetadata);
            uint end = startIndex + count;
            for (uint i = startIndex; i < end; ++i)
                DrawMetadataBuffer.SetDataRawAtIndex(i, blank);

            uint elementSize = DrawMetadataBuffer.ElementSize;

            uint byteOffset = startIndex * elementSize;
            uint byteCount = count * elementSize;
            DrawMetadataBuffer.PushSubData((int)byteOffset, byteCount);

            if (IsGpuSceneLoggingEnabled())
                SceneLog($"Zeroed published draw metadata range [{startIndex}, {end}) ({byteCount} bytes)");
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

    }
}
