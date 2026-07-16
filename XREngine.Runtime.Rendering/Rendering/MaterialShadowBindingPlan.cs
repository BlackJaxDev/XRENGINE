using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public sealed class MaterialShadowBindingPlan(ShaderVar[] parameters, int[] textureIndices)
{
    public ShaderVar[] Parameters { get; } = parameters;
    public int[] TextureIndices { get; } = textureIndices;
}
