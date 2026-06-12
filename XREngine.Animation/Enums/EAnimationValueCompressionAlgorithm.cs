namespace XREngine.Animation
{
    /// <summary>
    /// Selects how baked property animation value arrays should be encoded.
    /// </summary>
    public enum EAnimationValueCompressionAlgorithm
    {
        /// <summary>
        /// Store baked samples as raw values.
        /// </summary>
        None = 0,

        /// <summary>
        /// Store a single value when every baked sample is identical.
        /// </summary>
        Constant = 1,

        /// <summary>
        /// Store repeated baked values as value/count runs.
        /// </summary>
        RunLength = 2,

        /// <summary>
        /// Store each unmanaged baked value as an XOR delta from the previous sample.
        /// </summary>
        Delta = 3,

        /// <summary>
        /// Store unmanaged XOR deltas and compress repeated delta payloads as runs.
        /// </summary>
        DeltaRunLength = 4,
    }
}
