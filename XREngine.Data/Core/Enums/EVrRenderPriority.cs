namespace XREngine
{
    /// <summary>
    /// Selects which output gets render-thread time first while VR is active.
    /// </summary>
    public enum EVrRenderPriority
    {
        /// <summary>
        /// The headset comes first. A desktop frame runs only when its measured cost
        /// fits before the next XR frame, with a low minimum rate. The window keeps its
        /// last image between desktop frames.
        /// </summary>
        Vr,

        /// <summary>
        /// The desktop window comes first. Every due desktop frame runs, also when it
        /// makes the next XR frame late. The XR runtime reprojects late eye frames.
        /// </summary>
        Desktop,
    }
}
