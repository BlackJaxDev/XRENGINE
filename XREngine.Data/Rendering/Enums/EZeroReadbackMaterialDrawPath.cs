namespace XREngine.Data.Rendering
{
    /// <summary>
    /// Selects the material draw path used by the zero-readback GPU indirect mesh strategy.
    /// </summary>
    public enum EZeroReadbackMaterialDrawPath
    {
        /// <summary>
        /// Diagnostic-only path that iterates every material/tier bucket on
        /// the CPU while using GPU-written per-bucket counts.
        /// </summary>
        FullBucketScanDiagnostic = 0,

        /// <summary>
        /// Diagnostic-only path that reads a compact GPU-produced bucket list
        /// back to the CPU before issuing draws.
        /// </summary>
        ActiveBucketListReadbackDiagnostic = 1,

        /// <summary>
        /// Draw active buckets with a shared material-table shader instead of per-material programs.
        /// </summary>
        MaterialTable = 2,

        /// <summary>
        /// Draw active buckets with the bindless material-table shader when the renderer supports it.
        /// </summary>
        BindlessMaterialTable = 3,

        /// <summary>
        /// Compatibility name for persisted pre-v1 settings. Production
        /// selection and telemetry report <see cref="FullBucketScanDiagnostic"/>.
        /// </summary>
        [Obsolete("Use FullBucketScanDiagnostic; full bucket scans are not a zero-readback production path.")]
        FullBucketScan = FullBucketScanDiagnostic,

        /// <summary>
        /// Compatibility name for persisted pre-v1 settings. Production
        /// selection and telemetry report
        /// <see cref="ActiveBucketListReadbackDiagnostic"/>.
        /// </summary>
        [Obsolete("Use ActiveBucketListReadbackDiagnostic; this path maps GPU-produced work.")]
        ActiveBucketList = ActiveBucketListReadbackDiagnostic,
    }
}
