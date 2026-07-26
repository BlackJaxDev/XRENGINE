namespace XREngine.Rendering;

/// <summary>
/// Reusable slot contracts used by importers, shader generation, and
/// consistency tests. Slot counts remain compile-time specialization axes.
/// </summary>
public static class UberMaterialSlotSchemas
{
    public static UberMaterialSlotSchema Decals { get; } = new(
        "decals", "decals", 4,
        ["Texture", "Mask", "Color", "Uv", "Pan", "BlendMode", "BlendAlpha"],
        [EUberSamplerRole.Color, EUberSamplerRole.MaskWhite]);

    public static UberMaterialSlotSchema Matcaps { get; } = new(
        "matcaps", "matcap", 4,
        ["Texture", "Mask", "Color", "Uv", "BlendMode", "Strength"],
        [EUberSamplerRole.Color, EUberSamplerRole.MaskWhite]);

    public static UberMaterialSlotSchema Emissions { get; } = new(
        "emissions", "emission", 4,
        ["Texture", "Mask", "Color", "Uv", "Pan", "Strength"],
        [EUberSamplerRole.EmissionBlack, EUberSamplerRole.MaskWhite]);

    public static UberMaterialSlotSchema Rims { get; } = new(
        "rims", "rim-lighting", 2,
        ["Texture", "Mask", "Color", "BlendMode", "Strength"],
        [EUberSamplerRole.Color, EUberSamplerRole.MaskWhite]);

    public static IReadOnlyList<UberMaterialSlotSchema> All { get; } =
        [Decals, Matcaps, Emissions, Rims];
}
