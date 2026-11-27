namespace XREngine.Data.Components
{
    /// <summary>
    /// Selects how <see cref="AnimStateMachineOscReceiverComponent"/> arbitrates between live OSC
    /// face tracking and sibling procedural eye gaze and blinking.
    /// </summary>
    public enum EFaceTrackingControlMode
    {
        /// <summary>
        /// Activates OSC face tracking while live tracking packets arrive and smoothly returns
        /// to neutral plus procedural eye gaze and blinking when the tracking signal drops.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Keeps OSC face tracking active and suppresses procedural eye gaze and blinking even
        /// when no recent packets have arrived.
        /// </summary>
        ForceFaceTracking = 1,

        /// <summary>
        /// Relaxes face-tracking parameters to neutral, suspends the face-tracking animator,
        /// and lets procedural eye gaze and blinking drive the avatar.
        /// </summary>
        ForceProcedural = 2,
    }
}
