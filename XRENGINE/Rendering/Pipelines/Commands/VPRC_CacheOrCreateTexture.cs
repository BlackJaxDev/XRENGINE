using System.Reflection;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_CacheOrCreateTexture : ViewportRenderCommand
    {
        /// <summary>
        /// The name of the texture in the pipeline.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Factory method to create the texture when it is not cached.
        /// </summary>
        public Func<XRTexture>? TextureFactory { get; set; }

        /// <summary>
        /// This action is called when the texture is already cached.
        /// Cast the texture to the correct type and verify its size with this action.
        /// If true is returned, the texture will be recreated.
        /// </summary>
        public Func<XRTexture, bool>? NeedsRecreate { get; set; } = null;

        public Action<XRTexture>? Resize { get; set; } = null;

        private const string InternalRecreateMethod = "NeedsRecreateTextureInternalSize";
        private const string FullRecreateMethod = "NeedsRecreateTextureFullSize";

        private RenderResourceSizePolicy? _sizePolicyOverride;
        private RenderResourceLifetime _lifetime = RenderResourceLifetime.Persistent;

        public VPRC_CacheOrCreateTexture SetOptions(string name, Func<XRTexture> factory, Func<XRTexture, bool>? needsRecreate, Action<XRTexture>? resize)
        {
            Name = name;
            TextureFactory = factory;
            NeedsRecreate = needsRecreate;
            Resize = resize;
            return this;
        }

        public VPRC_CacheOrCreateTexture UseSizePolicy(RenderResourceSizePolicy sizePolicy)
        {
            _sizePolicyOverride = sizePolicy;
            return this;
        }

        public VPRC_CacheOrCreateTexture UseLifetime(RenderResourceLifetime lifetime)
        {
            _lifetime = lifetime;
            return this;
        }

        protected override void Execute()
        {
            if (Name is null)
                return;

            XRTexture? texture = null;
            bool hasTexture = ActivePipelineInstance.TryGetTexture(Name, out texture);

            if (hasTexture && texture is not null)
            {
                bool shouldRecreate = NeedsRecreate?.Invoke(texture) ?? false;
                if (shouldRecreate)
                {
                    if (Resize is not null)
                    {
                        Resize.Invoke(texture);
                        RegisterDescriptor(texture);
                        return;
                    }

                    texture = null;
                    hasTexture = false;
                }
                else
                {
                    RegisterDescriptor(texture);
                    return;
                }
            }

            if (TextureFactory is not null)
            {
                texture = TextureFactory();
                texture.Name = Name;
                TextureResourceDescriptor descriptor = BuildDescriptor(texture);
                ActivePipelineInstance.SetTexture(texture, descriptor);
            }
        }

        private void RegisterDescriptor(XRTexture texture)
        {
            TextureResourceDescriptor descriptor = BuildDescriptor(texture);
            ActivePipelineInstance.Resources.RegisterTextureDescriptor(descriptor);
        }

        private TextureResourceDescriptor BuildDescriptor(XRTexture texture)
        {
            TextureResourceDescriptor descriptor = TextureResourceDescriptor.FromTexture(texture, _lifetime);

            if ((_sizePolicyOverride ?? InferSizePolicy()) is RenderResourceSizePolicy policy)
                descriptor = descriptor with { SizePolicy = policy };

            return descriptor with { Name = texture.Name ?? descriptor.Name };
        }

        private RenderResourceSizePolicy? InferSizePolicy()
        {
            if (NeedsRecreate is null)
                return _sizePolicyOverride;

            MethodInfo method = NeedsRecreate.GetInvocationList()[0].Method;
            return method.Name switch
            {
                InternalRecreateMethod => RenderResourceSizePolicy.Internal(),
                FullRecreateMethod => RenderResourceSizePolicy.Window(),
                _ => _sizePolicyOverride
            };
        }
    }
}
