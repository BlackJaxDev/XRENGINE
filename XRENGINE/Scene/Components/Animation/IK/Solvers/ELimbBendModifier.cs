namespace XREngine.Scene.Components.Animation
{
    /// <summary>
    /// Automatic bend modes.
    /// </summary>
    [Serializable]
    public enum ELimbBendModifier
    {
        Animation, // Bending relative to the animated rotation of the first bone
        Target, // Bending relative to IKRotation
        Parent, // Bending relative to parentBone
        Arm, // Arm modifier tries to find the most biometrically natural and relaxed arm bend plane
        Goal // Use the bend goal Transform
    }
}
