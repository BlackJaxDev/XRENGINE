using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_RenderMeshesPass : ViewportPopStateRenderCommand
    {
        public VPRC_RenderMeshesPass()
        {
            _render = RenderCPU;
        }
        public VPRC_RenderMeshesPass(int renderPass, bool gpuDispatch)
        {
            RenderPass = renderPass;
            GPUDispatch = gpuDispatch;
            _render = gpuDispatch ? (Action)RenderGPU : RenderCPU;
        }

        private Action _render;

        private bool _gpuDispatch = false;
        public bool GPUDispatch
        {
            get => _gpuDispatch;
            set => SetField(ref _gpuDispatch, value);
        }

        private int _renderPass = 0;
        public int RenderPass
        {
            get => _renderPass;
            set => SetField(ref _renderPass, value);
        }

        public void SetOptions(int renderPass, bool gpuDispatch)
        {
            RenderPass = renderPass;
            GPUDispatch = gpuDispatch;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (propName == nameof(GPUDispatch))
                _render = _gpuDispatch ? (Action)RenderGPU : RenderCPU;
        }

        protected override void Execute()
        {
            _render();
        }

        private void RenderGPU()
        {
            using var passScope = Engine.Rendering.State.PushRenderGraphPassIndex(_renderPass);
            var camera = ActivePipelineInstance.RenderState.SceneCamera;

            ActivePipelineInstance.MeshRenderCommands.RenderCPU(_renderPass, true, camera);
            ActivePipelineInstance.MeshRenderCommands.RenderGPU(_renderPass);

            // Safety net: if the GPU-indirect path fails/early-outs (common during init/validation),
            // fall back to CPU mesh rendering for this pass so viewports (e.g. VR eyes) don't go blank.
            if (ActivePipelineInstance.MeshRenderCommands.TryGetGpuPass(_renderPass, out var gpuPass) && gpuPass.VisibleCommandCount == 0)
                ActivePipelineInstance.MeshRenderCommands.RenderCPUMeshOnly(_renderPass);
        }

        private void RenderCPU()
        {
            using var passScope = Engine.Rendering.State.PushRenderGraphPassIndex(_renderPass);
            var camera = ActivePipelineInstance.RenderState.SceneCamera;
            ActivePipelineInstance.MeshRenderCommands.RenderCPU(_renderPass, false, camera);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            if (RenderPass < 0)
                return;

            string passName = $"RenderMeshes_{RenderPass}";
            var builder = context.Metadata.ForPass(RenderPass, passName, RenderGraphPassStage.Graphics);
            builder
                .UseEngineDescriptors()
                .UseMaterialDescriptors();

            if (context.CurrentRenderTarget is { } target)
            {
                builder.WithName($"{passName}_{target.Name}");
                var colorLoad = target.ConsumeColorLoadOp();
                var depthLoad = target.ConsumeDepthLoadOp();

                builder.UseColorAttachment(
                    MakeFboColorResource(target.Name),
                    target.ColorAccess,
                    colorLoad,
                    target.GetColorStoreOp());

                builder.UseDepthAttachment(
                    MakeFboDepthResource(target.Name),
                    target.DepthAccess,
                    depthLoad,
                    target.GetDepthStoreOp());
            }
        }
    }
} 
