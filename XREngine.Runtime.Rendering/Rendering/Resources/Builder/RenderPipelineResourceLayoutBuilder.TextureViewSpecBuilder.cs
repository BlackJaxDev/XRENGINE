namespace XREngine.Rendering.Resources;

public sealed partial class RenderPipelineResourceLayoutBuilder
{
    /// <summary>
    /// Builds a <see cref="RenderPipelineResourceLayout"/> from the added resource specifications, validating them against the builder's associated resource profile.
    /// </summary>
    public sealed class TextureViewSpecBuilder : SpecBuilder<TextureViewSpecBuilder>
    {
        private readonly string _sourceTextureName;
        private uint _baseMipLevel;
        private uint _mipLevelCount = 1u;
        private uint _baseLayer;
        private uint _layerCount = 1u;
        private ESizedInternalFormat? _sizedInternalFormat;
        private EDepthStencilFmt _depthStencilAspect = EDepthStencilFmt.None;
        private bool _arrayTarget;
        private bool _multisample;
        private Func<XRTexture>? _factory;

        internal TextureViewSpecBuilder(RenderPipelineResourceLayoutBuilder owner, string name, string sourceTextureName)
            : base(owner, name)
        {
            _sourceTextureName = sourceTextureName;
            DependsOn(sourceTextureName);
        }

        /// <summary>
        /// Sets the mip level range for the texture view resource specification.
        /// </summary>
        /// <param name="baseMipLevel">The base mip level.</param>
        /// <param name="mipLevelCount">The number of mip levels.</param>
        /// <returns>The builder instance.</returns>
        public TextureViewSpecBuilder MipRange(uint baseMipLevel, uint mipLevelCount)
        {
            _baseMipLevel = baseMipLevel;
            _mipLevelCount = Math.Max(1u, mipLevelCount);
            return this;
        }

        /// <summary>
        /// Sets the layer range for the texture view resource specification.
        /// </summary>
        /// <param name="baseLayer">The base layer.</param>
        /// <param name="layerCount">The number of layers.</param>
        /// <returns>The builder instance.</returns>
        public TextureViewSpecBuilder LayerRange(uint baseLayer, uint layerCount)
        {
            _baseLayer = baseLayer;
            _layerCount = Math.Max(1u, layerCount);
            return this;
        }

        /// <summary>
        /// Sets the sized internal format for the texture view resource specification.
        /// </summary>
        /// <param name="sizedInternalFormat">The sized internal format.</param>
        /// <returns>The builder instance.</returns>
        public TextureViewSpecBuilder SizedFormat(ESizedInternalFormat sizedInternalFormat)
        {
            _sizedInternalFormat = sizedInternalFormat;
            return this;
        }

        /// <summary>
        /// Sets the depth stencil aspect for the texture view resource specification.
        /// </summary>
        /// <param name="aspect">The depth stencil aspect.</param>
        /// <returns>The builder instance.</returns>
        public TextureViewSpecBuilder DepthStencilAspect(EDepthStencilFmt aspect)
        {
            _depthStencilAspect = aspect;
            return this;
        }

        /// <summary>
        /// Sets whether the texture view resource specification is an array target and/or multisample.
        /// </summary>
        /// <param name="array">Whether the texture view is an array target.</param>
        /// <param name="multisample">Whether the texture view is multisample.</param>
        /// <returns>The builder instance.</returns>
        public TextureViewSpecBuilder Target(bool array, bool multisample)
        {
            _arrayTarget = array;
            _multisample = multisample;
            return this;
        }

        /// <summary>
        /// Sets the factory function for creating the texture view resource.
        /// </summary>
        /// <param name="factory">The factory function.</param>
        /// <returns>The builder instance.</returns>
        public TextureViewSpecBuilder Factory(Func<XRTexture> factory)
        {
            _factory = factory;
            return this;
        }

        /// <summary>
        /// Builds the texture view resource specification and adds it to the parent <see cref="RenderPipelineResourceLayoutBuilder"/>.
        /// </summary>
        /// <returns>The parent builder instance.</returns>
        public RenderPipelineResourceLayoutBuilder Add()
            => Owner.Add(new TextureViewSpec(
                Name,
                LifetimeValue,
                SizePolicyValue,
                UsageValue,
                DependenciesValue,
                PredicateValue,
                HistoryPolicyValue,
                DebugLabelValue,
                RequiredValue,
                _sourceTextureName,
                _baseMipLevel,
                _mipLevelCount,
                _baseLayer,
                _layerCount,
                _sizedInternalFormat,
                _depthStencilAspect,
                _arrayTarget,
                _multisample,
                _factory));
    }
}
