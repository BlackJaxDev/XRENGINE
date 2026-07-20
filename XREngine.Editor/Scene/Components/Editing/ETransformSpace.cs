namespace XREngine.Scene.Components.Editing
{
    public enum ETransformSpace
    {
        /// <summary>
        /// Relative to the world.
        /// </summary>
        World,
        /// <summary>
        /// Relative to the parent transform (or world if no parent).
        /// </summary>
        Parent,
        /// <summary>
        /// Relative to the current transform.
        /// </summary>
        Local,
        /// <summary>
        /// Relative to the camera transform.
        /// </summary>
        Screen,
    }
}
