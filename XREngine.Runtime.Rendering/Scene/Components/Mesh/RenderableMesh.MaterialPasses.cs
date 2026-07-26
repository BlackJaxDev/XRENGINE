using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Components.Scene.Mesh;

public partial class RenderableMesh
{
    private readonly RenderCommandMesh3D _materialOutlineCommand = new(0)
    {
        Enabled = false,
        ForceCpuRendering = true,
        GpuProfilingLabel = "MaterialOutline",
    };

    private void SyncMaterialPassCommands(
        XRMeshRenderer? renderer,
        XRMaterial? material,
        bool isShadowCollection)
    {
        _rc.Enabled = ShouldSubmitPrimaryCommand(material, isShadowCollection);

        MaterialPassDefinition? outlinePass = null;
        bool outlineEnabled = !isShadowCollection &&
            renderer is not null &&
            material is not null &&
            material.PassSet.TryGetPass(EMaterialPassIdentity.Outline, out outlinePass) &&
            outlinePass.Enabled;

        XRMaterial? outlineMaterial = outlineEnabled ? material!.OutlinePassVariant : null;
        if (outlineMaterial is null || outlinePass is null)
        {
            _materialOutlineCommand.Enabled = false;
            _materialOutlineCommand.MaterialOverride = null;
            return;
        }

        _materialOutlineCommand.Mesh = renderer;
        _materialOutlineCommand.WorldMatrix = _rc.WorldMatrix;
        _materialOutlineCommand.WorldMatrixIsModelMatrix = _rc.WorldMatrixIsModelMatrix;
        _materialOutlineCommand.WorldCullingVolumeOverride = _rc.WorldCullingVolumeOverride;
        _materialOutlineCommand.Instances = _rc.Instances;
        _materialOutlineCommand.MaterialOverride = outlineMaterial;
        _materialOutlineCommand.RenderOptionsOverride = outlinePass.RenderOptions;
        _materialOutlineCommand.RenderPass = outlinePass.RenderPass;
        _materialOutlineCommand.Enabled = true;
    }

    private static bool ShouldSubmitPrimaryCommand(XRMaterial? material, bool isShadowCollection)
    {
        if (material is null || material.PassSet.Passes.Length == 0)
            return true;

        EMaterialPassIdentity identity = isShadowCollection
            ? EMaterialPassIdentity.Shadow
            : EMaterialPassIdentity.Base;
        return material.PassSet.TryGetPass(identity, out MaterialPassDefinition pass) && pass.Enabled;
    }
}
