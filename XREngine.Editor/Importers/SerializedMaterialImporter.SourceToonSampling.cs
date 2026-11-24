using XREngine.Rendering;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>
    /// Sets the defaults of the source stochastic sampling and detail mask controls.
    /// </summary>
    /// <remarks>
    /// The exact-name pass binds the stochastic mode, the per-texture stochastic toggles, pixel mode, ignore
    /// alpha, and the detail theme and global mask indices. This method sets the defaults that are not zero, so
    /// that a material that does not store a control gets the source default.
    /// </remarks>
    private static void BindSourceToonSamplingOptions(XRMaterial material, SerializedMaterialDocument document)
    {
        SetFloat(material, "_StochasticDeliotHeitzDensity", GetFloat(document, "_StochasticDeliotHeitzDensity", 1.0f));
        SetFloat(material, "_StochasticHexGridDensity", GetFloat(document, "_StochasticHexGridDensity", 1.0f));
        SetFloat(material, "_StochasticHexFallOffContrast", GetFloat(document, "_StochasticHexFallOffContrast", 0.6f));
        SetFloat(material, "_StochasticHexFallOffPower", GetFloat(document, "_StochasticHexFallOffPower", 7.0f));

        SetInt(material, "_DetailTexGlobalMaskBlendType", GetInt(document, "_DetailTexGlobalMaskBlendType", 2));
        SetInt(material, "_DetailNormalGlobalMaskBlendType", GetInt(document, "_DetailNormalGlobalMaskBlendType", 2));
    }
}
