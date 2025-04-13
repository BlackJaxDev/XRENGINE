namespace XREngine.Scene.Components.Animation
{
    /// <summary>
    /// Automatic bend modes.
    /// </summary>
    [Serializable]
    public enum ELimbBendModifier
    {
        /// <summary>
        /// Bending relative to the animated rotation of the first bone.
        /// </summary>
        Animation,
        /// <summary>
        /// Bending relative to the IK target transform's rotation / IKRotation.
        /// </summary>
        Target,
        /// <summary>
        /// Bending relative to the parent bone's rotation.
        /// </summary>
        Parent,
        /// <summary>
        /// Determines the bend direction based on the arm's natural bend direction.
        /// </summary>
        Arm,
        /// <summary>
        /// Uses bend goal transform to determine the bend direction.
        /// For example, a knee or elbow target transform.
        /// </summary>
        Goal
    }
}
