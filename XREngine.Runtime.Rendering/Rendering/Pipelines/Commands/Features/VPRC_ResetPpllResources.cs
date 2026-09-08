using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public sealed class VPRC_ResetPpllResources : ViewportRenderCommand
    {
        public string CounterBufferName { get; set; } = string.Empty;
        public string HeadPointerTextureName { get; set; } = string.Empty;
        public string ClearHeadPointersComputeShaderPath { get; set; } = string.Empty;

        private XRRenderProgram? _clearHeadPointersProgram;
        private string? _clearHeadPointersProgramPath;

        protected override void Execute()
        {
            if (string.IsNullOrWhiteSpace(CounterBufferName) ||
                string.IsNullOrWhiteSpace(HeadPointerTextureName) ||
                string.IsNullOrWhiteSpace(ClearHeadPointersComputeShaderPath))
            {
                return;
            }

            if (!ActivePipelineInstance.TryGetBuffer(CounterBufferName, out XRDataBuffer? counterBuffer) || counterBuffer is null)
                return;

            if (!ActivePipelineInstance.TryGetTexture(HeadPointerTextureName, out XRTexture? headTexture) || headTexture is null)
                return;

            XRRenderProgram program = GetOrCreateClearHeadPointersProgram();
            ActivePipelineInstance.RenderState.ApplyScopedProgramBindings(program);
            counterBuffer.BindTo(program, 25u);
            program.BindImageTexture(0u, headTexture, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R32UI);

            uint width = Math.Max((uint)headTexture.WidthHeightDepth.X, 1u);
            uint height = Math.Max((uint)headTexture.WidthHeightDepth.Y, 1u);
            uint groupsX = (width + 15u) / 16u;
            uint groupsY = (height + 15u) / 16u;
            program.DispatchCompute(Math.Max(groupsX, 1u), Math.Max(groupsY, 1u), 1u, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.ShaderStorage);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            if (string.IsNullOrWhiteSpace(CounterBufferName) ||
                string.IsNullOrWhiteSpace(HeadPointerTextureName))
            {
                return;
            }

            var builder = context.GetOrCreateSyntheticPass(
                nameof(VPRC_ResetPpllResources),
                ERenderGraphPassStage.Compute);
            builder
                .WriteBuffer(CounterBufferName)
                .ReadWriteTexture(MakeTextureResource(HeadPointerTextureName));
        }

        private XRRenderProgram GetOrCreateClearHeadPointersProgram()
        {
            if (_clearHeadPointersProgram is null || _clearHeadPointersProgramPath != ClearHeadPointersComputeShaderPath)
            {
                _clearHeadPointersProgram = new(false, false, XRShader.EngineShader(ClearHeadPointersComputeShaderPath, EShaderType.Compute));
                _clearHeadPointersProgramPath = ClearHeadPointersComputeShaderPath;
            }

            return _clearHeadPointersProgram;
        }
    }
}
