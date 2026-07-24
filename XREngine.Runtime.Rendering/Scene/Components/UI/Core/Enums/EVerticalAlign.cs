namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Only applies if the parent is a UIBoundableComponent.
    /// </summary>
    public enum EVerticalAlign
    {
        /// <summary>
        /// Does not align the component vertically.
        /// The position must be set manually.
        /// </summary>
        None,
        /// <summary>
        /// Aligns the origin to the top of the parent.
        /// </summary>
        Top,
        /// <summary>
        /// Aligns the origin to the center of the parent.
        /// </summary>
        Center,
        /// <summary>
        /// Aligns the origin to the bottom of the parent.
        /// </summary>
        Bottom,
        /// <summary>
        /// Stretches the component from the top to the bottom of the parent.
        /// </summary>
        Stretch,
    }
}
