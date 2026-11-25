using XREngine.Components;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.UI;

public partial class UITextComponent
{
    private XRMeshRenderer? _ownedStandaloneRenderer;
    private XRMesh? _ownedStandaloneGeometry;
    private XRMaterial? _ownedStandaloneMaterial;
    private bool _retiringStandalone;
    private bool _installingStandalone;

    private bool CanUseSharedTextBatch()
    {
        if (GetType() != typeof(UITextComponent) || DisableBatching || AnimatableTransforms ||
            NonVertexShadersOverride is not null || !UIBatchCollector.HasWebGpuRasterProfile(RenderParameters) ||
            RenderCommand2D.MaterialOverride is not null || HasAuthoredWorldMaterialOverride)
            return false;
        if (Mesh is not null && (!ReferenceEquals(Mesh, _ownedStandaloneRenderer) ||
            !ReferenceEquals(Material, _ownedStandaloneMaterial) ||
            !Mesh.HasOnlySettingUniformsHandler(MeshRend_SettingUniforms) ||
            !Mesh.HasOnlyRenderDataPreparationHandler(MeshRend_PreparingRenderData)))
            return false;
        return Mesh is not null || Material is null;
    }

    private bool CanDeferStandaloneText()
    {
        if (!CanUseSharedTextBatch())
            return false;
        UICanvasTransform? canvas = BoundableTransform.ParentCanvas;
        UICanvasComponent? owner = canvas?.SceneNode?.GetComponent<UICanvasComponent>();
        return owner?.BatchCollector.Enabled == true &&
            (canvas!.DrawSpace == ECanvasDrawSpace.Screen || owner.UseOffscreenRenderingForNonScreenSpaces());
    }

    private void RetireOwnedStandaloneIfBatched()
    {
        if (!CanDeferStandaloneText() || Mesh is not { } renderer ||
            !ReferenceEquals(renderer, _ownedStandaloneRenderer))
            return;
        DestroyOwnedStandalone();
    }

    private void DestroyOwnedStandalone()
    {
        if (_ownedStandaloneRenderer is not { } renderer)
            return;
        if (_retiringStandalone)
            return;
        XRMesh? geometry = _ownedStandaloneGeometry;
        XRMaterial? material = _ownedStandaloneMaterial;
        XRDataBuffer<System.Numerics.Vector4>? transforms = _transformsBuffer;
        XRDataBuffer<System.Numerics.Vector4>? uvs = _uvsBuffer;
        XRDataBuffer<float>? rotations = _rotationsBuffer;
        _retiringStandalone = true;
        try
        {
            // Mesh clears Material and can synchronously invoke authored callbacks.
            // Keep the old ownership record alive until the final command identity is known.
            if (ReferenceEquals(Mesh, renderer))
                Mesh = null;
            if (ReferenceEquals(Mesh, renderer))
                return;
            renderer.SettingUniforms -= MeshRend_SettingUniforms;
            renderer.PreparingRenderData -= MeshRend_PreparingRenderData;
            new UITextStandaloneRetirement(this, renderer, geometry, material,
                transforms, uvs, rotations).Queue();
            if (!ReferenceEquals(_ownedStandaloneRenderer, renderer))
                return;
            _ownedStandaloneRenderer = null;
            _ownedStandaloneGeometry = null;
            _ownedStandaloneMaterial = null;
            if (ReferenceEquals(_transformsBuffer, transforms)) _transformsBuffer = null;
            if (ReferenceEquals(_uvsBuffer, uvs)) _uvsBuffer = null;
            if (ReferenceEquals(_rotationsBuffer, rotations)) _rotationsBuffer = null;
            _writtenGlyphBuffer = null;
            _writtenGlyphLayout = null;
            _createdAtlasType = null;
            _pushFull = true;
            _dataChanged = true;
        }
        finally { _retiringStandalone = false; }
    }

    protected override bool ShouldRender3D()
    {
        if (!base.ShouldRender3D())
            return false;
        VerifyCreated(false, Font?.Atlas, requireStandalone: true);
        return Mesh is not null;
    }

    protected override bool ShouldRender2D(RenderInfo info, RenderCommandCollection passes,
        IRuntimeRenderCamera? camera)
    {
        if (!base.ShouldRender2D(info, passes, camera))
            return false;
        // A collector refusal can fall through to the individual command. It must
        // have actual storage before that command is admitted.
        VerifyCreated(false, Font?.Atlas, requireStandalone: true);
        return Mesh is not null;
    }
}
