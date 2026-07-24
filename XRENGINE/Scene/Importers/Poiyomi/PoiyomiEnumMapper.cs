using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// Translates serialized Poiyomi enum values into XRENGINE values.
/// </summary>
internal static class PoiyomiEnumMapper
{
    public static int LightingMode(UnityMaterialDocument document, ICollection<MaterialConversionDiagnostic> diagnostics)
        => MapInt(document, "_LightingMode", 5, diagnostics,
            (0, 0), (1, 1), (2, 2), (3, 3), (4, 4), (5, 5), (6, 6), (7, 7), (8, 8));

    public static int AlphaMaskMode(UnityMaterialDocument document, ICollection<MaterialConversionDiagnostic> diagnostics)
        => MapInt(document, "_MainAlphaMaskMode", 0, diagnostics,
            (0, 0), (1, 1), (2, 2), (3, 3), (4, 4));

    public static int MatcapUvMode(UnityMaterialDocument document, ICollection<MaterialConversionDiagnostic> diagnostics)
        => MapInt(document, "_MatcapUVMode", 1, diagnostics, (0, 0), (1, 1), (2, 2));

    public static int RimStyle(UnityMaterialDocument document, ICollection<MaterialConversionDiagnostic> diagnostics)
        => MapInt(document, "_RimStyle", 0, diagnostics, (0, 0), (1, 1), (2, 2));

    public static int RimBlendMode(UnityMaterialDocument document, ICollection<MaterialConversionDiagnostic> diagnostics)
        => MapInt(document, "_RimBlendMode", 1, diagnostics, (0, 0), (1, 1), (2, 1), (3, 2));

    public static int DissolveMode(UnityMaterialDocument document, ICollection<MaterialConversionDiagnostic> diagnostics)
        => MapInt(document, "_DissolveType", 0, diagnostics, (1, 0), (2, 2), (3, 1), (4, 1));

    public static int SpecularType(UnityMaterialDocument document, ICollection<MaterialConversionDiagnostic> diagnostics)
        => MapInt(document, "_Is_SpecularToHighColor", 0, diagnostics, (0, 1), (1, 0));

    public static int ParallaxMode(UnityMaterialDocument document, ICollection<MaterialConversionDiagnostic> diagnostics)
        => MapInt(document, "_ParallaxInternalHeightmapMode", 0, diagnostics, (0, 0), (1, 1));

    public static int Identity(
        UnityMaterialDocument document,
        string sourceProperty,
        int fallback,
        int minimum,
        int maximum,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        int count = maximum - minimum + 1;
        (int Source, int Destination)[] mappings = new (int, int)[count];
        for (int index = 0; index < count; index++)
        {
            int value = minimum + index;
            mappings[index] = (value, value);
        }

        return MapInt(document, sourceProperty, fallback, diagnostics, mappings);
    }

    public static int TextureChannel(
        UnityMaterialDocument document,
        string sourceProperty,
        int fallback,
        ICollection<MaterialConversionDiagnostic> diagnostics)
        => MapInt(document, sourceProperty, fallback, diagnostics, (0, 0), (1, 1), (2, 2), (3, 3));

    public static int UvChannel(
        UnityMaterialDocument document,
        string sourceProperty,
        int fallback,
        ICollection<MaterialConversionDiagnostic> diagnostics)
        => MapInt(document, sourceProperty, fallback, diagnostics, (0, 0), (1, 1), (2, 2), (3, 3));

    public static ECullMode CullMode(
        UnityMaterialDocument document,
        ECullMode fallback,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        if (!document.TryGetFloat("_Cull", out float raw))
            return fallback;

        int value = (int)MathF.Round(raw);
        return value switch
        {
            0 => ECullMode.None,
            1 => ECullMode.Front,
            2 => ECullMode.Back,
            _ => ReportOutOfRange("_Cull", value, fallback, diagnostics),
        };
    }

    public static ETransparencyMode TransparencyMode(
        UnityMaterialDocument document,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        if (document.TryGetPositive("_AlphaToCoverage"))
            return ETransparencyMode.AlphaToCoverage;

        int mode = document.TryGetInt("_Mode", out int authoredMode)
            ? authoredMode
            : document.CustomRenderQueue >= 3000 ? 3 : 0;

        return mode switch
        {
            0 => ETransparencyMode.Opaque,
            1 or 9 => ETransparencyMode.Masked,
            2 or 3 or 5 or 6 or 7 => ETransparencyMode.WeightedBlendedOit,
            4 => ETransparencyMode.Additive,
            _ => ReportOutOfRange("_Mode", mode, ETransparencyMode.Opaque, diagnostics),
        };
    }

    private static int MapInt(
        UnityMaterialDocument document,
        string sourceProperty,
        int fallback,
        ICollection<MaterialConversionDiagnostic> diagnostics,
        params (int Source, int Destination)[] mappings)
    {
        int sourceValue;
        if (!document.TryGetInt(sourceProperty, out sourceValue))
        {
            if (!document.TryGetFloat(sourceProperty, out float floatValue))
                return fallback;

            sourceValue = (int)MathF.Round(floatValue);
        }

        foreach ((int source, int destination) in mappings)
        {
            if (source == sourceValue)
                return destination;
        }

        return ReportOutOfRange(sourceProperty, sourceValue, fallback, diagnostics);
    }

    private static T ReportOutOfRange<T>(
        string sourceProperty,
        int sourceValue,
        T fallback,
        ICollection<MaterialConversionDiagnostic> diagnostics)
    {
        diagnostics.Add(new MaterialConversionDiagnostic(
            MaterialConversionDiagnosticCodes.EnumValueOutOfRange,
            MaterialConversionDiagnosticSeverity.Warning,
            $"Serialized enum value {sourceValue} is not supported by the pinned Poiyomi 9.3.64 mapping; using '{fallback}'.",
            sourceProperty));
        return fallback;
    }
}
