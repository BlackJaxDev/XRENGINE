namespace XREngine.Data.Rendering
{
    /// <summary>
    /// GPU storage formats supported by the current shadow-map render and sampling paths.
    /// </summary>
    public enum EShadowMapStorageFormat
    {
        R16Float = 0,
        R16UNorm = 1,
        R32Float = 2,
        R8UNorm = 3,
        RG16Float = 20,
        RG32Float = 21,
        RGBA16Float = 30,
        RGBA32Float = 31,
        Depth24 = 10,
        Depth16 = 11,
        Depth32Float = 12,
    }
}
