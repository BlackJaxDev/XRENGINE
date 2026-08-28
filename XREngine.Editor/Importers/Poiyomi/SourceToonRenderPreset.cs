namespace XREngine.Scene.Importers.SourceToon;

/// <summary>
/// Rendering presets exposed by Poiyomi Toon 9.3.64's <c>_Mode</c> property.
/// Values intentionally match the serialized Unity material values.
/// </summary>
public enum SourceToonRenderPreset
{
    Opaque = 0,
    Cutout = 1,
    Fade = 2,
    Transparent = 3,
    Additive = 4,
    SoftAdditive = 5,
    Multiplicative = 6,
    Multiplicative2X = 7,
    TransClipping = 9,
}
