namespace XREngine.Rendering
{
    public enum EMeshBillboardMode
    {
        /// <summary>
        /// No billboarding.
        /// </summary>
        None,
        /// <summary>
        /// Billboards facing towards the camera's position.
        /// </summary>
        Perspective,
        /// <summary>
        /// Billboards facing towards the camera's forward vector.
        /// </summary>
        Orthographic,
    }
}
