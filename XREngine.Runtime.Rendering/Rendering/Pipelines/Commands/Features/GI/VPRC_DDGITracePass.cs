using System;
using XREngine.Components.Lights;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.GI.DDGI;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// DDGI GPU BVH trace compute pass.
    /// Traverses the scene BVH entirely on the GPU via direct compute dispatch with zero host readback,
    /// populating the persistent DDGIHitBuffer SSBO with closest-hit records.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_DDGITracePass : VPRC_DDGIComputePass
    {
        public string RayBufferName { get; set; } = DDGIResourceNames.RayBuffer;
        public string HitBufferName { get; set; } = DDGIResourceNames.HitBuffer;
        public string ReadyVariableName { get; set; } = "DDGIGeometryReady";
        public string NodeCountVariableName { get; set; } = "DDGIGeometryNodeCount";
        public string NodeBufferVariableName { get; set; } = "DDGIGeometryNodes";
        public string TriangleBufferVariableName { get; set; } = "DDGIGeometryTriangles";

        private XRRenderProgram? _traceProgram;

        protected override bool ShouldExecuteThisFrame()
            => GlobalIlluminationPlanSelection.IsSelectedAndSupported(
                RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline,
                EGlobalIlluminationMode.DDGI);

        protected override void ExecuteDDGI()
        {
            if (!GlobalIlluminationPlanSelection.IsSelectedAndSupported(ActivePipelineInstance.Pipeline, EGlobalIlluminationMode.DDGI))
                return;

            var world = ActivePipelineInstance.RenderState.WindowViewport?.World
                ?? RuntimeEngine.Rendering.State.RenderingWorld;
            var context = DDGIFrameContext.Get(ActivePipelineInstance);
            if (world is null || !context.TryGetSelectedVolume(world, out var activeVolume) || activeVolume is null || !activeVolume.VolumeEnabled)
                return;

            var variables = ActivePipelineInstance.Variables;
            if (!variables.TryGet(ReadyVariableName, out bool ready) || !ready)
                return;

            if (!variables.BufferVariables.TryGetValue(NodeBufferVariableName, out var nodeBuffer) || nodeBuffer is null)
                return;

            var rayBuffer = ActivePipelineInstance.GetBuffer(RayBufferName);
            var hitBuffer = ActivePipelineInstance.GetBuffer(HitBufferName);
            if (rayBuffer is null || hitBuffer is null)
                return;

            var state = context.State;
            if (!context.CanRun(EDDGIUpdateStage.Rays))
                return;

            var cascade = state.GetActiveCascade();
            int scheduledProbes = cascade?.ScheduledProbeCount ?? state.ScheduledProbeCount;
            int activeRays = scheduledProbes * activeVolume.RaysPerProbe;
            if (activeRays <= 0)
                return;

            variables.BufferVariables.TryGetValue(TriangleBufferVariableName, out var triangleBuffer);
            if (triangleBuffer is null)
                return;
            if (!variables.BufferVariables.TryGetValue("DDGIGeometryMaterials", out var materialBuffer) ||
                !variables.BufferVariables.TryGetValue("DDGIGeometryAttributes", out var attributeBuffer) ||
                !variables.TextureVariables.TryGetValue("DDGIMaterialTextures", out var materialTextures))
                return;

            EnsureTraceProgram();
            if (_traceProgram is null)
                return;

            // Bind SSBOs per BvhRaycastCore contract:
            // binding 0: Rays
            // binding 1: Nodes
            // binding 2: Triangles
            // binding 3: Hits
            _traceProgram.BindBuffer(rayBuffer, 0);
            _traceProgram.BindBuffer(nodeBuffer, 1);
            _traceProgram.BindBuffer(triangleBuffer, 2);
            _traceProgram.BindBuffer(hitBuffer, 3);
            _traceProgram.BindBuffer(materialBuffer, 5);
            _traceProgram.BindBuffer(attributeBuffer, 6);
            _traceProgram.Sampler("uMaterialTextures", materialTextures, 0);
            variables.TryGet("DDGIGeometryMaterialCount", out uint materialCount);
            _traceProgram.Uniform("uMaterialCount", materialCount);

            _traceProgram.Uniform("uRayCount", (uint)activeRays);
            _traceProgram.Uniform("uRootIndex", 0u);
            variables.TryGet(NodeCountVariableName, out uint nodeCount);
            variables.TryGet("DDGIGeometryTriangleCount", out uint triangleCount);
            _traceProgram.Uniform("uNodeCount", nodeCount);
            _traceProgram.Uniform("uTriangleCount", triangleCount);
            _traceProgram.Uniform("uPacketWidth", 32u);
            _traceProgram.Uniform("uUsePacketMode", 0u);
            _traceProgram.Uniform("uAnyHitMode", 0u);
            _traceProgram.Uniform("uMaxStackDepth", 64u);

            uint groups = ((uint)activeRays + 31u) / 32u;
            _traceProgram.DispatchCompute(groups, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
            context.Advance(EDDGIUpdateStage.Hits);
        }

        private void EnsureTraceProgram()
        {
            _traceProgram = DDGIFrameContext.Get(ActivePipelineInstance).Program("ddgi_trace");
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_DDGITracePass), ERenderGraphPassStage.Compute);
            builder.ReadBuffer("DDGIGeometryMaterials");
            builder.ReadBuffer("DDGIGeometryAttributes");
            builder.SampleTexture(MakeTextureResource("DDGIMaterialTextures"));
            builder.ReadBuffer(RayBufferName);
            builder.ReadBuffer(NodeBufferVariableName);
            builder.ReadBuffer(TriangleBufferVariableName);
            builder.WriteBuffer(HitBufferName);
        }



    }
}
