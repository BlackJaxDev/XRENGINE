using System.Reflection;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_CacheOrCreateFBO : ViewportRenderCommand
    {
        /// <summary>
        /// The name of the FBO in the pipeline.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Factory method to create the FBO when it is not cached.
        /// </summary>
        public Func<XRFrameBuffer>? FrameBufferFactory { get; set; }

        /// <summary>
        /// This action is called when the FBO is already cached.
        /// Return the size the FBO should be with this action and the FBO will be resized if necessary.
        /// </summary>
        public Func<(uint x, uint y)>? SizeVerifier { get; set; }

        private const string InternalSizeMethod = "GetDesiredFBOSizeInternal";
        private const string FullSizeMethod = "GetDesiredFBOSizeFull";

        private RenderResourceSizePolicy? _sizePolicyOverride;
        private RenderResourceLifetime _lifetime = RenderResourceLifetime.Persistent;

        public VPRC_CacheOrCreateFBO SetOptions(string name, Func<XRFrameBuffer> factory, Func<(uint x, uint y)>? sizeVerifier)
        {
            Name = name;
            FrameBufferFactory = factory;
            SizeVerifier = sizeVerifier;
            return this;
        }

        public VPRC_CacheOrCreateFBO UseSizePolicy(RenderResourceSizePolicy sizePolicy)
        {
            _sizePolicyOverride = sizePolicy;
            return this;
        }

        public VPRC_CacheOrCreateFBO UseLifetime(RenderResourceLifetime lifetime)
        {
            _lifetime = lifetime;
            return this;
        }

        protected override void Execute()
        {
            if (Name is null)
                return;

            if (ActivePipelineInstance.TryGetFBO(Name, out var fbo) && fbo is not null)
            {
                if (SizeVerifier is not null)
                {
                    (uint x, uint y) = SizeVerifier();
                    if (fbo.Width != x || fbo.Height != y)
                        fbo.Resize(x, y);
                }

                RegisterDescriptor(fbo);
                return;
            }

            if (FrameBufferFactory is null)
                return;

            fbo = FrameBufferFactory();
            fbo.Name = Name;
            FrameBufferResourceDescriptor descriptor = BuildDescriptor(fbo);
            ActivePipelineInstance.SetFBO(fbo, descriptor);
        }

        private void RegisterDescriptor(XRFrameBuffer frameBuffer)
        {
            FrameBufferResourceDescriptor descriptor = BuildDescriptor(frameBuffer);
            ActivePipelineInstance.Resources.RegisterFrameBufferDescriptor(descriptor);
        }

        private FrameBufferResourceDescriptor BuildDescriptor(XRFrameBuffer frameBuffer)
        {
            FrameBufferResourceDescriptor descriptor = FrameBufferResourceDescriptor.FromFrameBuffer(frameBuffer, _lifetime);

            if ((_sizePolicyOverride ?? InferSizePolicy()) is RenderResourceSizePolicy policy)
                descriptor = descriptor with { SizePolicy = policy };

            return descriptor with { Name = frameBuffer.Name ?? descriptor.Name };
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
