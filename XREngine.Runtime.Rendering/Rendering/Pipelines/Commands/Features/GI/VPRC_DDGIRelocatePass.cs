using System;
using System.Numerics;
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
    /// DDGI probe relocation and classification compute pass.
    /// Analyzes ray hit distances and backfaces to dynamically relocate probes away from geometry surfaces
    /// within strict dual-grid bounds [-0.5 * spacing, +0.5 * spacing], while classifying embedded probes as inactive
    /// and open-space probes as sleeping.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_DDGIRelocatePass : VPRC_DDGIComputePass
    {
        public string ProbeBufferName { get; set; } = DDGIResourceNames.ProbeStateBuffer;
        public string RayBufferName { get; set; } = DDGIResourceNames.RayBuffer;
        public string HitBufferName { get; set; } = DDGIResourceNames.HitBuffer;
        public string TriangleBufferVariableName { get; set; } = "DDGIGeometryTriangles";

        private XRRenderProgram? _relocateProgram;

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

            var state = context.State;
            if (!context.CanRun(EDDGIUpdateStage.Radiance))
                return;

            var cascade = state.GetActiveCascade();
            int scheduledProbes = cascade?.ScheduledProbeCount ?? state.ScheduledProbeCount;
            if (scheduledProbes <= 0)
                return;
            int totalProbes = cascade?.TotalProbeCount ?? state.TotalProbeCount;
            int probeOffset = cascade != null ? cascade.ProbeUpdateOffset : state.ProbeUpdateOffset;
            Vector3 probeSpacing = cascade?.ProbeSpacing ?? state.ProbeSpacing;

            var probeBuffer = ActivePipelineInstance.GetBuffer(ProbeBufferName);
            var rayBuffer = ActivePipelineInstance.GetBuffer(RayBufferName);
            var hitBuffer = ActivePipelineInstance.GetBuffer(HitBufferName);

            if (probeBuffer is null || rayBuffer is null || hitBuffer is null)
                return;

            ActivePipelineInstance.Variables.BufferVariables.TryGetValue(TriangleBufferVariableName, out var triangleBuffer);
            if (triangleBuffer is null)
                return;
            var variables = ActivePipelineInstance.Variables;
            if (!variables.BufferVariables.TryGetValue("DDGIGeometryMaterials", out var materialBuffer) ||
                !variables.BufferVariables.TryGetValue("DDGIGeometryNodes", out var nodeBuffer) ||
                !variables.BufferVariables.TryGetValue("DDGIGeometryAttributes", out var attributeBuffer) ||
                !variables.TextureVariables.TryGetValue("DDGIMaterialTextures", out var materialTextures))
                return;

            EnsureRelocateProgram();
            if (_relocateProgram is null)
                return;

            // Bind SSBOs:
            // binding 0: ProbeBuffer (readwrite)
            // binding 1: Rays (readonly)
            // binding 2: Hits (readonly)
            // binding 3: Triangles (readonly)
            _relocateProgram.BindBuffer(probeBuffer, 0);
            _relocateProgram.BindBuffer(rayBuffer, 1);
            _relocateProgram.BindBuffer(hitBuffer, 2);
            _relocateProgram.BindBuffer(triangleBuffer, 3);
            _relocateProgram.BindBuffer(materialBuffer, 4);
            _relocateProgram.BindBuffer(attributeBuffer, 5);
            _relocateProgram.BindBuffer(nodeBuffer, 6);
            _relocateProgram.Sampler("uMaterialTextures", materialTextures, 0);
            variables.TryGet("DDGIGeometryMaterialCount", out uint materialCount);
            _relocateProgram.Uniform("uMaterialCount", materialCount);
            variables.TryGet("DDGIGeometryNodeCount", out uint nodeCount);
            variables.TryGet("DDGIGeometryTriangleCount", out uint triangleCount);
            variables.TryGet("DDGIGeometryRootIndex", out uint rootIndex);
            _relocateProgram.Uniform("uNodeCount", nodeCount);
            _relocateProgram.Uniform("uTriangleCount", triangleCount);
            _relocateProgram.Uniform("uRootIndex", rootIndex);

            // Set uniforms
            _relocateProgram.Uniform("uProbeBaseOffset", (uint)(cascade?.ProbeOffset ?? 0));
            _relocateProgram.Uniform("uProbeOffset", (uint)probeOffset);
            _relocateProgram.Uniform("uScheduledProbeCount", (uint)scheduledProbes);
            _relocateProgram.Uniform("uProbeCount", (uint)totalProbes);
            _relocateProgram.Uniform("uRaysPerProbe", (uint)state.RaysPerProbe);
            _relocateProgram.Uniform("uProbeSpacing", probeSpacing);
            _relocateProgram.Uniform("uMinDistance", state.RelocationMinDistance);
            _relocateProgram.Uniform("uRelocationStep", state.RelocationStepSize);
            _relocateProgram.Uniform("uBackfaceThreshold", state.BackfaceHitRatioThreshold);
            _relocateProgram.Uniform("uRelocationEnabled", state.RelocationEnabled ? 1u : 0u);
            _relocateProgram.Uniform("uClassificationEnabled", state.ClassificationEnabled ? 1u : 0u);

            uint groups = ((uint)scheduledProbes + 31u) / 32u;
            _relocateProgram.DispatchCompute(groups, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
            context.Advance(EDDGIUpdateStage.Relocation);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_DDGIRelocatePass), ERenderGraphPassStage.Compute);
            builder.ReadWriteBuffer(ProbeBufferName);
            builder.ReadBuffer(RayBufferName);
            builder.ReadBuffer(HitBufferName);
            builder.ReadBuffer(TriangleBufferVariableName);
            builder.ReadBuffer("DDGIGeometryMaterials");
            builder.ReadBuffer("DDGIGeometryNodes");
            builder.ReadBuffer("DDGIGeometryAttributes");
            builder.SampleTexture(MakeTextureResource("DDGIMaterialTextures"));
        }

        private void EnsureRelocateProgram()
        {
            _relocateProgram = DDGIFrameContext.Get(ActivePipelineInstance).Program("ddgi_relocate");
        }


    }
}
