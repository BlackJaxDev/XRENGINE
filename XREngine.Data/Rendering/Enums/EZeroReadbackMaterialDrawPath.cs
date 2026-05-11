namespace XREngine.Data.Rendering
{
    /// <summary>
    /// Selects the material draw path used by the zero-readback GPU indirect mesh strategy.
    /// </summary>
    public enum EZeroReadbackMaterialDrawPath
    {
        /// <summary>
        /// Iterate every material/tier bucket on the CPU while using GPU-written per-bucket counts.
        /// </summary>
        FullBucketScan = 0,

        /// <summary>
        /// Build and read a compact list of active material/tier buckets before issuing draws.
        /// </summary>
        ActiveBucketList = 1,

        /// <summary>
        /// Draw active buckets with a shared material-table shader instead of per-material programs.
        /// </summary>
        MaterialTable = 2,

        /// <summary>
        /// Draw active buckets with the bindless material-table shader when the renderer supports it.
        /// </summary>
        BindlessMaterialTable = 3,
    }
}
