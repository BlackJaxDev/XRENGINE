namespace XREngine.Rendering.Info
{
    /// <summary>
    /// The result of one command collection request on a <see cref="RenderInfo"/>.
    /// </summary>
    /// <remarks>
    /// Only <see cref="CallbackRejected"/> can change for a different camera. The other rejections apply to every
    /// camera, shadow cameras included.
    /// </remarks>
    internal enum ERenderInfoCollectResult
    {
        /// <summary>
        /// The render info added its enabled commands. A command that is disabled, or a primary command that the
        /// material excludes from this pass, was not added.
        /// </summary>
        Collected,

        /// <summary>
        /// Disposal of the render info was requested. No camera collects it, and the scene removes it at the next
        /// collect pass.
        /// </summary>
        Disposed,

        /// <summary>
        /// The owning <see cref="XREngine.Components.Scene.Mesh.RenderableMesh"/> requested retirement, so its pre-collection
        /// callback rejects every camera until the scene removes the render info.
        /// </summary>
        Retired,

        /// <summary>
        /// A pre-collection callback that its owner declared camera independent rejected the collection. The callback
        /// gives the same result for every camera and every pass collection.
        /// </summary>
        RejectedForEveryCamera,

        /// <summary>
        /// The pre-collection callback rejected the collection for this camera. The result can be different for another
        /// camera, for example a shadow camera.
        /// </summary>
        CallbackRejected,
    }
}
