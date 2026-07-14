namespace XREngine.Rendering.Resources;

public sealed partial class RenderPipelineResourceLayoutBuilder
{
    /// <summary>
    /// Builds a generation-owned fullscreen-quad execution helper. It is stored
    /// in the framebuffer registry for existing render commands, but owns no
    /// framebuffer attachments and is not a render target.
    /// </summary>
    public sealed class QuadMaterialSpecBuilder : SpecBuilder<QuadMaterialSpecBuilder>
    {
        private Func<XRFrameBuffer>? _factory;

        /// <summary>
        /// Initializes a new instance of the <see cref="QuadMaterialSpecBuilder"/> class.
        /// </summary>
        /// <param name="owner">The resource layout builder that owns this quad material specification.</param>
        /// <param name="name">The name of the quad material specification.</param>
        internal QuadMaterialSpecBuilder(RenderPipelineResourceLayoutBuilder owner, string name)
            : base(owner, name) { }

        /// <summary>
        /// Sets the factory function for creating the fullscreen-quad execution helper.
        /// </summary>
        /// <param name="factory">The factory function.</param>
        /// <returns>The builder instance.</returns>
        public QuadMaterialSpecBuilder Factory(Func<XRFrameBuffer> factory)
        {
            _factory = factory;
            return this;
        }

        /// <summary>
        /// Adds the configured quad material resource specification to the builder's collection of resource specifications.
        /// </summary>
        /// <returns>The parent builder instance.</returns>
        public RenderPipelineResourceLayoutBuilder Add()
            => Owner.Add(new QuadMaterialSpec(
                Name,
                LifetimeValue,
                DependenciesValue,
                PredicateValue,
                DebugLabelValue,
                RequiredValue,
                _factory));
    }
}
