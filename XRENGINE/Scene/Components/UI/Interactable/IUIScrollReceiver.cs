namespace XREngine.Rendering.UI
{
    /// <summary>
    /// Implemented by components that can receive routed mouse-wheel scroll input
    /// from <see cref="XREngine.Components.UICanvasInputComponent"/>.
    /// </summary>
    public interface IUIScrollReceiver
    {
        /// <summary>
        /// Handles a mouse scroll delta.
        /// Return true to mark the input as handled.
        /// </summary>
        bool HandleMouseScroll(float diff);
    }
}