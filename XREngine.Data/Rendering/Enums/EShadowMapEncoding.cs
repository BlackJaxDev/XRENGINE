namespace XREngine.Data.Rendering
{
    /// <summary>
    /// Describes what a sampled shadow map stores. Depth filtering modes are configured separately.
    /// </summary>
    public enum EShadowMapEncoding
    {
        Depth = 0,
        Variance2 = 1,
        ExponentialVariance2 = 2,
        ExponentialVariance4 = 3,
    }
}
