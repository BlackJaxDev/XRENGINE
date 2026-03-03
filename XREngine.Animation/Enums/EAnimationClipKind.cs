namespace XREngine.Animation
{
    /// <summary>
    /// Classifies the animation data format within a clip.
    /// Set during import; enables auto-detection of humanoid muscle data
    /// vs standard per-bone transform curves.
    /// </summary>
    public enum EAnimationClipKind
    {
        /// <summary>
        /// Unknown or unclassified clip format.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// Standard per-bone local transform animations (position, rotation, scale).
        /// </summary>
        GenericTransform,

        /// <summary>
        /// Unity humanoid muscle-space animation (float curves with Mecanim channel names).
        /// </summary>
        UnityHumanoidMuscle,
    }
}
