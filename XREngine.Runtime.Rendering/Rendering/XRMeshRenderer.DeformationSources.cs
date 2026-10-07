using System.Numerics;

namespace XREngine.Rendering;

public partial class XRMeshRenderer
{
    private readonly Dictionary<XRMesh, XRMeshDeformationInputs> _deformationInputs =
        new(ReferenceEqualityComparer.Instance);
    private XRDataBuffer? _deformationSeedPalette;
    private bool _deformationPoseSettled;
    private ulong _deformationSeedFrame;
    private bool _hasDeformationSeedFrame;
    private bool _deformationGpuOwnedPose;

    /// <summary>Resolves canonical inputs for the exact authored primitive while its renderer lease is held.</summary>
    internal bool TryPrepareDeformationInputs(XRMesh mesh, bool skinning, bool blendshapes,
        bool publish, out XRMeshDeformationInputSnapshot inputs, bool observePose = true, bool requireMorphs = true)
    {
        ValidateResourcePublicationLease();
        inputs = default;
        if (mesh.IsDestroyed || mesh.IsDestroyQueued || !OwnsDeformationMesh(mesh)) return false;
        if (!ReferenceEquals(mesh, Mesh))
        {
            inputs = GetDeformationInputs(mesh).Prepare(skinning, blendshapes, publish, observePose);
            return true;
        }
        if (skinning)
        {
            mesh.EnsureComputeSkinningBuffers();
            bool gpuOwnedPose = HasExternalSkinPaletteSource || HasGpuDrivenBoneSource;
            if (_deformationGpuOwnedPose != gpuOwnedPose)
            {
                SetField(ref _deformationGpuOwnedPose, gpuOwnedPose, publishNotifications: false);
                ResetSkinPaletteSeedState();
            }
            if (!HasExternalSkinPaletteSource)
            {
                if (!EnsureSkinningBuffers(logWarnings: false)) return false;
                if (!ReferenceEquals(_deformationSeedPalette, ActiveSkinPaletteBuffer))
                {
                    SetField(ref _deformationSeedPalette, ActiveSkinPaletteBuffer, publishNotifications: false);
                    SetField(ref _deformationPoseSettled, false, publishNotifications: false);
                    SetField(ref _hasDeformationSeedFrame, false, publishNotifications: false);
                    ResetSkinPaletteSeedState();
                }
                ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
                if (observePose && !_deformationPoseSettled && (!_hasDeformationSeedFrame || _deformationSeedFrame != frameId))
                {
                    SetField(ref _deformationPoseSettled, ReseedSkinPaletteUntilPoseStable(), publishNotifications: false);
                    SetField(ref _deformationSeedFrame, frameId, publishNotifications: false);
                    SetField(ref _hasDeformationSeedFrame, true, publishNotifications: false);
                }
                if (publish) PushBoneMatricesToGPU();
                else SyncDirtyBoneMatricesToClientBuffer();
            }
        }
        if (blendshapes)
        {
            if (!EnsureBlendshapeBuffers(logWarnings: false))
            {
                if (requireMorphs) return false;
                blendshapes = false;
            }
            if (blendshapes && publish) PushBlendshapeWeightsToGPU();
        }
        BlendshapeResourceSnapshot morphs = CaptureBlendshapeResources();
        inputs = new(skinning ? ActiveSkinPaletteBuffer : null,
            skinning ? ActivePreviousSkinPaletteBuffer : null,
            skinning ? ActiveSkinPaletteBase : 0, skinning ? ActiveSkinPaletteCount : 0,
            skinning && (HasExternalSkinPaletteSource || HasGpuDrivenBoneSource),
            blendshapes ? morphs.ActiveWeights : null,
            blendshapes ? checked((uint)morphs.ActiveCount) : 0,
            SkinnedOutputVersion, morphs.WeightsVersion)
        {
            UsesLocalBoneOwnership = skinning && !HasExternalSkinPaletteSource && HasGpuDrivenBoneSource,
        };
        return true;
    }

    private XRMeshDeformationInputs GetDeformationInputs(XRMesh mesh)
    {
        ValidateResourcePublicationLease();
        if (_deformationInputs.TryGetValue(mesh, out XRMeshDeformationInputs? inputs)) return inputs;
        // Membership changes are cold transitions. Do not retain historical
        // resource owners just because their mesh assets remain cached/alive.
        foreach (var (source, previous) in _deformationInputs)
            if (source.IsDestroyed || !OwnsDeformationMesh(source))
            { _deformationInputs.Remove(source); previous.Dispose(); }
        inputs = new(this, mesh);
        _deformationInputs.Add(mesh, inputs);
        return inputs;
    }

    internal bool OwnsDeformationMesh(XRMesh mesh)
    {
        if (ReferenceEquals(mesh, Mesh)) return true;
        for (int index = 0; index < Submeshes.Count; index++)
            if (ReferenceEquals(Submeshes[index].Mesh, mesh)) return true;
        return false;
    }

    internal void ReleaseUnusedDeformationInputs(XRMesh mesh)
    {
        if (IsDestroyed || IsDestroyQueued) return;
        EnterResourcePublicationLease();
        try
        {
            if ((mesh.IsDestroyed || !OwnsDeformationMesh(mesh)) &&
                _deformationInputs.Remove(mesh, out XRMeshDeformationInputs? inputs)) inputs.Dispose();
        }
        finally { ExitResourcePublicationLease(); }
    }

    /// <summary>Reads a normalized morph control belonging to one authored mesh.</summary>
    public float GetBlendshapeWeightNormalized(XRMesh mesh, uint index)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        EnterResourcePublicationLease();
        try { return ReferenceEquals(mesh, Mesh) ? GetBlendshapeWeightNormalized(index) : GetDeformationInputs(mesh).GetWeight(index); }
        finally { ExitResourcePublicationLease(); }
    }

    /// <summary>Sets a normalized morph control without aliasing another submesh's shape indices.</summary>
    public void SetBlendshapeWeightNormalized(XRMesh mesh, uint index, float weight)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        EnterResourcePublicationLease();
        try
        {
            if (ReferenceEquals(mesh, Mesh)) SetBlendshapeWeightNormalized(index, weight);
            else GetDeformationInputs(mesh).SetWeight(index, weight);
        }
        finally { ExitResourcePublicationLease(); }
    }

    /// <summary>Sets a named normalized control in the selected mesh's own shape table.</summary>
    public void SetBlendshapeWeightNormalized(XRMesh mesh, string name, float weight)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.GetBlendshapeIndex(name, out uint index)) SetBlendshapeWeightNormalized(mesh, index, weight);
    }

    /// <summary>Publishes a GPU palette with the selected mesh's exact palette index order.</summary>
    internal void SetGpuDrivenSkinPaletteSource(XRMesh mesh, object owner, XRDataBuffer palette,
        XRDataBuffer? previous, uint first, uint count)
    {
        EnterResourcePublicationLease();
        try
        {
            if (ReferenceEquals(mesh, Mesh)) SetGpuDrivenSkinPaletteSource(owner, palette, previous, first, count);
            else GetDeformationInputs(mesh).SetExternalPalette(owner, palette, previous, first, count);
        }
        finally { ExitResourcePublicationLease(); }
    }

    internal void ClearGpuDrivenSkinPaletteSource(XRMesh mesh, object owner)
    {
        EnterResourcePublicationLease();
        try
        {
            if (ReferenceEquals(mesh, Mesh)) ClearGpuDrivenSkinPaletteSource(owner);
            else if (_deformationInputs.TryGetValue(mesh, out XRMeshDeformationInputs? inputs)) inputs.ClearExternalPalette(owner);
        }
        finally { ExitResourcePublicationLease(); }
    }

    private void DestroyDeformationInputs()
    {
        foreach (XRMeshDeformationInputs inputs in _deformationInputs.Values) inputs.Dispose();
        _deformationInputs.Clear();
    }

    internal static SkinPaletteMatrix ComposeSkinPalette(XRMesh mesh, in Matrix4x4 inverseBind, in Matrix4x4 current)
        => SkinPaletteMatrix.FromRowVectorMatrix((mesh.BindRootMatrix ?? Matrix4x4.Identity) * inverseBind * current);
}
