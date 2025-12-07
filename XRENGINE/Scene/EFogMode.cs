namespace XREngine.Scene
{
    /// <summary>
    /// Defines the fog calculation mode.
    /// </summary>
    public enum EFogMode
    {
        /// <summary>
        /// Linear fog falloff based on distance.
        /// </summary>
        Linear,

        /// <summary>
        /// Exponential fog falloff.
        /// </summary>
        Exponential,

        /// <summary>
        /// Squared exponential fog falloff for denser fog.
        /// </summary>
        ExponentialSquared,

        /// <summary>
        /// Height-based fog that's denser near the ground.
        /// </summary>
        HeightBased,
    }
}
