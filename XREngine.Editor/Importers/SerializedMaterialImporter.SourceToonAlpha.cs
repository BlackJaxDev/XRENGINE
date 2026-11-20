using XREngine.Rendering;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>
    /// Binds the source alpha options to the generic Uber alpha controls.
    /// </summary>
    /// <remarks>
    /// The source shader tests each option toggle at run time and keeps the option values when the toggle is off.
    /// The binding therefore copies the toggle state, and a disabled option stays inactive with its values preserved.
    /// Names that are the same in the source and in the Uber shader are bound by the exact-name pass. This method
    /// binds only the renamed controls and the controls whose state depends on another property.
    /// </remarks>
    private static void BindSourceToonAlphaOptions(XRMaterial material, SerializedMaterialDocument document)
    {
        // Opaque presets set force opaque to 1 and cutout or transparent presets set it to 0. The
        // property default is 1, so a material that does not store it is opaque.
        SetFloat(material, "_AlphaForceOpaque", document.TryGetFloat("_AlphaForceOpaque", out float forceOpaque) ? forceOpaque : 1.0f);

        SetFloat(material, "_DistanceFade", document.TryGetPositive("_AlphaDistanceFade") ? 1.0f : 0.0f);
        SetInt(material, "_DistanceFadePosition", GetInt(document, "_AlphaDistanceFadeType", 1));
        SetFloat(material, "_DistanceFadeMin", GetFloat(document, "_AlphaDistanceFadeMin"));
        SetFloat(material, "_DistanceFadeMax", GetFloat(document, "_AlphaDistanceFadeMax"));
        SetFloat(material, "_DistanceFadeNearAlpha", GetFloat(document, "_AlphaDistanceFadeMinAlpha"));
        SetFloat(material, "_DistanceFadeFarAlpha", GetFloat(document, "_AlphaDistanceFadeMaxAlpha", 1.0f));
        SetInt(material, "_DistanceFadeGlobalMask", GetInt(document, "_AlphaDistanceFadeGlobalMask"));

        SetFloat(material, "_AlphaDither", document.TryGetPositive("_AlphaDithering") ? 1.0f : 0.0f);
        SetFloat(material, "_AlphaDitherGradient", GetFloat(document, "_AlphaDitherGradient", 0.1f));
        SetFloat(material, "_AlphaDitherBias", GetFloat(document, "_AlphaDitherBias"));

        // Sharpening applies only while alpha to coverage is on.
        SetFloat(
            material,
            "_AlphaSharpenedA2C",
            document.TryGetPositive("_AlphaToCoverage") && document.TryGetPositive("_AlphaSharpenedA2C") ? 1.0f : 0.0f);
        SetFloat(material, "_AlphaMipScale", GetFloat(document, "_AlphaMipScale", 0.25f));
    }
}
