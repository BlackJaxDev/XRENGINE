namespace XREngine.Rendering
{
    /// <summary>
    /// Auto exposure adaptation state of one <see cref="ColorGradingSettings"/> object in one
    /// render pipeline instance. The settings object holds the authored exposure controls, which the
    /// two VR eyes share. This object holds the values that auto exposure changes while it renders:
    /// the GPU-ready flag of the current frame, the update timing, the last metered luminance and the
    /// adapted CPU exposure. Each eye pipeline instance gets its own object, so the two eyes cannot
    /// overwrite each other's adaptation (no last-eye-wins).
    /// </summary>
    internal sealed class AutoExposureRuntimeState
    {
        /// <summary>
        /// True when the GPU exposure texture of this pipeline instance was written this frame, so
        /// the tonemap may sample it.
        /// </summary>
        public bool GpuReadyThisFrame;

        /// <summary>
        /// Engine time of the last successful exposure update, or <see cref="float.MinValue"/>
        /// before the first one. The CPU path uses it as its update gate, and the GPU path uses it
        /// to start without a transition on the first update.
        /// </summary>
        public float LastUpdateTime = float.MinValue;

        /// <summary>Last metered luminance of the CPU path.</summary>
        public float LastLumDot;

        /// <summary>
        /// Exposure that the CPU path adapted for this instance, or NaN when it has none. Only paired
        /// VR eye instances use it: other instances keep the adapted CPU exposure in
        /// <see cref="ColorGradingSettings.Exposure"/>, as before.
        /// </summary>
        public float AdaptedExposure = float.NaN;

        /// <summary>True when <see cref="AdaptedExposure"/> holds a CPU-adapted value.</summary>
        public bool HasAdaptedExposure => float.IsFinite(AdaptedExposure);
    }
}
