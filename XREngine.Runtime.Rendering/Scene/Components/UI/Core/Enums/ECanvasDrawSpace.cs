namespace XREngine.Rendering.UI
{
    public enum ECanvasDrawSpace
    {
        /// <summary>
        /// Canvas is drawn on top of the viewport.
        /// </summary>
        Screen,
        /// <summary>
        /// Canvas is drawn in front of the camera.
        /// </summary>
        Camera,
        /// <summary>
        /// Canvas is drawn in the world like any other actor.
        /// Camera is irrelevant.
        /// </summary>
        World,
    }
}
