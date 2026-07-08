namespace XREngine.Rendering.Resources;

public sealed partial class RenderPipelineResourceLayoutBuilder
{
    /// <summary>
    /// Builder for constructing a <see cref="FrameBufferSpec"/> resource specification.
    /// </summary>
    public sealed class FrameBufferSpecBuilder : SpecBuilder<FrameBufferSpecBuilder>
    {
        private readonly List<FrameBufferAttachmentDescriptor> _attachments = [];
        private Func<XRFrameBuffer>? _factory;

        internal FrameBufferSpecBuilder(RenderPipelineResourceLayoutBuilder owner, string name)
            : base(owner, name)
        {
        }

        /// <summary>
        /// Adds a color attachment to the frame buffer resource specification.
        /// </summary>
        /// <param name="index">The index of the color attachment.</param>
        /// <param name="resourceName">The name of the resource to attach.</param>
        /// <param name="mipLevel">The mip level of the resource to attach.</param>
        /// <param name="layerIndex">The layer index of the resource to attach.</param>
        /// <returns>The builder instance.</returns>
        public FrameBufferSpecBuilder Color(int index, string resourceName, int mipLevel = 0, int layerIndex = -1)
        {
            EFrameBufferAttachment attachment = (EFrameBufferAttachment)((int)EFrameBufferAttachment.ColorAttachment0 + index);
            return Attachment(resourceName, attachment, mipLevel, layerIndex);
        }

        /// <summary>
        /// Adds a depth-stencil attachment to the frame buffer resource specification.
        /// </summary>
        /// <param name="resourceName">The name of the resource to attach.</param>
        /// <param name="mipLevel">The mip level of the resource to attach.</param>
        /// <param name="layerIndex">The layer index of the resource to attach.</param>
        /// <returns>The builder instance.</returns>
        public FrameBufferSpecBuilder DepthStencil(string resourceName, int mipLevel = 0, int layerIndex = -1)
            => Attachment(resourceName, EFrameBufferAttachment.DepthStencilAttachment, mipLevel, layerIndex);

        /// <summary>
        /// Adds a depth attachment to the frame buffer resource specification.
        /// </summary>
        /// <param name="resourceName">The name of the resource to attach.</param>
        /// <param name="mipLevel">The mip level of the resource to attach.</param>
        /// <param name="layerIndex">The layer index of the resource to attach.</param>
        /// <returns>The builder instance.</returns>
        public FrameBufferSpecBuilder Depth(string resourceName, int mipLevel = 0, int layerIndex = -1)
            => Attachment(resourceName, EFrameBufferAttachment.DepthAttachment, mipLevel, layerIndex);

        /// <summary>
        /// Adds a stencil attachment to the frame buffer resource specification.
        /// </summary>
        /// <param name="resourceName">The name of the resource to attach.</param>
        /// <param name="mipLevel">The mip level of the resource to attach.</param>
        /// <param name="layerIndex">The layer index of the resource to attach.</param>
        /// <returns>The builder instance.</returns>
        public FrameBufferSpecBuilder Stencil(string resourceName, int mipLevel = 0, int layerIndex = -1)
            => Attachment(resourceName, EFrameBufferAttachment.StencilAttachment, mipLevel, layerIndex);

        /// <summary>
        /// Adds an attachment to the frame buffer resource specification.
        /// </summary>
        /// <param name="resourceName">The name of the resource to attach.</param>
        /// <param name="attachment">The type of attachment.</param>
        /// <param name="mipLevel">The mip level of the resource to attach.</param>
        /// <param name="layerIndex">The layer index of the resource to attach.</param>
        /// <returns>The builder instance.</returns>
        public FrameBufferSpecBuilder Attachment(string resourceName, EFrameBufferAttachment attachment, int mipLevel = 0, int layerIndex = -1)
        {
            _attachments.Add(new FrameBufferAttachmentDescriptor(resourceName, attachment, mipLevel, layerIndex));
            DependsOn(resourceName);
            return this;
        }

        /// <summary>
        /// Sets the factory function for creating the frame buffer resource.
        /// </summary>
        /// <param name="factory">The factory function.</param>
        /// <returns>The builder instance.</returns>
        public FrameBufferSpecBuilder Factory(Func<XRFrameBuffer> factory)
        {
            _factory = factory;
            return this;
        }

        /// <summary>
        /// Adds the configured frame buffer resource specification to the builder's collection of resource specifications.
        /// </summary>
        /// <returns>The parent builder instance.</returns>
        public RenderPipelineResourceLayoutBuilder Add()
            => Owner.Add(new FrameBufferSpec(
                Name,
                LifetimeValue,
                SizePolicyValue,
                UsageValue,
                DependenciesValue,
                PredicateValue,
                HistoryPolicyValue,
                DebugLabelValue,
                RequiredValue,
                _attachments.ToArray(),
                _factory));
    }
}
