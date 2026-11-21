namespace XREngine.Rendering;

public partial class XRMaterial
{
    private bool _advancedSurfaceDecal;

    /// <summary>
    /// Marks a masked material as a mesh surface decal. The Advanced Render Pipeline on Vulkan
    /// draws it into the decal buffer, and native shading blends it over the surface below
    /// before lighting. It never enters the visibility buffer or the shadow lanes. Renderers
    /// without the decal lane draw the material with its masked transparency mode.
    /// </summary>
    public bool AdvancedSurfaceDecal
    {
        get => _advancedSurfaceDecal;
        set => SetField(ref _advancedSurfaceDecal, value);
    }
}
