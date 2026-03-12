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
        /// Use for masked cutout content that should depth test and depth write like opaque geometry
        /// while remaining separate from deferred and alpha-blended passes.
        /// </summary>
        MaskedForward,
        /// <summary>
        /// Use for all objects that use alpha translucency
        /// </summary>
        TransparentForward,
        /// <summary>
        /// Use for weighted blended order-independent transparency accumulation.
        /// This pass writes into dedicated accumulation and revealage targets and
        /// should not rely on painter's-order blending.
        /// </summary>
        WeightedBlendedOitForward,
        /// <summary>
        /// Use for exact per-pixel linked-list transparency insertion.
        /// This pass records fragments into a linked-list storage buffer and is
        /// resolved in a later fullscreen pass.
        /// </summary>
        PerPixelLinkedListForward,
        /// <summary>
        /// Use for exact depth-peeling transparency layer generation.
        /// This pass peels one surviving transparent layer per iteration and is
        /// resolved in a later fullscreen pass.
        /// </summary>
        DepthPeelingForward,
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
