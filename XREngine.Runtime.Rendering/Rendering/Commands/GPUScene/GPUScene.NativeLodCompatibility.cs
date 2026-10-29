using XREngine.Components.Scene.Mesh;
using XREngine.Rendering.Info;

namespace XREngine.Rendering.Commands;

public partial class GPUScene
{
    private readonly XRMeshRenderer?[] _nativeLodRenderers = new XRMeshRenderer?[MaxLogicalMeshLodCount];

    /// <summary>Seals native mesh substitution admission before the draw streams are published.</summary>
    private void RefreshNativeLodCompatibilityAtFrameBoundary()
    {
        bool changed = false;
        foreach (var entry in _commandIndexLookup)
        {
            uint index = entry.Key;
            if (index >= _updatingCommandCount) continue;
            DrawMetadata metadata = UpdatingDrawMetadataBuffer.GetDataRawAtIndex<DrawMetadata>(index);
            if ((metadata.Flags & (uint)GPUIndirectRenderFlags.LODEnabled) == 0) continue;
            var source = entry.Value;
            RenderInfo? renderInfo = (source.command as RenderCommand)?.OwnerRenderInfo;
            bool compatible = AreNativeLodCandidatesCompatible(renderInfo, source.snapshot,
                source.subMeshIndex, metadata);
            uint flags = metadata.Flags & ~(uint)GPUIndirectRenderFlags.NativeLodSelectionDisabled;
            if (!compatible) flags |= (uint)GPUIndirectRenderFlags.NativeLodSelectionDisabled;
            if (flags == metadata.Flags) continue;
            metadata.Flags = flags;
            WriteDrawMetadata(index, metadata);
            changed = true;
        }
        if (changed) MarkUpdatingCommandsDirty();
    }

    private bool AreNativeLodCandidatesCompatible(RenderInfo? renderInfo,
        in GpuSceneMeshCommandSnapshot snapshot, int primitiveIndex, in DrawMetadata metadata)
    {
        RenderableMesh? renderable = ResolveLodRenderable(renderInfo);
        if (renderable is null || snapshot.Renderer is not { } sourceRenderer ||
            !_idToMaterial.TryGetValue(metadata.MaterialID, out XRMaterial? sourceMaterial) ||
            !_logicalMeshStates.TryGetValue(metadata.LogicalMeshID, out LogicalMeshState? state)) return false;
        int primitiveCount = Math.Max(1, sourceRenderer.Submeshes.Count);
        try
        {
            int count = renderable.CollectLodRenderers((uint)primitiveIndex, _nativeLodRenderers);
            if (count != state.LODCount || count > MaxLogicalMeshLodCount) return false;
            for (int level = 0; level < count; level++)
            {
                XRMeshRenderer? renderer = _nativeLodRenderers[level];
                if (renderer is null || Math.Max(1, renderer.Submeshes.Count) != primitiveCount ||
                    !renderer.TryGetMesh(primitiveIndex, out XRMesh? mesh, out XRMaterial? material) ||
                    mesh is null || !ReferenceEquals(mesh, state.Meshes[level]) ||
                    !ReferenceEquals(snapshot.MaterialOverride ?? material, sourceMaterial) ||
                    mesh.HasSkinning || mesh.HasBlendshapes || renderer.Buffers.Count != 0 ||
                    renderer.BindingPublishers.Count != 0 || renderer.HasSettingUniformsHandlers ||
                    renderer.HasRenderDataPreparation) return false;
            }
            return true;
        }
        finally { Array.Clear(_nativeLodRenderers); }
    }
}
