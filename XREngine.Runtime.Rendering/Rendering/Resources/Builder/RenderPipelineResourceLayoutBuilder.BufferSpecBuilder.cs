namespace XREngine.Rendering.Resources;

public sealed partial class RenderPipelineResourceLayoutBuilder
{
    /// <summary>
    /// Builder for defining a buffer resource specification within a <see cref="RenderPipelineResourceLayoutBuilder"/>.
    /// </summary>
    public sealed class BufferSpecBuilder : SpecBuilder<BufferSpecBuilder>
    {
        private ulong _sizeInBytes = 1UL;
        private EBufferTarget _target = EBufferTarget.ArrayBuffer;
        private EBufferUsage _usage = EBufferUsage.DynamicDraw;
        private uint _elementStride;
        private uint _elementCount;
        private EBufferAccessPattern _accessPattern = EBufferAccessPattern.ReadWrite;
        private Func<XRDataBuffer>? _factory;

        /// <summary>
        /// Initializes a new instance of the <see cref="BufferSpecBuilder"/> class.
        /// </summary>
        /// <param name="owner">The resource layout builder that owns this buffer specification.</param>
        /// <param name="name">The name of the buffer specification.</param>
        internal BufferSpecBuilder(RenderPipelineResourceLayoutBuilder owner, string name)
            : base(owner, name) { }

        /// <summary>
        /// Sets the buffer format for the buffer resource specification.
        /// </summary>
        /// <param name="sizeInBytes">The size of the buffer in bytes.</param>
        /// <param name="target">The buffer target.</param>
        /// <param name="usage">The buffer usage.</param>
        /// <returns>The builder instance.</returns>
        public BufferSpecBuilder BufferFormat(ulong sizeInBytes, EBufferTarget target, EBufferUsage usage)
        {
            _sizeInBytes = Math.Max(1UL, sizeInBytes);
            _target = target;
            _usage = usage;
            return this;
        }

        /// <summary>
        /// Sets the element stride and count for the buffer resource specification.
        /// </summary>
        /// <param name="elementStride">The stride of each element in bytes.</param>
        /// <param name="elementCount">The number of elements.</param>
        /// <returns>The builder instance.</returns>
        public BufferSpecBuilder Elements(uint elementStride, uint elementCount)
        {
            _elementStride = elementStride;
            _elementCount = elementCount;
            return this;
        }

        /// <summary>
        /// Sets the access pattern for the buffer resource specification.
        /// </summary>
        /// <param name="accessPattern">The access pattern.</param>
        /// <returns>The builder instance.</returns>
        public BufferSpecBuilder Access(EBufferAccessPattern accessPattern)
        {
            _accessPattern = accessPattern;
            return this;
        }

        /// <summary>
        /// Sets the factory function for creating the buffer resource.
        /// </summary>
        /// <param name="factory">The factory function.</param>
        /// <returns>The builder instance.</returns>
        public BufferSpecBuilder Factory(Func<XRDataBuffer> factory)
        {
            _factory = factory;
            return this;
        }

        /// <summary>
        /// Adds the configured buffer resource specification to the builder's collection of resource specifications.
        /// </summary>
        /// <returns>The parent builder instance.</returns>
        public RenderPipelineResourceLayoutBuilder Add()
            => Owner.Add(new BufferSpec(
                Name,
                LifetimeValue,
                SizePolicyValue,
                UsageValue,
                DependenciesValue,
                PredicateValue,
                HistoryPolicyValue,
                DebugLabelValue,
                RequiredValue,
                _sizeInBytes,
                _target,
                _usage,
                _elementStride,
                _elementCount,
                _accessPattern,
                _factory));
    }
}
