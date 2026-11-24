using System.Numerics;
using XREngine.Rendering;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>
    /// Packs the source matcap circle mask, alpha, and hue selection options of one slot.
    /// </summary>
    /// <remarks>
    /// The first source slot stores its circle mask under the <c>_Matcap0</c> prefix. The other slots use the
    /// same prefix as their other options. The intensity-to-alpha weight is zero when that option is off.
    /// </remarks>
    private static void BindSourceToonMatcapOptions(
        XRMaterial material,
        SerializedMaterialDocument document,
        int slot,
        string matcapPrefix)
    {
        bool enabled = document.TryGetPositive($"{matcapPrefix}Enable");
        string circlePrefix = slot == 0 ? "_Matcap0" : matcapPrefix;
        // X is not used. The surface feature binder writes the rotation to _MatcapSlotRotation.
        SetVector4(material, $"_MatcapSlotShape{slot}", new Vector4(
            0.0f,
            document.TryGetPositive($"{circlePrefix}CircleMaskEnabled") ? 1.0f : 0.0f,
            GetFloat(document, $"{circlePrefix}CircleMaskBorder", 0.45f),
            GetFloat(document, $"{circlePrefix}CircleMaskBlur", 0.1f)));

        bool applyToAlpha = enabled && document.TryGetPositive($"{matcapPrefix}ApplyToAlphaEnabled");
        SetVector4(material, $"_MatcapSlotAlpha{slot}", new Vector4(
            GetFloat(document, $"{matcapPrefix}AlphaOverride"),
            applyToAlpha ? GetFloat(document, $"{matcapPrefix}ApplyToAlphaBlending", 1.0f) : 0.0f,
            GetInt(document, $"{matcapPrefix}ApplyToAlphaSourceBlend"),
            GetInt(document, $"{matcapPrefix}ApplyToAlphaBlendType")));

        // The source value 0 selects the hue and 1 shifts it. A missing value shifts the hue.
        SetInt(material, $"_MatcapSlotHueSelect{slot}",
            GetInt(document, $"{matcapPrefix}HueSelectOrShift", 1) == 0 ? 1 : 0);
    }
}
