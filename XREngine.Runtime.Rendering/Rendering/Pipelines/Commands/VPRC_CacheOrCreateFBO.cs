using System.Reflection;
using System.Text;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
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

        /// <summary>
        /// Optional predicate that forces a full FBO recreation when the cached
        /// instance is no longer compatible with the current pipeline state.
        /// </summary>
        public Func<XRFrameBuffer, bool>? NeedsRecreate { get; set; }

        private const string InternalSizeMethod = "GetDesiredFBOSizeInternal";
        private const string FullSizeMethod = "GetDesiredFBOSizeFull";

        private RenderResourceSizePolicy? _sizePolicyOverride;
        private RenderResourceLifetime _lifetime = RenderResourceLifetime.Persistent;

        public override string GpuProfilingName
            => string.IsNullOrWhiteSpace(Name) ? base.GpuProfilingName : $"{base.GpuProfilingName}[{Name}]";

        public VPRC_CacheOrCreateFBO SetOptions(string name, Func<XRFrameBuffer> factory, Func<(uint x, uint y)>? sizeVerifier, Func<XRFrameBuffer, bool>? needsRecreate = null)
        {
            Name = name;
            FrameBufferFactory = factory;
            SizeVerifier = sizeVerifier;
            NeedsRecreate = needsRecreate;
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
            try
            {
                ExecuteCore();
            }
            catch (Exception ex)
            {
                LogExecutionFailure(ex);
                throw;
            }
        }

        private void ExecuteCore()
        {
            if (Name is null)
                return;

            XRRenderPipelineInstance? pipelineInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (pipelineInstance is null)
                throw new InvalidOperationException($"Cannot cache or create FBO '{Name}' because there is no active render pipeline instance.");

            // Cache-or-create commands manage concrete pipeline resources, not variable aliases.
            // Using the broader name-resolution path here can suppress recreation after cache
            // invalidation by resolving a stale variable entry with the same name.
            if (pipelineInstance.Resources.TryGetFrameBuffer(Name, out var fbo) && fbo is not null)
            {
                if (NeedsRecreate?.Invoke(fbo) == true)
                {
                    // Destroy the previous instance so its GL/Vulkan wrappers tear down the
                    // underlying handles. Without this, the cache-or-create cycle leaks GPU
                    // objects on every reconfiguration (resize, AA change, pipeline rebuild),
                    // and NVIDIA's OpenGL driver eventually trips a FAST_FAIL_CORRUPT_LIST_ENTRY
                    // inside glNamedFramebufferTexture after enough orphaned attachments
                    // accumulate.
                    fbo.Destroy(true);
                    fbo = null;
                }

                if (fbo is null)
                {
                    XRFrameBuffer recreated = CreateFrameBufferOrThrow("recreating a cached FBO");
                    recreated.Name = Name;
                    FrameBufferResourceDescriptor recreatedDescriptor = BuildDescriptor(recreated);
                    pipelineInstance.SetFBO(recreated, recreatedDescriptor);
                    return;
                }

                if (SizeVerifier is not null)
                {
                    (uint x, uint y) = SizeVerifier();
                    if (fbo.Width != x || fbo.Height != y)
                        fbo.Resize(x, y);
                }

                RegisterDescriptor(pipelineInstance, fbo);
                return;
            }

            fbo = CreateFrameBufferOrThrow("creating a missing FBO");
            fbo.Name = Name;
            FrameBufferResourceDescriptor descriptor = BuildDescriptor(fbo);
            pipelineInstance.SetFBO(fbo, descriptor);
        }

        private XRFrameBuffer CreateFrameBufferOrThrow(string reason)
        {
            if (FrameBufferFactory is null)
                throw new InvalidOperationException($"Cannot cache or create FBO '{Name}' while {reason}: FrameBufferFactory is null.");

            XRFrameBuffer? frameBuffer = FrameBufferFactory();
            if (frameBuffer is null)
                throw new InvalidOperationException($"Cannot cache or create FBO '{Name}' while {reason}: FrameBufferFactory returned null.");

            return frameBuffer;
        }

        private void RegisterDescriptor(XRRenderPipelineInstance pipelineInstance, XRFrameBuffer frameBuffer)
        {
            FrameBufferResourceDescriptor descriptor = BuildDescriptor(frameBuffer);
            pipelineInstance.Resources.RegisterFrameBufferDescriptor(descriptor);
        }

        private FrameBufferResourceDescriptor BuildDescriptor(XRFrameBuffer frameBuffer)
        {
            FrameBufferResourceDescriptor descriptor = RenderResourceDescriptorFactory.FromFrameBuffer(frameBuffer, _lifetime);

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

        private void LogExecutionFailure(Exception ex)
        {
            string diagnostics;
            try
            {
                diagnostics = BuildFailureDiagnostics(ex);
            }
            catch (Exception diagnosticException)
            {
                diagnostics = $"Failed to build CacheOrCreateFBO diagnostics: {diagnosticException}";
            }

            Debug.RenderingWarningEvery(
                $"VPRC.CacheOrCreateFBO.{Name ?? "<unnamed>"}.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[RenderDiag] CacheOrCreateFBO failed:\n{0}",
                diagnostics);
        }

        private string BuildFailureDiagnostics(Exception ex)
        {
            XRRenderPipelineInstance? pipelineInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            var builder = new StringBuilder(1024);
            builder.Append("Exception: ").Append(ex.GetType().FullName).Append(": ").AppendLine(ex.Message);
            builder.Append("CommandName: ").AppendLine(GpuProfilingName);
            builder.Append("FboName: ").AppendLine(Name ?? "<null>");
            builder.Append("CommandIndex: ").Append(CommandContainer?.IndexOf(this).ToString() ?? "<no container>").AppendLine();
            builder.Append("ParentPipeline: ").AppendLine(ParentPipeline?.GetType().FullName ?? "<null>");
            builder.Append("ActivePipelineInstance: ").AppendLine(pipelineInstance?.GetType().FullName ?? "<null>");
            builder.Append("HasFactory: ").Append(FrameBufferFactory is not null).AppendLine();
            builder.Append("HasSizeVerifier: ").Append(SizeVerifier is not null).AppendLine();
            builder.Append("HasNeedsRecreate: ").Append(NeedsRecreate is not null).AppendLine();
            builder.Append("Lifetime: ").Append(_lifetime).AppendLine();
            builder.Append("SizePolicyOverride: ").Append(_sizePolicyOverride?.ToString() ?? "<null>").AppendLine();
            builder.Append("CachedFbo: ").AppendLine(DescribeCachedFrameBuffer(pipelineInstance));
            builder.Append("Stack:").AppendLine();
            builder.AppendLine(ex.StackTrace ?? "<no stack>");
            return builder.ToString();
        }

        private string DescribeCachedFrameBuffer(XRRenderPipelineInstance? pipelineInstance)
        {
            if (pipelineInstance is null)
                return "<no active pipeline instance>";
            if (Name is null)
                return "<command name is null>";

            return pipelineInstance.Resources.TryGetFrameBuffer(Name, out XRFrameBuffer? frameBuffer) && frameBuffer is not null
                ? DescribeFrameBuffer(frameBuffer)
                : "<not registered>";
        }

        private static string DescribeFrameBuffer(XRFrameBuffer frameBuffer)
        {
            int targetCount = frameBuffer.Targets?.Length ?? 0;
            int drawBufferCount = frameBuffer.DrawBuffers?.Length ?? 0;
            return $"{frameBuffer.GetType().FullName} Name='{frameBuffer.Name ?? "<null>"}' Size={frameBuffer.Width}x{frameBuffer.Height} Targets={targetCount} DrawBuffers={drawBufferCount} TextureTypes={frameBuffer.TextureTypes} Samples={frameBuffer.EffectiveSampleCount} Complete={frameBuffer.IsLastCheckComplete}";
        }
    }
}
