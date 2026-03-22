namespace XREngine
{
    /// <summary>
    /// Specifies the anti-aliasing mode used for rendering.
    /// </summary>
    public enum EAntiAliasingMode
    {
        /// <summary>No anti-aliasing.</summary>
        None,
        /// <summary>Multisample anti-aliasing.</summary>
        Msaa,
        /// <summary>Fast approximate anti-aliasing.</summary>
        Fxaa,
        /// <summary>Subpixel morphological anti-aliasing.</summary>
        Smaa,
        /// <summary>Temporal anti-aliasing.</summary>
        Taa,
        /// <summary>Temporal super-resolution.</summary>
        Tsr
    }
}
