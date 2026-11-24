using XREngine.Rendering;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>
    /// Binds the source back face options to the generic Uber back face controls.
    /// </summary>
    /// <remarks>
    /// The exact-name pass binds the theme index, the mask controls, the replace-alpha toggle, and the hue shift
    /// controls. This method sets the defaults that are not zero and converts the hue shift speed to the Uber
    /// time unit.
    /// </remarks>
    private static void BindSourceToonBackFaceOptions(XRMaterial material, SerializedMaterialDocument document)
    {
        SetFloat(material, "_BackFaceHueShiftSpeed", GetFloat(document, "_BackFaceHueShiftSpeed") * SourceToonTimeScale);
        SetInt(material, "_BackFaceHueSelectOrShift", GetInt(document, "_BackFaceHueSelectOrShift", 1));
        SetFloat(material, "_BackFaceDetailIntensity", GetFloat(document, "_BackFaceDetailIntensity", 1.0f));
        SetFloat(material, "_BackFaceEmissionLimiter", GetFloat(document, "_BackFaceEmissionLimiter", 1.0f));
    }
}
