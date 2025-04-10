namespace XREngine.Rendering.Models.Materials
{
    /// <summary>
    /// Defines operations that can be performed on the stencil buffer.
    /// </summary>
    public enum EStencilOp
    {
        /// <summary>
        /// Sets the stencil buffer value to 0.
        /// </summary>
        Zero = 0,

        /// <summary>
        /// Bitwise inverts the current stencil buffer value.
        /// </summary>
        Invert = 5386,

        /// <summary>
        /// Keeps the current value in the stencil buffer unchanged.
        /// </summary>
        Keep = 7680,

        /// <summary>
        /// Replaces the stencil buffer value with the reference value.
        /// </summary>
        Replace = 7681,

        /// <summary>
        /// Increments the current stencil buffer value. Clamps to the maximum value.
        /// </summary>
        Incr = 7682,

        /// <summary>
        /// Decrements the current stencil buffer value. Clamps to 0.
        /// </summary>
        Decr = 7683,

        /// <summary>
        /// Increments the current stencil buffer value. Wraps to 0 when the maximum value is exceeded.
        /// </summary>
        IncrWrap = 34055,

        /// <summary>
        /// Decrements the current stencil buffer value. Wraps to the maximum value when 0 is exceeded.
        /// </summary>
        DecrWrap = 34056
    }
}
