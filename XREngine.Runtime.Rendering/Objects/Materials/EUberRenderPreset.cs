namespace XREngine.Rendering;

/// <summary>
/// Identifies a render preset of the Uber shader. A preset sets the blend state, depth write, render pass,
/// alpha cutoff, and shader blend mode of a material together.
/// </summary>
public enum EUberRenderPreset
{
    /// <summary>No blending. Writes depth.</summary>
    Opaque = 0,

    /// <summary>No blending. Discards fragments below the alpha cutoff and writes depth.</summary>
    Cutout = 1,

    /// <summary>Alpha blending that also discards fragments below the alpha cutoff and writes depth.</summary>
    TransparentCutout = 2,

    /// <summary>Alpha blending. Does not write depth.</summary>
    Fade = 3,

    /// <summary>Premultiplied alpha blending. Does not write depth.</summary>
    Transparent = 4,

    /// <summary>Adds the color to the destination. Does not write depth.</summary>
    Additive = 5,

    /// <summary>Adds the color scaled by the inverted destination color. Does not write depth.</summary>
    SoftAdditive = 6,

    /// <summary>Multiplies the destination by the color. Does not write depth.</summary>
    Multiply = 7,

    /// <summary>Multiplies the destination by two times the color. Does not write depth.</summary>
    Multiply2X = 8,
}
