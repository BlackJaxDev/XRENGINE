using System.Numerics;
using XREngine.Rendering;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>
    /// The factor that converts a source animation speed to a speed per second. The source time value
    /// advances by 1 every 20 seconds.
    /// </summary>
    private const float SourceToonTimeScale = 0.05f;

    /// <summary>
    /// Binds the source color adjustment options to the generic Uber color adjustment controls.
    /// </summary>
    /// <remarks>
    /// The exact-name pass binds the controls that have the same name in the source and in the Uber shader.
    /// This method sets the defaults of controls that some source versions do not have, and converts the hue
    /// shift speed to the Uber time unit. A version without the clamp option always clamps the adjusted color,
    /// so the clamp default is on.
    /// </remarks>
    private static void BindSourceToonColorAdjustOptions(XRMaterial material, SerializedMaterialDocument document)
    {
        SetFloat(material, "_MainHueShiftSpeed", GetFloat(document, "_MainHueShiftSpeed") * SourceToonTimeScale);
        SetInt(material, "_MainHueShiftSelectOrShift", GetInt(document, "_MainHueShiftSelectOrShift", 1));
        SetFloat(material, "_MainGamma", GetFloat(document, "_MainGamma", 1.0f));
        SetFloat(material, "_MainColorAdjustClamp", GetFloat(document, "_MainColorAdjustClamp", 1.0f));
        SetVector4(
            material,
            "_MainTintColor",
            document.TryGetVector("_MainTintColor", out Vector4 tint) ? tint : new Vector4(1.0f, 1.0f, 1.0f, 0.0f));

        SetInt(material, "_MainHueGlobalMaskBlendType", GetInt(document, "_MainHueGlobalMaskBlendType", 2));
        SetInt(material, "_MainSaturationGlobalMaskBlendType", GetInt(document, "_MainSaturationGlobalMaskBlendType", 2));
        SetInt(material, "_MainBrightnessGlobalMaskBlendType", GetInt(document, "_MainBrightnessGlobalMaskBlendType", 2));
        SetInt(material, "_MainGammaGlobalMaskBlendType", GetInt(document, "_MainGammaGlobalMaskBlendType", 2));
    }
}
