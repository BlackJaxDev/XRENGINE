// =====================================================================================
// GPUScene.CommandConversion.cs - stage-native record creation, Phase 1 updates, and mesh label/validation helpers.
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

            DrawMetadata metadata = new()
            {
                DrawID = boundsId,
                MeshID = meshID,
                SubmeshID = (meshID << 16) | (submeshLocalIndex & 0xFFFF),
                MaterialID = materialID,
                RenderPass = (uint)command.RenderPass,
                InstanceCount = command.Instances == 0 ? 1u : command.Instances,
                LayerMask = 0xFFFFFFFF,
                Flags = 0,
                LodPolicy = 0,
                RenderIdentityID = command.StableQueryKey,
                LogicalMeshID = logicalMeshID,
                TransformID = transformId,
                SkinID = skinId,
                StateClassID = stateClassId,
                BoundsID = boundsId,
            };

            BoundsGpu bounds = ComputeRenderCullingBoundsGpu(renderInfo, mesh.Bounds, modelMatrix, boundsId + 1u);

            if (renderInfo is RenderInfo3D info3d)
                metadata.LayerMask = 1u << info3d.Layer;

            metadata.Flags = ComposeDrawFlags(renderInfo, command, mesh, material, modelMatrix, lodCount);
            return (metadata, bounds);
        }

        private static uint ComposeDrawFlags(
            RenderInfo renderInfo,
            IRenderCommandMesh command,
            XRMesh mesh,
            XRMaterial material,
            in Matrix4x4 modelMatrix,
            uint lodCount)
        {
            GPUIndirectRenderFlags flags = GPUIndirectRenderFlags.None;
            if (material.IsTransparentLike())
                flags |= GPUIndirectRenderFlags.Transparent;

            if (renderInfo is RenderInfo3D info3d)
            {
                if (info3d.CastsShadows)
                    flags |= GPUIndirectRenderFlags.CastShadow;
                if (info3d.ReceivesShadows)
                    flags |= GPUIndirectRenderFlags.ReceiveShadows;
            }

            if (mesh.HasSkinning)
                flags |= GPUIndirectRenderFlags.Skinned;
            if (mesh.HasBlendshapes)
                flags |= GPUIndirectRenderFlags.BlendShapes;
            if (command.Instances > 1u)
                flags |= GPUIndirectRenderFlags.Instanced;
            if (lodCount > 1u)
                flags |= GPUIndirectRenderFlags.LODEnabled;

            ECullMode cullMode = material.RenderOptions?.CullMode ?? ECullMode.Back;
            if (cullMode == ECullMode.None)
                flags |= GPUIndirectRenderFlags.DoubleSided;
            if (cullMode != ECullMode.Back)
                flags |= GPUIndirectRenderFlags.NonCanonicalRasterState;
            if (!HasUniformPositiveScale(modelMatrix))
                flags |= GPUIndirectRenderFlags.Dynamic;
            if (command.ForceCpuRendering || material.RenderOptions?.ExcludeFromGpuIndirect == true)
                flags |= GPUIndirectRenderFlags.CpuFallbackOnly;

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

            bool rebuildRenderable = false;
            bool anyChanged = false;

            using (_lock.EnterScope())
            {
                if (!_commandIndicesPerMeshCommand.TryGetValue(meshCmd, out var indices) || indices.Count == 0)
                {
                    Add(renderInfo);
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

                    var existing = UpdatingDrawMetadataBuffer.GetDataRawAtIndex<DrawMetadata>(index);
                    var updated = existing;

                    bool transformChanged = UpdateTransform(existing.TransformID, modelMatrix);
                    BoundsGpu updatedBounds = ComputeRenderCullingBoundsGpu(renderInfo, mesh.Bounds, modelMatrix, updated.BoundsID + 1u);
                    updated.MeshID = newMeshID;
                    updated.SubmeshID = (newMeshID << 16) | ((uint)subMeshIndex & 0xFFFF);
                    updated.MaterialID = newMaterialID;
                    updated.InstanceCount = meshCmd.Instances == 0 ? 1u : meshCmd.Instances;
                    updated.RenderPass = (uint)meshCmd.RenderPass;
                    updated.LogicalMeshID = newLogicalMeshID;
                    updated.DrawID = index;
                    updated.BoundsID = index;
                    updated.StateClassID = ResolveStateClassId(material, meshCmd.RenderPass, newMaterialID);

                    if (renderInfo is RenderInfo3D info3d)
                        updated.LayerMask = 1u << info3d.Layer;
                    updated.Flags = ComposeDrawFlags(renderInfo, meshCmd, mesh, material, modelMatrix, lodCount);
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

                    uint elementSize = UpdatingDrawMetadataBuffer.ElementSize;

                    uint byteOffset = minIndex * elementSize;
                    uint byteCount = (maxIndex - minIndex + 1) * elementSize;
                    UpdatingDrawMetadataBuffer.PushSubData((int)byteOffset, byteCount);
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

                Remove(renderInfo);
                Add(renderInfo);
                return true;
            }

            return anyChanged;
        }

        // Meshlet cone culling assumes a rigid or uniformly scaled transform.
        // Mirroring and non-uniform scale stay in the conventional raster path
        // until the task shader owns the corresponding conservative math.
        private static bool HasUniformPositiveScale(in Matrix4x4 matrix)
        {
            Vector3 x = new(matrix.M11, matrix.M12, matrix.M13);
            Vector3 y = new(matrix.M21, matrix.M22, matrix.M23);
            Vector3 z = new(matrix.M31, matrix.M32, matrix.M33);
            float sx = x.LengthSquared();
            float sy = y.LengthSquared();
            float sz = z.LengthSquared();
            const float tolerance = 0.0001f;
            if (sx <= tolerance || sy <= tolerance || sz <= tolerance)
                return false;

            float maxScale = Math.Max(sx, Math.Max(sy, sz));
            if (Math.Abs(sx - sy) > maxScale * tolerance || Math.Abs(sx - sz) > maxScale * tolerance)
                return false;

            return Vector3.Dot(Vector3.Cross(x, y), z) > 0.0f;
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
