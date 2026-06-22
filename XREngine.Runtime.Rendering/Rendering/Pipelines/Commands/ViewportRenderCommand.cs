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

        public virtual void CollectVisible()
        {

        }
        public virtual void SwapBuffers()
        {

        }

        internal virtual void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            // Default commands do not contribute metadata; derived commands can override.
        }

        protected static string MakeFboColorResource(string fboName)
            => RenderGraphResourceNames.MakeFboColor(fboName);

        protected static string MakeColorTargetResource(string targetName)
            => string.Equals(targetName, RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase)
                ? RenderGraphResourceNames.OutputRenderTarget
                : MakeFboColorResource(targetName);

        protected static string MakeFboDepthResource(string fboName)
            => RenderGraphResourceNames.MakeFboDepth(fboName);

        protected static string MakeFboStencilResource(string fboName)
            => RenderGraphResourceNames.MakeFboStencil(fboName);

        protected static string MakeTextureResource(string textureName)
            => RenderGraphResourceNames.MakeTexture(textureName);

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

        private static bool IsColorOnlyPostProcessOutputTarget(string targetName)
            => string.Equals(targetName, DefaultRenderPipeline.PostProcessOutputFBOName, StringComparison.Ordinal)
            || string.Equals(targetName, DefaultRenderPipeline.FinalPostProcessOutputFBOName, StringComparison.Ordinal);

        internal virtual void OnAttachedToContainer()
        {
        }

        internal virtual void OnParentPipelineAssigned()
        {
        }

        internal virtual void AllocateContainerResources(XRRenderPipelineInstance instance)
        {
        }

        internal virtual void ReleaseContainerResources(XRRenderPipelineInstance instance)
        {
        }
        public void ExecuteIfShould()
        {
            bool shouldExecute = ShouldExecute;
            if (shouldExecute)
            {
                using var shouldExecuteScope = RuntimeRenderingHostServices.Current.StartProfileScope(CpuShouldExecuteProfilingName);
                shouldExecute = ShouldExecuteThisFrame();
            }

            if (shouldExecute)
            {
                using var cpuScope = RuntimeRenderingHostServices.Current.StartProfileScope(CpuProfilingName);
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
