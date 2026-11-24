namespace XREngine
{
    /// <summary>
    /// Selects the reconstruction filter applied by MSAA, TSR, and TAA passes.
    /// </summary>
    public enum EAntiAliasingFilterMode
    {
        /// <summary>Uses the built-in default reconstruction filter of each anti-aliasing mode.</summary>
        Default = 0,
        /// <summary>Uses a 1x1 box reconstruction filter without neighbor overlap or ringing.</summary>
        Box1x1 = 1,
        /// <summary>Uses a 2x2 bilinear tent reconstruction filter over neighboring subpixel samples or texels.</summary>
        Tent2x2 = 2
    }
}
