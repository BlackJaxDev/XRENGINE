namespace XREngine
{
    /// <summary>
    /// Global override for how transforms publish their render matrix when
    /// <see cref="XREngine.Scene.Transforms.TransformBase.RecalculateMatrices"/> runs.
    /// </summary>
    public enum ERenderMatrixUpdateMode
    {
        /// <summary>
        /// Each call decides for itself via its <c>setRenderMatrixNow</c> argument (engine default).
        /// </summary>
        Default,

        /// <summary>
        /// Force every render-matrix update through the deferred render-matrix queue,
        /// ignoring per-call requests for an immediate synchronous push.
        /// A transform with no world still publishes synchronously (it has no queue to route through).
        /// </summary>
        ForceDeferred,

        /// <summary>
        /// Force every render-matrix update to publish synchronously on the calling tick.
        /// Diagnostic: when matrix-hierarchy recalculation runs with a parallel or asynchronous
        /// loop type, this can invoke <c>RenderMatrixChanged</c> off the render thread / concurrently.
        /// </summary>
        ForceSynchronous,
    }
}
