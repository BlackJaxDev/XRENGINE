using XREngine.Rendering;
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

            if (ActivePipelineInstance.TryGetBuffer(CounterBufferName, out XRDataBuffer? counterBuffer) && counterBuffer is not null)
            {
                counterBuffer.SetDataRawAtIndex(0, 0u);
                counterBuffer.SetDataRawAtIndex(1, 0u);
                counterBuffer.PushSubData();
            }

            if (!ActivePipelineInstance.TryGetTexture(HeadPointerTextureName, out XRTexture? headTexture) || headTexture is null)
                return;

            XRRenderProgram program = GetOrCreateClearHeadPointersProgram();
            ActivePipelineInstance.RenderState.ApplyScopedProgramBindings(program);
            program.BindImageTexture(0u, headTexture, 0, false, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R32UI);

            var viewport = ActivePipelineInstance.RenderState.WindowViewport;
            uint width = (uint)Math.Max(1, viewport?.InternalWidth ?? 1);
            uint height = (uint)Math.Max(1, viewport?.InternalHeight ?? 1);
            uint groupsX = (width + 15u) / 16u;
            uint groupsY = (height + 15u) / 16u;
            program.DispatchCompute(Math.Max(groupsX, 1u), Math.Max(groupsY, 1u), 1u, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.ShaderStorage);
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
