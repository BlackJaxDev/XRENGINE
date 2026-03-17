using System.Reflection;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_CacheOrCreateRenderBuffer : ViewportRenderCommand
    {
        /// <summary>
        /// The name of the render buffer in the pipeline.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Factory method to create the render buffer when it is not cached.
        /// </summary>
        public Func<XRRenderBuffer>? RenderBufferFactory { get; set; }

        /// <summary>
        /// Called when the render buffer is already cached.
        /// Returns the expected (width, height) so the render buffer can be resized if needed.
        /// </summary>
        public Func<(uint x, uint y)>? SizeVerifier { get; set; }

        private const string InternalSizeMethod = "GetDesiredFBOSizeInternal";
        private const string FullSizeMethod = "GetDesiredFBOSizeFull";

        private RenderResourceSizePolicy? _sizePolicyOverride;
        private RenderResourceLifetime _lifetime = RenderResourceLifetime.Persistent;

        public VPRC_CacheOrCreateRenderBuffer SetOptions(string name, Func<XRRenderBuffer> factory, Func<(uint x, uint y)>? sizeVerifier = null)
        {
            Name = name;
            RenderBufferFactory = factory;
            SizeVerifier = sizeVerifier;
            return this;
        }

        public VPRC_CacheOrCreateRenderBuffer UseSizePolicy(RenderResourceSizePolicy sizePolicy)
        {
            _sizePolicyOverride = sizePolicy;
            return this;
        }

        public VPRC_CacheOrCreateRenderBuffer UseLifetime(RenderResourceLifetime lifetime)
        {
            _lifetime = lifetime;
            return this;
        }

        protected override void Execute()
        {
            if (Name is null)
                return;

            if (ActivePipelineInstance.TryGetRenderBuffer(Name, out XRRenderBuffer? renderBuffer) && renderBuffer is not null)
            {
                if (SizeVerifier is not null)
                {
                    (uint x, uint y) = SizeVerifier();
                    if (renderBuffer.Width != x || renderBuffer.Height != y)
                    {
                        renderBuffer.Width = x;
                        renderBuffer.Height = y;
                        renderBuffer.Allocate();
                    }
                }

                RegisterDescriptor(renderBuffer);
                return;
            }

            if (RenderBufferFactory is null)
                return;

            renderBuffer = RenderBufferFactory();
            renderBuffer.Name = Name;
            RenderBufferResourceDescriptor descriptor = BuildDescriptor(renderBuffer);
            ActivePipelineInstance.SetRenderBuffer(renderBuffer, descriptor);
        }

        private void RegisterDescriptor(XRRenderBuffer renderBuffer)
        {
            RenderBufferResourceDescriptor descriptor = BuildDescriptor(renderBuffer);
            ActivePipelineInstance.Resources.RegisterRenderBufferDescriptor(descriptor);
        }

        private RenderBufferResourceDescriptor BuildDescriptor(XRRenderBuffer renderBuffer)
        {
            RenderBufferResourceDescriptor descriptor = RenderResourceDescriptorFactory.FromRenderBuffer(renderBuffer, _lifetime);

            if ((_sizePolicyOverride ?? InferSizePolicy()) is RenderResourceSizePolicy policy)
                descriptor = descriptor with { SizePolicy = policy };

            return descriptor with { Name = renderBuffer.Name ?? descriptor.Name };
        }

        private RenderResourceSizePolicy? InferSizePolicy()
        {
            if (SizeVerifier is null)
                return _sizePolicyOverride;

            MethodInfo method = SizeVerifier.GetInvocationList()[0].Method;
            return method.Name switch
            {
                InternalSizeMethod => RenderResourceSizePolicy.Internal(),
                FullSizeMethod => RenderResourceSizePolicy.Window(),
                _ => _sizePolicyOverride
            };
        }
    }
}
