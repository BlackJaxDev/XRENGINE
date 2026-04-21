namespace XREngine.Components.Capture.Lights
{
    /// <summary>
    /// Controls the soft shadow technique used when sampling shadow maps.
    /// </summary>
    public enum ESoftShadowMode
    {
        /// <summary>
        /// No soft shadowing. Uses PCF or single-sample hard shadows.
        /// </summary>
        Hard = 0,

        /// <summary>
        /// Fixed-radius Poisson-disk soft shadows (the original "PCSS" toggle).
        /// Penumbra width is constant regardless of blocker distance.
        /// </summary>
        PCSS = 1,

        /// <summary>
        /// Fixed-radius Vogel-disk soft shadows with a configurable tap count.
        /// Produces a more even low-sample distribution than the legacy Poisson disk path.
        /// </summary>
        VogelDisk = 3,

        /// <summary>
        /// Contact-hardening soft shadows with blocker-search-based variable penumbra.
        /// Shadows are sharp near contact points and soften with distance from the occluder,
        /// producing physically plausible penumbrae.
        /// Requires <see cref="LightComponent.LightSourceRadius"/> to control penumbra scale.
        /// </summary>
        ContactHardening = 2,
    }
}
