namespace XREngine.Components.Scene.Mesh;

/// <summary>Names the optional renderer preparation timing counters.</summary>
public enum RenderableMeshStage
{
    ApplyPendingRenderMatrixUpdates,
    ApplyPendingMatrixState,
    ProcessPendingSkinnedBounds,
    ApplyPendingBoneBounds,
    SwapPendingRenderCommand,
    BeforeAdd,
    BeforeAddSkinningState,
    BeforeAddCullingBounds,
    BeforeAddCommandState,
    BeforeAddMaterialPasses,
    BeforeAddDiagnostics,
    BoneRenderMatrixChanged,
    RootBoneRenderMatrixChanged,
    PublishRenderCommandCullingVolume,
    Count,
}
