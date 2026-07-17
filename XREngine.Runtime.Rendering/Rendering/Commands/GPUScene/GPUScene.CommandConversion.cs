// =====================================================================================
// GPUScene.CommandConversion.cs - CPU command -> GPU command conversion, Phase 1 updates, and mesh label/validation helpers.
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
        /// Converts a render command to a GPU-friendly format.
        /// </summary>
        /// <param name="renderInfo">The parent render info.</param>
        /// <param name="command">The mesh render command to convert.</param>
        /// <param name="mesh">The mesh to render.</param>
        /// <param name="material">The material to use.</param>
        /// <param name="submeshLocalIndex">The submesh index within the mesh renderer.</param>
        /// <returns>The GPU command, or null if conversion failed.</returns>
        private GPUIndirectRenderCommand? ConvertToGPUCommand(
            RenderInfo renderInfo,
            IRenderCommandMesh command,
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
                Flags = 0,
                LODLevel = 0,
                ShaderProgramID = 0,
                LogicalMeshID = logicalMeshID,
                TransformID = transformId,
                SkinID = skinId,
                StateClassID = stateClassId,
                BoundsID = boundsId,
                Reserved1 = 0
            };

            // Bounds: world-space (center + radius), conservative for non-uniform scale.
            gpuCommand.BoundingSphere = ComputeRenderCullingBoundsGpu(renderInfo, mesh.Bounds, modelMatrix, boundsId + 1u).BoundingSphere;

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
                {
                    var missingMaterial = meshCmd.MaterialOverride ?? meshCmd.Mesh?.Material;
                    if (meshCmd.ForceCpuRendering || missingMaterial?.RenderOptions?.ExcludeFromGpuIndirect == true)
                        return false;

                    Add(renderInfo);
                    return true;
                }

                // If this command is now excluded from GPU indirect, remove its indices.
                var topMaterial = meshCmd.MaterialOverride ?? meshCmd.Mesh?.Material;
                if (meshCmd.ForceCpuRendering || topMaterial?.RenderOptions?.ExcludeFromGpuIndirect == true)
                {
                    if (_commandUpdateErrorLogBudget > 0 && Interlocked.Decrement(ref _commandUpdateErrorLogBudget) >= 0)
                        Debug.MeshesWarning($"[GPUScene] CPU fallback/ExcludeFromGpuIndirect became true; removing mesh command. Renderable={ResolveOwnerLabel(renderInfo.Owner)}");

                    RemoveMeshCommandIndices(meshCmd, indices);
                    return true;
                }

                var subMeshes = meshCmd.Mesh?.GetMeshes();
                if (subMeshes is null || subMeshes.Length == 0)
                {
                    if (_commandUpdateErrorLogBudget > 0 && Interlocked.Decrement(ref _commandUpdateErrorLogBudget) >= 0)
                        Debug.MeshesWarning($"[GPUScene] Mesh command lost submeshes; removing. Renderable={ResolveOwnerLabel(renderInfo.Owner)}");

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

                    bool transformChanged = UpdateTransform(existing.TransformID, modelMatrix);
                    BoundsGpu updatedBounds = ComputeRenderCullingBoundsGpu(renderInfo, mesh.Bounds, modelMatrix, updated.BoundsID + 1u);
                    updated.BoundingSphere = updatedBounds.BoundingSphere;

                    updated.MeshID = newMeshID;
                    updated.SubmeshID = (newMeshID << 16) | ((uint)subMeshIndex & 0xFFFF);
                    updated.MaterialID = newMaterialID;
                    updated.InstanceCount = meshCmd.Instances == 0 ? 1u : meshCmd.Instances;
                    updated.RenderPass = (uint)meshCmd.RenderPass;
                    updated.RenderDistance = meshCmd.RenderDistance.ClampMin(0.0f);
                    updated.LogicalMeshID = newLogicalMeshID;
                    updated.Reserved1 = index;
                    updated.BoundsID = index;
                    updated.StateClassID = ResolveStateClassId(material, meshCmd.RenderPass, newMaterialID);

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

                    BoundsGpu existingBounds = UpdatingBoundsBuffer.GetDataRawAtIndex<BoundsGpu>(index);
                    bool boundsChanged = !existingBounds.Equals(updatedBounds);

                    if (!existing.Equals(updated) || transformChanged || boundsChanged)
                    {
                        UpdatingCommandsBuffer.SetDataRawAtIndex(index, updated);
                        WriteDrawMetadata(index, updated);
                        WriteBounds(index, updatedBounds);
                        if (existing.MeshID != updated.MeshID || existing.LogicalMeshID != updated.LogicalMeshID)
                        {
                            LodTransitionBuffer.SetDataRawAtIndex(index, default(GPULodTransitionState));
                            QueueCpuLodTransitionWrite(index);
                        }
                        if (_useInternalBvh)
                            WriteTightCommandAabb(index, renderInfo, mesh.Bounds, modelMatrix);
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
                    FlushCpuLodTransitionWrites();
                    MarkUpdatingCommandsDirty();

                    FlushMeshDataDirtyRange();

                    if (_useInternalBvh)
                    {
                        bool canRefit = _bvhReady && !_bvhDirty && _gpuBvhTree is not null && _bvhPrimitiveCount == _updatingCommandCount;
                        if (canRefit)
                            _bvhRefitPending = true;
                        else
                            MarkBvhDirtyUnlessSuppressed(_updatingCommandCount);
                    }

                    _meshletsDirty = true;
                    RebuildAtlasIfDirty();
                }
            }

            if (rebuildRenderable)
            {
                if (_commandUpdateErrorLogBudget > 0 && Interlocked.Decrement(ref _commandUpdateErrorLogBudget) >= 0)
                    Debug.MeshesWarning($"[GPUScene] Rebuilding renderable GPU commands due to structural mismatch. Renderable={ResolveOwnerLabel(renderInfo.Owner)}");

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

            FlushCpuLodTransitionWrites();

            indices.Clear();
            _commandIndicesPerMeshCommand.Remove(meshCmd);
            meshCmd.GPUCommandIndex = uint.MaxValue;
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
