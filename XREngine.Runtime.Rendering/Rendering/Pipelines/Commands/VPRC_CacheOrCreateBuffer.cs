using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_CacheOrCreateBuffer : ViewportRenderCommand
    {
        /// <summary>
        /// The name of the buffer in the pipeline.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Factory method to create the buffer when it is not cached.
        /// </summary>
        public Func<XRDataBuffer>? BufferFactory { get; set; }

        /// <summary>
        /// Called when the buffer is already cached.
        /// Return true if the buffer needs to be recreated (e.g. size or target changed).
        /// </summary>
        public Func<XRDataBuffer, bool>? NeedsRecreate { get; set; }

        private RenderResourceLifetime _lifetime = RenderResourceLifetime.Persistent;
        private EBufferAccessPattern _accessPattern = EBufferAccessPattern.ReadWrite;

        public VPRC_CacheOrCreateBuffer SetOptions(string name, Func<XRDataBuffer> factory, Func<XRDataBuffer, bool>? needsRecreate = null)
        {
            Name = name;
            BufferFactory = factory;
            NeedsRecreate = needsRecreate;
            return this;
        }

        public VPRC_CacheOrCreateBuffer UseLifetime(RenderResourceLifetime lifetime)
        {
            _lifetime = lifetime;
            return this;
        }

        public VPRC_CacheOrCreateBuffer UseAccessPattern(EBufferAccessPattern accessPattern)
        {
            _accessPattern = accessPattern;
            return this;
        }

        protected override void Execute()
        {
            if (Name is null)
                return;

            if (ActivePipelineInstance.TryGetBuffer(Name, out XRDataBuffer? buffer) && buffer is not null)
            {
                bool shouldRecreate = NeedsRecreate?.Invoke(buffer) ?? false;
                if (!shouldRecreate)
                {
                    RegisterDescriptor(buffer);
                    return;
                }
            }

            if (BufferFactory is null)
                return;

            buffer = BufferFactory();
            buffer.AttributeName = Name;
            BufferResourceDescriptor descriptor = BuildDescriptor(buffer);
            ActivePipelineInstance.SetBuffer(buffer, descriptor);
        }

        private void RegisterDescriptor(XRDataBuffer buffer)
        {
            BufferResourceDescriptor descriptor = BuildDescriptor(buffer);
            ActivePipelineInstance.Resources.RegisterBufferDescriptor(descriptor);
        }

        private BufferResourceDescriptor BuildDescriptor(XRDataBuffer buffer)
        {
            BufferResourceDescriptor descriptor = RenderResourceDescriptorFactory.FromBuffer(buffer, _lifetime);
            return descriptor with
            {
                Name = buffer.AttributeName ?? descriptor.Name,
                SupportsAliasing = _lifetime == RenderResourceLifetime.Transient,
                AccessPattern = _accessPattern
            };
        }
    }
}
