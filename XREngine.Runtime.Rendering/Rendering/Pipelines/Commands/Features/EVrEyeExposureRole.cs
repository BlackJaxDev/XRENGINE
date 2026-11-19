namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// Auto exposure role of one pipeline in <see cref="VPRC_ExposureUpdate"/>. Only the two
    /// pipelines of a two-pass VR eye pair get an eye role.
    /// </summary>
    internal enum EVrEyeExposureRole
    {
        /// <summary>Not a paired VR eye: desktop, mirror, capture or single-pass stereo.</summary>
        None,

        /// <summary>
        /// Owner (left) eye with one headset exposure: it meters its own image and publishes the
        /// adapted value for the other eye.
        /// </summary>
        HeadsetLeader,

        /// <summary>
        /// Follower (right) eye with one headset exposure: it copies the value that the owner eye
        /// published and does not meter its own image.
        /// </summary>
        HeadsetFollower,

        /// <summary>
        /// Either eye with <see cref="ColorGradingSettings.IndependentEyeExposure"/> on: it meters
        /// its own image and adapts its own exposure state.
        /// </summary>
        IndependentEye,
    }
}
