namespace XREngine
{
    public enum ERenderPass2D
    {
        /// <summary>
        /// Use for background objects that don't write to depth.
        /// </summary>
        Background,
        /// <summary>
        /// Use for any fully opaque objects.
        /// </summary>
        Opaque,
        /// <summary>
        /// Use for all objects that use alpha translucency! Material.HasTransparency will help you determine this.
        /// </summary>
        Transparent,
        /// <summary>
        /// Renders on top of everything that has been previously rendered.
        /// </summary>
        OnTop,
    }
}
