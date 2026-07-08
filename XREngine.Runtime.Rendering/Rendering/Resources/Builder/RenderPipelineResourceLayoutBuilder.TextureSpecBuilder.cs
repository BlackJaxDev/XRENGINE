namespace XREngine.Rendering.Resources;

public sealed partial class RenderPipelineResourceLayoutBuilder
{
    /// <summary>
    /// Builder class for configuring a <see cref="TextureSpec"/> with specific properties such as format, size, and usage.
    /// </summary>
    public sealed class TextureSpecBuilder : SpecBuilder<TextureSpecBuilder>
    {
        private EPixelInternalFormat? _internalFormat;
        private EPixelFormat? _pixelFormat;
        private EPixelType? _pixelType;
        private ESizedInternalFormat? _sizedInternalFormat;
        private uint _samples = 1u;
        private uint _layers = 1u;
        private RenderResourceMipPolicy _mipPolicy = new();
        private bool _stereoCompatible;
        private bool _requiresStorageUsage;
        private Func<XRTexture>? _factory;

        internal TextureSpecBuilder(RenderPipelineResourceLayoutBuilder owner, string name)
            : base(owner, name)
        {
        }

        /// <summary>
        /// Sets the pixel format of the texture resource specification.
        /// </summary>
        /// <param name="internalFormat">The internal format of the texture.</param>
        /// <param name="pixelFormat">The pixel format of the texture.</param>
        /// <param name="pixelType">The pixel type of the texture.</param>
        /// <returns>The builder instance.</returns>
        public TextureSpecBuilder Format(EPixelInternalFormat internalFormat, EPixelFormat pixelFormat, EPixelType pixelType)
        {
            _internalFormat = internalFormat;
            _pixelFormat = pixelFormat;
            _pixelType = pixelType;
            return this;
        }

        /// <summary>
        /// Sets the sized internal format of the texture resource specification.
        /// </summary>
        /// <param name="sizedInternalFormat">The sized internal format of the texture.</param>
        /// <returns>The builder instance.</returns>
        public TextureSpecBuilder SizedFormat(ESizedInternalFormat sizedInternalFormat)
        {
            _sizedInternalFormat = sizedInternalFormat;
            return this;
        }

        /// <summary>
        /// Sets the number of samples for the texture resource specification.
        /// </summary>
        /// <param name="samples">The number of samples.</param>
        /// <returns>The builder instance.</returns>
        public TextureSpecBuilder Samples(uint samples)
        {
            _samples = Math.Max(1u, samples);
            return this;
        }

        /// <summary>
        /// Sets the number of layers for the texture resource specification.
        /// </summary>
        /// <param name="layers">The number of layers.</param>
        /// <returns>The builder instance.</returns>
        public TextureSpecBuilder Layers(uint layers)
        {
            _layers = Math.Max(1u, layers);
            return this;
        }

        /// <summary>
        /// Sets the mip policy for the texture resource specification.
        /// </summary>
        /// <param name="mipPolicy">The mip policy.</param>
        /// <returns>The builder instance.</returns>
        public TextureSpecBuilder Mips(RenderResourceMipPolicy mipPolicy)
        {
            _mipPolicy = mipPolicy;
            return this;
        }

        /// <summary>
        /// Sets whether the texture resource specification is stereo compatible.
        /// </summary>
        /// <param name="stereoCompatible">Whether the texture is stereo compatible.</param>
        /// <returns>The builder instance.</returns>
        public TextureSpecBuilder StereoCompatible(bool stereoCompatible = true)
        {
            _stereoCompatible = stereoCompatible;
            return this;
        }

        /// <summary>
        /// Sets whether the texture resource specification requires storage usage.
        /// </summary>
        /// <param name="requiresStorageUsage">Whether the texture requires storage usage.</param>
        /// <returns>The builder instance.</returns>
        public TextureSpecBuilder RequiresStorageUsage(bool requiresStorageUsage = true)
        {
            _requiresStorageUsage = requiresStorageUsage;
            return this;
        }

        /// <summary>
        /// Sets the factory function for creating the texture resource.
        /// </summary>
        /// <param name="factory">The factory function.</param>
        /// <returns>The builder instance.</returns>
        public TextureSpecBuilder Factory(Func<XRTexture> factory)
        {
            _factory = factory;
            return this;
        }

        /// <summary>
        /// Adds the configured texture resource specification to the builder's collection of resource specifications.
        /// </summary>
        /// <returns>The builder instance.</returns>
        public RenderPipelineResourceLayoutBuilder Add()
            => Owner.Add(new TextureSpec(
                Name,
                LifetimeValue,
                SizePolicyValue,
                UsageValue,
                DependenciesValue,
                PredicateValue,
                HistoryPolicyValue,
                DebugLabelValue,
                RequiredValue,
                _internalFormat,
                _pixelFormat,
                _pixelType,
                _sizedInternalFormat,
                _samples,
                _layers,
                _mipPolicy,
                _stereoCompatible,
                _requiresStorageUsage,
                _factory));
    }
}
