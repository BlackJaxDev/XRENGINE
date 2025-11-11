namespace XREngine
{
    /// <summary>
    /// Controls which global illumination strategy the renderer should use.
    /// </summary>
    public enum EGlobalIlluminationMode
    {
        None = 0,
        LightProbesAndIbl = 1,
        Restir = 2,
        VoxelConeTracing = 3,
    }
}
