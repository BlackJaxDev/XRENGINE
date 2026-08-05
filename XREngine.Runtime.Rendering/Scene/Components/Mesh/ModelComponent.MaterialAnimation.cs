using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Scene.Mesh;

public partial class ModelComponent
{
    /// <summary>
    /// Resolves and prewarms a material animation target. Animation
    /// member reflection caches the returned binding for frame updates.
    /// </summary>
    public MaterialAnimationBinding GetMaterialAnimationBinding(
        int materialSlot,
        string sourceProperty,
        int component)
        => new(this, materialSlot, sourceProperty, component);

    internal void RefreshMaterialAnimationBounds(int materialSlot)
    {
        if ((uint)materialSlot < (uint)Meshes.Count)
            Meshes[materialSlot].RefreshVertexEffectCullingBounds();
    }

    internal XRMaterial? ResolveMaterialAnimationSlot(int materialSlot)
    {
        if (Model is null || materialSlot < 0 || materialSlot >= Model.Meshes.Count)
            return null;

        SubMesh subMesh = Model.Meshes[materialSlot];
        return subMesh.LODs.Count > 0 ? subMesh.LODs.Min?.Material : null;
    }
}
