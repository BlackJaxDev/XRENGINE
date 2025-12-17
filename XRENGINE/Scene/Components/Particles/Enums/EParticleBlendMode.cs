namespace XREngine.Scene.Components.Particles.Enums;
#region Enums

public enum EParticleBlendMode
{
    /// <summary>
    /// Standard alpha blending.
    /// </summary>
    AlphaBlend,

    /// <summary>
    /// Additive blending for glowing effects.
    /// </summary>
    Additive,

    /// <summary>
    /// Soft additive blending.
    /// </summary>
    SoftAdditive,

    /// <summary>
    /// Multiply blending for darkening effects.
    /// </summary>
    Multiply,

    /// <summary>
    /// Pre-multiplied alpha blending.
    /// </summary>
    Premultiplied
}

#endregion
