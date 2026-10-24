using System.Numerics;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// A scroll receiver that uses the pointer position from the presented publication.
    /// The canvas input calls <see cref="HandlePointerScroll"/> when the receiver has a
    /// current hit record in the displayed packet. Otherwise it calls
    /// <see cref="IUIScrollReceiver.HandleMouseScroll"/>.
    /// </summary>
    public interface IUIPointerScrollReceiver : IUIScrollReceiver
    {
        /// <summary>
        /// Handles a mouse scroll delta at a pointer position in the receiver's presented
        /// local coordinates. <paramref name="geometry"/> is the pointer geometry captured
        /// with the same hit record. Return true to mark the input as handled.
        /// </summary>
        bool HandlePointerScroll(float diff, Vector2 localPoint, in UIPointerGeometry geometry);
    }
}
