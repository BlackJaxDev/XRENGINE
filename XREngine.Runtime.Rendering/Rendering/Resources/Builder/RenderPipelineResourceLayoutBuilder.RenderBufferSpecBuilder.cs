namespace XREngine.Rendering.Resources;

public sealed partial class RenderPipelineResourceLayoutBuilder
{
    /// <summary>
    /// Builder for constructing a <see cref="RenderPipelineResourceSpec"/> for a render buffer resource.
    /// </summary>
    public sealed class RenderBufferSpecBuilder : SpecBuilder<RenderBufferSpecBuilder>
    {
        private ERenderBufferStorage _storageFormat = ERenderBufferStorage.Rgba8;
        private uint _samples = 1u;
        private EFrameBufferAttachment? _defaultAttachment;
        private Func<XRRenderBuffer>? _factory;

        /// <summary>
        /// Initializes a new instance of the <see cref="RenderBufferSpecBuilder"/> class.
        /// </summary>
        /// <param name="owner">The resource layout builder that owns this render buffer specification.</param>
        /// <param name="name">The name of the render buffer specification.</param>
        internal RenderBufferSpecBuilder(RenderPipelineResourceLayoutBuilder owner, string name)
            : base(owner, name) { }

        /// <summary>
        /// Sets the storage format for the render buffer resource specification.
        /// </summary>
        /// <param name="storageFormat">The storage format.</param>
        /// <returns>The builder instance.</returns>
        public RenderBufferSpecBuilder Storage(ERenderBufferStorage storageFormat)
        {
            _storageFormat = storageFormat;
            return this;
        }

        /// <summary>
        /// Sets the number of samples for the render buffer resource specification.
        /// </summary>
        /// <param name="samples">The number of samples.</param>
        /// <returns>The builder instance.</returns>
        public RenderBufferSpecBuilder Samples(uint samples)
        {
            _samples = Math.Max(1u, samples);
            return this;
        }

        /// <summary>
        /// Sets the default attachment for the render buffer resource specification.
        /// </summary>
        /// <param name="attachment">The default attachment.</param>
        /// <returns>The builder instance.</returns>
        public RenderBufferSpecBuilder DefaultAttachment(EFrameBufferAttachment attachment)
        {
            _defaultAttachment = attachment;
            return this;
        }

        /// <summary>
        /// Sets the factory function for creating the render buffer resource.
        /// </summary>
        /// <param name="factory">The factory function.</param>
        /// <returns>The builder instance.</returns>
        public RenderBufferSpecBuilder Factory(Func<XRRenderBuffer> factory)
        {
            _factory = factory;
            return this;
        }

        /// <summary>
        /// Adds the configured render buffer resource specification to the builder's collection of resource specifications.
        /// </summary>
        /// <returns>The parent builder instance.</returns>
        public RenderPipelineResourceLayoutBuilder Add()
            => Owner.Add(new RenderBufferSpec(
                Name,
                LifetimeValue,
                SizePolicyValue,
                UsageValue,
                DependenciesValue,
                PredicateValue,
                HistoryPolicyValue,
                DebugLabelValue,
                RequiredValue,
                _storageFormat,
                _samples,
                _defaultAttachment,
                _factory));
    }
}
