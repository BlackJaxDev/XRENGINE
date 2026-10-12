using XREngine.Rendering;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>Preserve source normal-map green inversion without changing shared texture bytes.</summary>
    private static void BindSourceToonNormalMetadata(XRMaterial material)
    {
        SetInt(material, "_BumpMapFlipGreen", 0);
        SetInt(material, "_DetailNormalMapFlipGreen", 0);
        foreach (XRTexture? texture in material.Textures)
        {
            if (texture?.SamplerName == "_BumpMap")
                SetInt(material, "_BumpMapFlipGreen", texture.ImportedNormalMapFlipGreen ? 1 : 0);
            else if (texture?.SamplerName == "_DetailNormalMap")
                SetInt(material, "_DetailNormalMapFlipGreen", texture.ImportedNormalMapFlipGreen ? 1 : 0);
        }
    }
}
