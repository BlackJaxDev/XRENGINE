using System.Reflection;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
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

        public override string CpuProfilingName
            => GetCpuProfilingNameWithSuffix(Name);

        public override string GpuProfilingName
            => GetGpuProfilingNameWithSuffix(Name);

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

            // Cache-or-create commands manage concrete pipeline resources, not variable aliases.
            // Using the broader name-resolution path here can suppress recreation after cache
            // invalidation by resolving a stale variable entry with the same name.
            bool hasTexture = ActivePipelineInstance.Resources.TryGetTexture(Name, out XRTexture? texture);
            bool recreatingTexture = false;

            if (hasTexture && texture is not null)
            {
                bool shouldRecreate = NeedsRecreate?.Invoke(texture) ?? false;
                if (shouldRecreate)
                {
                    if (Resize is not null)
                    {
                        try
                        {
                            Resize.Invoke(texture);
                        }
                        catch
                        {
                            // If resizing fails, fall back to recreation.
                        }

                        // Some "needs recreate" conditions are about type correctness, not size.
                        // Resizing won't fix those, so re-check and recreate if still invalid.
                        bool stillInvalid = NeedsRecreate?.Invoke(texture) ?? false;
                        if (!stillInvalid)
                        {
                            RegisterDescriptor(texture);
                            RecordChurn("Resized", "Resize");
                            ActivePipelineInstance.NotifyRenderResourcesChanged("VPRC_CacheOrCreateTexture.Resize");
                            return;
                        }
                    }

                    // Destroy the previous instance so its API wrappers tear down the
                    // underlying GPU handles. Without this, the cache-or-create cycle leaks
                    // texture objects on every pipeline reconfiguration, which combined with
                    // leaked FBO attachments eventually corrupts NVIDIA's per-texture
                    // attached-FBO list (FAST_FAIL_CORRUPT_LIST_ENTRY in glNamedFramebufferTexture).
                    RecordChurn("Recreated", "NeedsRecreate");
                    RecordChurn("Destroyed", "NeedsRecreate");
                    recreatingTexture = true;
                    texture.Destroy(true);
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
                RecordChurn("Created", recreatingTexture ? "Recreate" : "Missing");
                ActivePipelineInstance.SetTexture(texture, descriptor);
            }
        }

        private void RecordChurn(string eventName, string reason)
        {
            if (Name is null)
                return;

            RuntimeRenderingHostServices.Current.RecordRenderResourceChurn("Texture", Name, eventName, reason);
        }

        private void RegisterDescriptor(XRTexture texture)
        {
            TextureResourceDescriptor descriptor = BuildDescriptor(texture);
            ActivePipelineInstance.Resources.RegisterTextureDescriptor(descriptor);
        }

        private TextureResourceDescriptor BuildDescriptor(XRTexture texture)
        {
            TextureResourceDescriptor descriptor = RenderResourceDescriptorFactory.FromTexture(texture, _lifetime);

            if ((_sizePolicyOverride ?? InferSizePolicy()) is RenderResourceSizePolicy policy)
                descriptor = descriptor with { SizePolicy = policy };

            return descriptor with
            {
                Name = texture.Name ?? descriptor.Name,
                SupportsAliasing = _lifetime == RenderResourceLifetime.Transient
            };
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
