// =====================================================================================
// GPUScene.CommandConversion.cs - stage-native record creation, Phase 1 updates, and mesh label/validation helpers.
// Part of the GPUScene partial class. See GPUScene.cs for the canonical class summary.
// =====================================================================================

using XREngine.Extensions;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
        /// Creates the stage-native cull-control and cull-bounds records for one draw.
        /// </summary>
        /// <param name="renderInfo">The parent render info.</param>
        /// <param name="command">The mesh render command to publish.</param>
        /// <param name="mesh">The mesh to render.</param>
        /// <param name="material">The material to use.</param>
        /// <param name="submeshLocalIndex">The submesh index within the mesh renderer.</param>
        /// <returns>The two canonical stream records, or null if publication failed.</returns>
        private (DrawMetadata Metadata, BoundsGpu Bounds)? CreateStageNativeDrawRecords(
            RenderInfo renderInfo,
            in GpuSceneMeshCommandSnapshot snapshot,
            XRMesh? mesh,
            XRMaterial? material,
            uint meshID,
            uint logicalMeshID,
            uint lodCount,
            uint submeshLocalIndex,
            uint transformId,
            uint skinId,
            uint stateClassId,
            uint boundsId)
        {
            if (mesh is null || material is null)
                return null;

            GetOrCreateMaterialID(material, out uint materialID);

            Matrix4x4 modelMatrix = snapshot.ModelMatrix;

            DrawMetadata metadata = new()
            {
                DrawID = boundsId,
                MeshID = meshID,
                SubmeshID = (meshID << 16) | (submeshLocalIndex & 0xFFFF),
                MaterialID = materialID,
                RenderPass = (uint)snapshot.RenderPass,
                InstanceCount = snapshot.Instances == 0 ? 1u : snapshot.Instances,
                LayerMask = 0xFFFFFFFF,
                Flags = 0,
                LodPolicy = 0,
                RenderIdentityID = snapshot.StableQueryKey,
                LogicalMeshID = logicalMeshID,
                TransformID = transformId,
                SkinID = skinId,
                StateClassID = stateClassId,
                BoundsID = boundsId,
            };

            BoundsGpu bounds = ComputeRenderCullingBoundsGpu(snapshot.Owner, mesh.Bounds, modelMatrix, boundsId + 1u);

            if (snapshot.Owner.Is3D)
                metadata.LayerMask = snapshot.Owner.LayerMask;

            metadata.Flags = ComposeDrawFlags(renderInfo, snapshot, mesh, material, modelMatrix, lodCount);
            return (metadata, bounds);
        }

        private static uint ComposeDrawFlags(
            RenderInfo renderInfo,
            in GpuSceneMeshCommandSnapshot snapshot,
            XRMesh mesh,
            XRMaterial material,
            in Matrix4x4 modelMatrix,
            uint lodCount)
        {
            GPUIndirectRenderFlags flags = GPUIndirectRenderFlags.None;
            if (material.IsTransparentLike())
                flags |= GPUIndirectRenderFlags.Transparent;

            if (snapshot.Owner.Is3D)
            {
                if (snapshot.Owner.CastsShadows)
                    flags |= GPUIndirectRenderFlags.CastShadow;
                if (snapshot.Owner.ReceivesShadows)
                    flags |= GPUIndirectRenderFlags.ReceiveShadows;
            }

            if (mesh.HasSkinning)
                flags |= GPUIndirectRenderFlags.Skinned;
            if (mesh.HasBlendshapes)
                flags |= GPUIndirectRenderFlags.BlendShapes;
            if (snapshot.Instances > 1u)
                flags |= GPUIndirectRenderFlags.Instanced;
            if (lodCount > 1u)
                flags |= GPUIndirectRenderFlags.LODEnabled;

            ECullMode cullMode = material.RenderOptions?.CullMode ?? ECullMode.Back;
            if (cullMode == ECullMode.None)
                flags |= GPUIndirectRenderFlags.DoubleSided;
            if (cullMode != ECullMode.Back)
                flags |= GPUIndirectRenderFlags.NonCanonicalRasterState;
            if (!MeshletTransformEligibility.HasUniformPositiveScale(modelMatrix))
                flags |= GPUIndirectRenderFlags.Dynamic;
            if (snapshot.ForceCpuRendering || material.RenderOptions?.ExcludeFromGpuIndirect == true)
                flags |= GPUIndirectRenderFlags.CpuFallbackOnly;
            uint editorHighlightBits = snapshot.EditorHighlightBits;
            if ((editorHighlightBits & 1u) != 0u)
                flags |= GPUIndirectRenderFlags.EditorHovered;
            if ((editorHighlightBits & 2u) != 0u)
                flags |= GPUIndirectRenderFlags.EditorSelected;

            return (uint)flags;
        }

        /// <summary>
        /// Updates existing GPU commands for a single mesh render command.
        /// Intended to be called during the swap/collect phases (single-threaded) to keep GPU state correct
        /// under transform/material/pass churn without remove/re-add.
        /// </summary>
        public bool TryUpdateMeshCommand(RenderInfo renderInfo, IRenderCommandMesh meshCmd)
        {
            if (renderInfo is null || meshCmd is null)
                return false;

            return TryUpdateMeshCommand(renderInfo, meshCmd,
                GpuSceneMeshCommandSnapshot.CaptureLive(renderInfo, meshCmd));
        }

        internal bool TryUpdateMeshCommand(RenderInfo renderInfo, IRenderCommandMesh meshCmd,
            in GpuSceneMeshCommandSnapshot snapshot)
        {
            if (renderInfo is null || meshCmd is null)
                return false;

            if (!S13aPublicationTelemetry.Enabled)
            {
                MeshUpdateObservation disabledObservation = default;
                return TryUpdateMeshCommandCore(renderInfo, meshCmd, snapshot, ref disabledObservation);
            }

            // This scope stays on the callback thread. Allocation deltas are never
            // summed from another worker's thread-local GC counter.
            MeshUpdateObservation observation = default;
            long started = Stopwatch.GetTimestamp();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            bool changed = false;
            bool completed = false;
            try
            {
                changed = TryUpdateMeshCommandCore(renderInfo, meshCmd, snapshot, ref observation);
                completed = true;
                return changed;
            }
            finally
            {
                long totalTicks = Stopwatch.GetTimestamp() - started;
                S13aPublicationTelemetry.MeshUpdate(changed, completed, observation.LockWaitTicks,
                    Math.Max(0L, totalTicks - observation.LockWaitTicks),
                    GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                    observation.Submeshes, observation.RegistrationAttempts, observation.MetadataWrites,
                    observation.StateClassWrites, observation.TransparencyWrites,
                    observation.MaterialTicks, observation.RegistrationTicks, observation.WriteTicks);
            }
        }

        private struct MeshUpdateObservation
        {
            public long LockWaitTicks;
            public int Submeshes;
            public int RegistrationAttempts;
            public int MetadataWrites;
            public int StateClassWrites;
            public int TransparencyWrites;
            public long MaterialTicks;
            public long RegistrationTicks;
            public long WriteTicks;
        }

        private bool TryUpdateMeshCommandCore(RenderInfo renderInfo, IRenderCommandMesh meshCmd,
            in GpuSceneMeshCommandSnapshot snapshot,
            ref MeshUpdateObservation observation)
        {

            bool rebuildRenderable = false;
            bool anyChanged = false;

            long beforeLock = S13aPublicationTelemetry.Enabled ? Stopwatch.GetTimestamp() : 0L;
            using (_lock.EnterScope())
            {
                if (S13aPublicationTelemetry.Enabled)
                    observation.LockWaitTicks = Stopwatch.GetTimestamp() - beforeLock;
                // Disposal runs for every early return, including removal and add paths.
                using var heldBody = S13aPublicationTelemetry.BeginLockBody();
                if (!_commandIndicesPerMeshCommand.TryGetValue(meshCmd, out var indices) || indices.Count == 0)
                {
                    Add(renderInfo, meshCmd, snapshot);
                    return true;
                }

                XRMeshRenderer? meshRenderer = snapshot.Renderer;
                if (meshRenderer is null)
                {
                    if (_commandUpdateErrorLogBudget > 0 && Interlocked.Decrement(ref _commandUpdateErrorLogBudget) >= 0)
                        Debug.MeshesWarning($"[GPUScene] Mesh command lost submeshes; removing. Renderable={ResolveOwnerLabel(renderInfo.Owner)}");

                    RemoveMeshCommandIndices(meshCmd, indices);
                    return true;
                }

                Matrix4x4 modelMatrix = snapshot.ModelMatrix;

                uint minIndex = uint.MaxValue;
                uint maxIndex = 0;

                for (int i = 0; i < indices.Count; i++)
                {
                    if (S13aPublicationTelemetry.Enabled)
                        observation.Submeshes++;
                    uint index = indices[i];
                    if (index >= UpdatingCommandCount)
                        continue;

                    if (!_commandIndexLookup.TryGetValue(index, out var lookup))
                        continue;

                    int subMeshIndex = lookup.subMeshIndex;
                    if (!meshRenderer.TryGetMesh(subMeshIndex, out XRMesh? mesh, out XRMaterial? mat))
                    {
                        rebuildRenderable = true;
                        break;
                    }

                    XRMaterial? material = snapshot.MaterialOverride ?? mat;
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
                        string meshLabel = EnsureMeshDebugLabel(mesh, snapshot.Renderer, renderInfo, subMeshIndex);
                        RecordUnsupportedMesh(mesh, meshLabel, validationFailure);

                        RemoveMeshCommandIndices(meshCmd, indices);
                        return true;
                    }

                    long phaseStarted = S13aPublicationTelemetry.Enabled ? Stopwatch.GetTimestamp() : 0L;
                    GetOrCreateMaterialID(material, out uint newMaterialID);
                    if (S13aPublicationTelemetry.Enabled)
                        observation.MaterialTicks += Stopwatch.GetTimestamp() - phaseStarted;

                    string resolvedMeshLabel = EnsureMeshDebugLabel(mesh, snapshot.Renderer, renderInfo, subMeshIndex);
                    if (S13aPublicationTelemetry.Enabled)
                        observation.RegistrationAttempts++;
                    phaseStarted = S13aPublicationTelemetry.Enabled ? Stopwatch.GetTimestamp() : 0L;
                    bool registered = ResolveLogicalMeshRegistration(renderInfo, mesh, (uint)subMeshIndex, resolvedMeshLabel, out uint newMeshID, out uint newLogicalMeshID, out uint lodCount, out var atlasFailure);
                    if (S13aPublicationTelemetry.Enabled)
                        observation.RegistrationTicks += Stopwatch.GetTimestamp() - phaseStarted;
                    if (!registered)
                    {
                        atlasFailure ??= "atlas registration failed";
                        RecordUnsupportedMesh(mesh, resolvedMeshLabel, atlasFailure);
                        RemoveMeshCommandIndices(meshCmd, indices);
                        return true;
                    }

                    var existing = UpdatingDrawMetadataBuffer.GetDataRawAtIndex<DrawMetadata>(index);
                    var updated = existing;

                    bool transformChanged = UpdateTransform(existing.TransformID, modelMatrix);
                    BoundsGpu updatedBounds = ComputeRenderCullingBoundsGpu(snapshot.Owner, mesh.Bounds, modelMatrix, updated.BoundsID + 1u);
                    updated.MeshID = newMeshID;
                    updated.SubmeshID = (newMeshID << 16) | ((uint)subMeshIndex & 0xFFFF);
                    updated.MaterialID = newMaterialID;
                    updated.InstanceCount = snapshot.Instances == 0 ? 1u : snapshot.Instances;
                    updated.RenderPass = (uint)snapshot.RenderPass;
                    updated.LogicalMeshID = newLogicalMeshID;
                    updated.DrawID = index;
                    updated.BoundsID = index;
                    phaseStarted = S13aPublicationTelemetry.Enabled ? Stopwatch.GetTimestamp() : 0L;
                    updated.StateClassID = ResolveStateClassId(material, snapshot.RenderPass, newMaterialID);
                    if (S13aPublicationTelemetry.Enabled)
                    {
                        observation.StateClassWrites++;
                        observation.MaterialTicks += Stopwatch.GetTimestamp() - phaseStarted;
                    }

                    if (snapshot.Owner.Is3D)
                        updated.LayerMask = snapshot.Owner.LayerMask;
                    updated.Flags = ComposeDrawFlags(renderInfo, snapshot, mesh, material, modelMatrix, lodCount);
                    GPUTransparencyMetadata transparency = GPUTransparencyMetadata.FromMaterial(material);
                    GPUTransparencyMetadata priorTransparency =
                        UpdatingTransparencyMetadataBuffer.GetDataRawAtIndex<GPUTransparencyMetadata>(index);
                    bool transparencyChanged = priorTransparency.PackedModeAndDomain != transparency.PackedModeAndDomain ||
                        priorTransparency.SortPriority != transparency.SortPriority ||
                        priorTransparency.AlphaCutoffBits != transparency.AlphaCutoffBits ||
                        priorTransparency.Flags != transparency.Flags;
                    if (transparencyChanged)
                    {
                        UpdatingTransparencyMetadataBuffer.SetDataRawAtIndex(index, transparency);
                        if (S13aPublicationTelemetry.Enabled)
                            observation.TransparencyWrites++;
                    }

                    if (existing.LogicalMeshID != newLogicalMeshID)
                    {
                        AcquireLogicalMeshResidency(newLogicalMeshID);
                        ReleaseLogicalMeshResidency(existing.LogicalMeshID, "TryUpdateMeshCommand(mesh changed)");
                    }

                    BoundsGpu existingBounds = UpdatingBoundsBuffer.GetDataRawAtIndex<BoundsGpu>(index);
                    bool boundsChanged = !existingBounds.Equals(updatedBounds);

                    if (!existing.Equals(updated) || transformChanged || boundsChanged)
                    {
                        phaseStarted = S13aPublicationTelemetry.Enabled ? Stopwatch.GetTimestamp() : 0L;
                        if (S13aPublicationTelemetry.Enabled)
                            observation.MetadataWrites++;
                        WriteDrawMetadata(index, updated);
                        WriteBounds(index, updatedBounds);
                        if (existing.MeshID != updated.MeshID || existing.LogicalMeshID != updated.LogicalMeshID)
                        {
                            LodTransitionBuffer.SetDataRawAtIndex(index, default(GPULodTransitionState));
                            QueueCpuLodTransitionWrite(index);
                        }
                        if (_useInternalBvh)
                            WriteTightCommandAabb(index, snapshot.Owner, mesh.Bounds, modelMatrix);
                        anyChanged = true;
                        minIndex = Math.Min(minIndex, index);
                        maxIndex = Math.Max(maxIndex, index);
                        if (S13aPublicationTelemetry.Enabled)
                            observation.WriteTicks += Stopwatch.GetTimestamp() - phaseStarted;
                    }
                    else if (transparencyChanged)
                    {
                        // The render-side transparency stream is copied when the command
                        // content version advances, even if its draw row stayed identical.
                        anyChanged = true;
                        minIndex = Math.Min(minIndex, index);
                        maxIndex = Math.Max(maxIndex, index);
                    }
                }

                if (!rebuildRenderable)
                {
                    if (!anyChanged)
                        return false;

                    uint elementSize = UpdatingDrawMetadataBuffer.ElementSize;

                    uint byteOffset = minIndex * elementSize;
                    uint byteCount = (maxIndex - minIndex + 1) * elementSize;
                    UpdatingDrawMetadataBuffer.CommitDirtyBytes(byteOffset, byteCount);
                    FlushCpuLodTransitionWrites();
                    MarkUpdatingCommandsDirty();

                    FlushMeshDataDirtyRange();

                    _meshletsDirty = true;
                    RebuildAtlasIfDirty();
                }
            }

            if (rebuildRenderable)
            {
                if (_commandUpdateErrorLogBudget > 0 && Interlocked.Decrement(ref _commandUpdateErrorLogBudget) >= 0)
                    Debug.MeshesWarning($"[GPUScene] Rebuilding renderable GPU commands due to structural mismatch. Renderable={ResolveOwnerLabel(renderInfo.Owner)}");

                using (_lock.EnterScope())
                {
                    if (_commandIndicesPerMeshCommand.TryGetValue(meshCmd, out var staleIndices))
                        RemoveMeshCommandIndices(meshCmd, staleIndices);
                }
                Add(renderInfo, meshCmd, snapshot);
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
            VerifyUpdatingBufferSize(UpdatingCommandCount);
            FlushDrawIndexedSoARange(0u, UpdatingCommandCount);
            FlushCpuLodTransitionWrites();
            FlushMeshDataDirtyRange();
            MarkUpdatingCommandsDirty();
            if (_gpuBvhTree is not null)
                MarkBvhDirty();
            RebuildAtlasIfDirty();
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

        /// <summary>
        /// Validates that a mesh can be used with GPU rendering.
        /// </summary>
        /// <param name="mesh">The mesh to validate.</param>
        /// <param name="reason">The reason for failure if validation fails.</param>
        /// <returns>True if the mesh is valid for GPU rendering; false otherwise.</returns>
        private static bool ValidateMeshForGpu(XRMesh mesh, out string reason)
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

            // Swap/collect revalidates animated meshes every frame. Inspect topology
            // metadata instead of allocating a flattened copy of every triangle.
            if (!mesh.HasIndexData(EPrimitiveType.Triangles))
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
                Debug.MeshesWarning(message);
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

    }
}
