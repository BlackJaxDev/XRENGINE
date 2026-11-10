using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Components.Scene.Mesh;

public partial class RenderableMesh
{
    internal IRenderCommandMesh MaterialOutlineCommand => _materialOutlineCommand;

    private readonly RenderCommandMesh3D _materialOutlineCommand = new(0)
    {
        Enabled = false,
        ForceCpuRendering = true,
        SupplementalMaterialPass = EMaterialPassIdentity.Outline,
        GpuProfilingLabel = "MaterialOutline",
    };

    private void SyncMaterialPassCommands(
        XRMeshRenderer? renderer,
        XRMaterial? material,
        bool isShadowCollection)
    {
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

    internal bool IsPrimaryCommand(RenderCommand command) => ReferenceEquals(command, _rc);

    /// <summary>
    /// Tells if a collection of the given kind takes this command.
    /// A shadow collection takes no supplemental material pass.
    /// </summary>
    /// <remarks>
    /// All collections of one frame share the command objects, and a swap publishes their state one time.
    /// Thus a shadow collection must skip the outline command here and must not disable it.
    /// </remarks>
    internal bool ShouldCollectCommand(RenderCommand command, bool isShadowCollection)
    {
        if (ReferenceEquals(command, _rc))
            return ShouldCollectPrimaryCommand(isShadowCollection);

        return !isShadowCollection || !ReferenceEquals(command, _materialOutlineCommand);
    }

    internal bool ShouldCollectPrimaryCommand(bool isShadowCollection)
        => IsPrimaryMaterialPassEnabled(
            MaterialOverride ?? CurrentLODRenderer?.Material, isShadowCollection);

    internal static bool IsPrimaryMaterialPassEnabled(XRMaterial? material, bool isShadowCollection)
    {
        if (material is null || material.PassSet.Passes.Length == 0)
            return true;

        EMaterialPassIdentity identity = isShadowCollection
            ? EMaterialPassIdentity.Shadow
            : EMaterialPassIdentity.Base;
        return material.PassSet.TryGetPass(identity, out MaterialPassDefinition pass) && pass.Enabled;
    }
}
