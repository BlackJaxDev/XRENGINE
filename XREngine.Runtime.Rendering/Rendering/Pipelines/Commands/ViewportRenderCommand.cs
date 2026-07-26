using System;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Pipelines.Commands
{
    public abstract class ViewportRenderCommand : XRBase
    {
        public const string SceneShaderPath = "Scene3D";
        private bool _shouldExecute = true;
        private bool _executeInShadowPass = false;

        [YamlIgnore]
        public ViewportRenderCommandContainer? CommandContainer { get; internal set; }

        [YamlIgnore]
        public RenderPipeline? ParentPipeline => CommandContainer?.ParentPipeline;
        public static XRRenderPipelineInstance ActivePipelineInstance => RuntimeEngine.Rendering.State.CurrentRenderingPipeline!;

        /// <summary>
        /// If true, the command will execute in the shadow pass.
        /// Otherwise, it will only execute in the main pass.
        /// </summary>
        public bool ExecuteInShadowPass
        {
            get => _executeInShadowPass;
            set => SetField(ref _executeInShadowPass, value);
        }
        /// <summary>
        /// If the command should execute.
        /// Can be used to skip commands while executing.
        /// Will be reset to true if execution is skipped.
        /// </summary>
        public bool ShouldExecute
        {
            get => _shouldExecute;
            set => SetField(ref _shouldExecute, value);
        }
        /// <summary>
        /// If true, this command's CollectVisible and SwapBuffers methods will be called.
        /// </summary>
        public virtual bool NeedsCollecVisible => false;

        private string? _gpuProfilingName;
        private string BaseGpuProfilingName => _gpuProfilingName ??= GetType().Name;
        public virtual string GpuProfilingName => BaseGpuProfilingName;
        private string? _cpuProfilingName;
        private string BaseCpuProfilingName => _cpuProfilingName ??= $"VPRC.{GetType().Name}";
        public virtual string CpuProfilingName => BaseCpuProfilingName;
        private string? _cpuProfilingNameSuffix;
        private string? _cpuProfilingNameWithSuffix;
        private string? _cpuShouldExecuteProfilingName;
        private string? _gpuProfilingNameSuffix;
        private string? _gpuProfilingNameWithSuffix;
        private string CpuShouldExecuteProfilingName => _cpuShouldExecuteProfilingName ??= $"{CpuProfilingName}.ShouldExecute";

        /// <summary>
        /// Gets the CPU profiling name with an optional suffix. 
        /// If the suffix is provided and different from the previous suffix, 
        /// it updates the cached profiling name with the new suffix.
        /// </summary>
        /// <param name="suffix">The suffix to append to the profiling name.</param>
        /// <returns>The CPU profiling name with the optional suffix.</returns>
        protected string GetCpuProfilingNameWithSuffix(string? suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix))
                return BaseCpuProfilingName;

            if (!string.Equals(_cpuProfilingNameSuffix, suffix, StringComparison.Ordinal))
            {
                _cpuProfilingNameSuffix = suffix;
                _cpuProfilingNameWithSuffix = $"{BaseCpuProfilingName}[{suffix}]";
            }

            return _cpuProfilingNameWithSuffix!;
        }

        /// <summary>
        /// Gets the GPU profiling name with an optional suffix. 
        /// If the suffix is provided and different from the previous suffix, 
        /// it updates the cached profiling name with the new suffix.
        /// </summary>
        /// <param name="suffix">The suffix to append to the profiling name.</param>
        /// <returns>The GPU profiling name with the optional suffix.</returns>
        protected string GetGpuProfilingNameWithSuffix(string? suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix))
                return BaseGpuProfilingName;

            if (!string.Equals(_gpuProfilingNameSuffix, suffix, StringComparison.Ordinal))
            {
                _gpuProfilingNameSuffix = suffix;
                _gpuProfilingNameWithSuffix = $"{BaseGpuProfilingName}[{suffix}]";
            }

            return _gpuProfilingNameWithSuffix!;
        }

        /// <summary>
        /// Executes the command.
        /// </summary>
        protected abstract void Execute();

        /// <summary>
        /// Allows commands to skip the profiler scope and execution when they can prove
        /// they have no work for the current frame.
        /// </summary>
        protected virtual bool ShouldExecuteThisFrame() => true;

        /// <summary>
        /// Collects visible objects for rendering.
        /// </summary>
        public virtual void CollectVisible()
        {

        }

        /// <summary>
        /// Swaps the buffers for this command, if applicable. 
        /// This method can be overridden by derived classes to implement specific buffer swapping behavior.
        /// </summary>
        public virtual void SwapBuffers()
        {

        }

        /// <summary>
        /// Describes the render pass for this command in the render graph.
        /// Derived classes can override this method to provide specific render pass descriptions.
        /// </summary>
        /// <param name="context">The context for describing the render pass.</param>
        internal virtual void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            // Default commands do not contribute metadata; derived commands can override.
        }

        /// <summary>
        /// Creates a render graph resource name for a color attachment based on the provided framebuffer name.
        /// </summary>
        /// <param name="fboName">The name of the framebuffer object.</param>
        /// <returns>The render graph resource name for the color attachment.</returns>
        protected static string MakeFboColorResource(string fboName)
            => RenderGraphResourceNames.MakeFboColor(fboName);

        /// <summary>
        /// Creates a render graph resource name for a color attachment based on the provided target name.
        /// If the target name is "OutputRenderTarget", it will return "OutputRenderTarget" instead of a framebuffer color resource.
        /// </summary>
        /// <param name="targetName">The name of the target.</param>
        /// <returns>The render graph resource name for the color attachment.</returns>
        protected static string MakeColorTargetResource(string targetName)
            => string.Equals(targetName, RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase)
                ? RenderGraphResourceNames.OutputRenderTarget
                : MakeFboColorResource(targetName);

        /// <summary>
        /// Creates a render graph resource name for a depth attachment based on the provided framebuffer name.
        /// </summary>
        /// <param name="fboName">The name of the framebuffer object.</param>
        /// <returns>The render graph resource name for the depth attachment.</returns>
        protected static string MakeFboDepthResource(string fboName)
            => RenderGraphResourceNames.MakeFboDepth(fboName);

        /// <summary>
        /// Creates a render graph resource name for a stencil attachment based on the provided framebuffer name.
        /// </summary>
        /// <param name="fboName">The name of the framebuffer object.</param>
        /// <returns>The render graph resource name for the stencil attachment.</returns>
        protected static string MakeFboStencilResource(string fboName)
            => RenderGraphResourceNames.MakeFboStencil(fboName);

        /// <summary>
        /// Creates a render graph resource name for a texture based on the provided texture name.
        /// </summary>
        /// <param name="textureName">The name of the texture.</param>
        /// <returns>The render graph resource name for the texture.</returns>
        protected static string MakeTextureResource(string textureName)
            => RenderGraphResourceNames.MakeTexture(textureName);

        /// <summary>
        /// Sets the render graph resources for this command.
        /// </summary>
        /// <param name="builder">The render pass builder used to describe the pass.</param>
        /// <param name="target">The render target binding.</param>
        /// <param name="depthLoad">The load operation for the depth attachment.</param>
        /// <param name="stencilLoad">The load operation for the stencil attachment.</param>
        protected static void UseRenderTargetDepthStencilAttachments(
            RenderPassBuilder builder,
            RenderTargetBinding target,
            ERenderPassLoadOp depthLoad,
            ERenderPassLoadOp stencilLoad)
        {
            if (IsColorOnlyPostProcessOutputTarget(target.Name))
                return;

            builder.UseDepthAttachment(
                MakeFboDepthResource(target.Name),
                target.DepthAccess,
                depthLoad,
                target.GetDepthStoreOp());

            // FBO metadata describes depth and stencil as separate graph resources.
            // Vulkan resolves the stencil resource only when the runtime FBO actually
            // has a stencil aspect, so declaring it here is safe for depth-only FBOs
            // and keeps depth-stencil FBOs from losing stencil writes in on-top passes.
            builder.UseStencilAttachment(
                MakeFboStencilResource(target.Name),
                target.DepthAccess,
                stencilLoad,
                target.GetStencilStoreOp());
        }

        /// <summary>
        /// Determines whether the given target name corresponds to a color-only post-process output target.
        /// </summary>
        /// <param name="targetName">The name of the render target.</param>
        /// <returns>True if the target is a color-only post-process output target; otherwise, false.</returns>
        private static bool IsColorOnlyPostProcessOutputTarget(string targetName)
            => string.Equals(targetName, DefaultRenderPipeline.PostProcessOutputFBOName, StringComparison.Ordinal)
            || string.Equals(targetName, DefaultRenderPipeline.FinalPostProcessOutputFBOName, StringComparison.Ordinal);

        /// <summary>
        /// Called when the command is attached to a container. 
        /// Derived classes can override this method to perform any necessary initialization or setup when the command is added to a container.
        /// </summary>
        internal virtual void OnAttachedToContainer()
        {
            // Default implementation does nothing; derived classes can override to perform actions when attached to a container.
        }
    
        /// <summary>
        /// Called when the parent pipeline is assigned to this command.
        /// Derived classes can override this method to perform any necessary initialization or setup when the parent pipeline is assigned.
        /// </summary>
        internal virtual void OnParentPipelineAssigned()
        {
            /// Default implementation does nothing; derived classes can override to perform actions when the parent pipeline is assigned.
        }

        /// <summary>
        /// Called when the command is detached from its container.
        /// Derived classes can override this method to perform any necessary cleanup or teardown when the command is removed from a container.
        /// </summary>
        /// <param name="instance">The instance of the render pipeline.</param>
        internal virtual void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
            // Default implementation does nothing; derived classes can override to allocate resources.
        }

        /// <summary>
        /// Called when the command is detached from its container.
        /// Derived classes can override this method to perform any necessary cleanup or teardown when the command is removed from a container.
        /// </summary>
        /// <param name="instance">The instance of the render pipeline.</param>
        internal virtual void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
            // Default implementation does nothing; derived classes can override to release resources.
        }

        /// <summary>
        /// Executes the command if it should be executed for the current frame.
        /// This method checks the ShouldExecute property and calls ShouldExecuteThisFrame() to determine if the command should be executed. If so, it starts CPU and GPU profiling scopes and calls the Execute() method.
        /// If the command is not executed, the ShouldExecute property is reset to true for the next frame.
        /// </summary>
        public void ExecuteIfShould()
        {
            bool shouldExecute = ShouldExecute;
            if (shouldExecute)
            {
                using var shouldExecuteScope = RuntimeRenderingHostServices.Profiling.StartProfileScope(CpuShouldExecuteProfilingName);
                shouldExecute = ShouldExecuteThisFrame();
            }

            if (shouldExecute)
            {
                using var cpuScope = RuntimeRenderingHostServices.Profiling.StartProfileScope(CpuProfilingName);
                RenderPipelineGpuProfiler gpuProfiler = RenderPipelineGpuProfiler.Instance;
                using var gpuScope = gpuProfiler.ShouldInstrumentCommandScopes
                    ? gpuProfiler.StartScope(this)
                    : default;
                Execute();
            }
            ShouldExecute = true;
        }
    }
}
