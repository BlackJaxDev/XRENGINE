namespace XREngine.Components.Capture.Lights
{
    /// <summary>
    /// Determines how the light is handled by the engine for optimization purposes.
    /// </summary>
    public enum ELightType
    {
        /// <summary>
        /// Movable light that is evaluated every frame.
        /// </summary>
        Dynamic,

        /// <summary>
        /// Movable light that can reuse cached shadow data while stationary.
        /// </summary>
        DynamicCached,

        /// <summary>
        /// Immovable light intended for baked lighting or cached shadow data.
        /// </summary>
        Static,
    }
}
