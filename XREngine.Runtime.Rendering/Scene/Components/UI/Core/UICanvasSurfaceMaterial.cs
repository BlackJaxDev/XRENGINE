using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI;

/// <summary>Defines the cooked world surface for a linear premultiplied canvas target.</summary>
public static class UICanvasSurfaceMaterial
{
    /// <summary>Creates the unlit sorted surface without changing desktop canvas materials.</summary>
    public static XRMaterial Create(XRTexture2D texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        XRMaterial material = new(Array.Empty<ShaderVar>(), [texture], Array.Empty<XRShader>())
        {
            Name = "Canvas linear premultiplied surface",
            EngineSemantic = EngineMaterialSemanticIdentity.UICanvasSurfaceV1,
            TransparencyMode = ETransparencyMode.PremultipliedAlpha,
            AdvancedLatePassMetadata = new(EAdvancedLatePassKind.SortedAlpha,
                participatesInMotionVectors: false, writesDepth: false, isOrderDependent: true),
        };
        material.RenderOptions.CullMode = ECullMode.None;
        material.RenderOptions.DepthTest.Function = EComparison.Lequal;
        return material;
    }
}
