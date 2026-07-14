namespace XREngine
{
    /// <summary>
    /// Controls which global illumination strategy the renderer should use.
    /// </summary>
    public enum EGlobalIlluminationMode
    {
        /// <summary>
        /// No global illumination is applied.
        /// Scene uses a global ambient term only.
        /// </summary>
        None = 0,
        /// <summary>
        /// Uses light probes and image-based lighting for global illumination.
        /// </summary>
        LightProbesAndIbl = 1,
        /// <summary>
        /// Uses path tracing + ReSTIR (if available) for global illumination.
        /// </summary>
        PathTracing = 2,
        /// <summary>
        /// Uses voxel cone tracing for global illumination.
        /// </summary>
        VoxelConeTracing = 3,
        /// <summary>
        /// Uses baked voxel lighting information in a 3D texture for global illumination.
        /// </summary>
        LightVolumes = 4,
        /// <summary>
        /// Uses radiance cascades for global illumination.
        /// </summary>
        RadianceCascades = 5,
        /// <summary>
        /// Uses surfel-based global illumination.
        /// </summary>
        SurfelGI = 6,
        /// <summary>
        /// Uses dynamic diffuse global illumination.
        /// </summary>
        DDGI = 8,
    }
}
