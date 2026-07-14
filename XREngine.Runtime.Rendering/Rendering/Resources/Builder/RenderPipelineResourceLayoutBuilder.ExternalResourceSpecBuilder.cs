namespace XREngine.Rendering.Resources;

public sealed partial class RenderPipelineResourceLayoutBuilder
{
    /// <summary>
    /// Builder for an externally owned resource contract. The layout records
    /// ownership and synchronization, while the window, caller, XR runtime, or
    /// backend binds the concrete object for each frame.
    /// </summary>
    public sealed class ExternalResourceSpecBuilder : SpecBuilder<ExternalResourceSpecBuilder>
    {
        private ExternalRenderResourceKind _kind = ExternalRenderResourceKind.FrameBuffer;
        private ExternalRenderResourceOwnership _ownership = ExternalRenderResourceOwnership.Caller;
        private ExternalRenderResourceSynchronization _synchronization = ExternalRenderResourceSynchronization.CallerProvided;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExternalResourceSpecBuilder"/> class.
        /// </summary>
        /// <param name="owner">The resource layout builder that owns this external resource specification.</param>
        /// <param name="name">The name of the external resource specification.</param>
        internal ExternalResourceSpecBuilder(RenderPipelineResourceLayoutBuilder owner, string name)
            : base(owner, name) { }

        /// <summary>
        /// Configures the external resource contract with the specified kind, ownership, and synchronization.
        /// </summary>
        /// <param name="kind">The kind of the external render resource.</param>
        /// <param name="ownership">The ownership model of the external render resource.</param>
        /// <param name="synchronization">The synchronization model of the external render resource.</param>
        /// <returns>The external resource specification builder to allow for further configuration.</returns>
        public ExternalResourceSpecBuilder Contract(
            ExternalRenderResourceKind kind,
            ExternalRenderResourceOwnership ownership,
            ExternalRenderResourceSynchronization synchronization)
        {
            _kind = kind;
            _ownership = ownership;
            _synchronization = synchronization;
            return this;
        }

        /// <summary>
        /// Adds the configured external resource specification to the resource layout.
        /// </summary>
        /// <returns>The resource layout builder to allow for further configuration.</returns>
        public RenderPipelineResourceLayoutBuilder Add()
            => Owner.Add(new ExternalResourceSpec(
                Name,
                DependenciesValue,
                PredicateValue,
                DebugLabelValue,
                _kind,
                _ownership,
                _synchronization));
    }
}
