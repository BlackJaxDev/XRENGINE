namespace XREngine.Data.Rendering
{
    public enum EDefaultRenderPass
    {
        /// <summary>
        /// Not for visible objects, used for pre-rendering operations.
        /// </summary>
        PreRender = -1,
        /// <summary>
        /// Use for any objects that will ALWAYS be rendered behind the scene, even if they are outside of the viewing frustum.
        /// </summary>
        Background,
        /// <summary>
        /// Use for any fully opaque objects that are always lit.
        /// </summary>
        OpaqueDeferred,
        /// <summary>
        /// Renders right after all opaque deferred objects.
        /// More than just decals can be rendered in this pass, it is simply for deferred renderables after all opaque deferred objects have been rendered.
        /// </summary>
        DeferredDecals,
        /// <summary>
        /// Use for any opaque objects that you need special lighting for (or no lighting at all).
        /// </summary>
        OpaqueForward,
        /// <summary>
        /// Use for all objects that use alpha translucency
        /// </summary>
        TransparentForward,
        /// <summary>
        /// Renders on top of everything that has been previously rendered.
        /// </summary>
        OnTopForward,
        /// <summary>
        /// Called after all rendering is done.
        /// </summary>
        PostRender,
    }
}
