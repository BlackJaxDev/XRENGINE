using System;
using System.Numerics;
using XREngine.Components.Lights;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering.GI.Contracts;
using XREngine.Rendering.GI.DDGI;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// DDGI ray generation compute pass.
    /// Distributes stratified probe rays using spherical Fibonacci with per-frame pseudo-random rotation,
    /// populating the persistent DDGIRayBuffer SSBO entirely on GPU.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_DDGIRaygenPass : VPRC_DDGIComputePass
    {
        public string ProbeStateBufferName { get; set; } = DDGIResourceNames.ProbeStateBuffer;
        public string RayBufferName { get; set; } = DDGIResourceNames.RayBuffer;

        private XRRenderProgram? _raygenProgram;

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

            var probeBuffer = ActivePipelineInstance.GetBuffer(ProbeStateBufferName);
            var rayBuffer = ActivePipelineInstance.GetBuffer(RayBufferName);
            if (probeBuffer is null || rayBuffer is null)
                return;

            if (!context.TryBegin(ActivePipelineInstance, activeVolume))
                return;
            var state = context.State;
            var cascade = state.GetActiveCascade();
            int scheduledProbes = cascade.ScheduledProbeCount;
            int totalProbes = cascade.TotalProbeCount;
            int probeOffset = cascade.ProbeUpdateOffset;
            Vector3 gridMin = cascade.GridMin;
            Vector3 probeSpacing = cascade.ProbeSpacing;
            IVector3 probeCounts = cascade.ProbeCounts;
            int activeRays = checked(scheduledProbes * state.RaysPerProbe);
            if (activeRays <= 0)
                return;
            EnsureRaygenProgram();
            if (_raygenProgram is null)
                return;
            uint frameIndex = state.FrameIndex;

            // Create pseudo-random frame rotation
            float golden1 = 0.6180339887f;
            float golden2 = 0.3819660113f;
            float yaw = (frameIndex * golden1 * MathF.PI * 2.0f) % (MathF.PI * 2.0f);
            float pitch = (frameIndex * golden2 * MathF.PI * 2.0f) % (MathF.PI * 2.0f);
            Matrix4x4 randomRotation = Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, 0.0f);

            _raygenProgram.BindBuffer(probeBuffer, 0);
            _raygenProgram.BindBuffer(rayBuffer, 1);

            _raygenProgram.Uniform("uRayCount", (uint)activeRays);
            _raygenProgram.Uniform("uRaysPerProbe", (uint)activeVolume.RaysPerProbe);
            _raygenProgram.Uniform("uFrameIndex", frameIndex);
            _raygenProgram.Uniform("uProbeCount", (uint)totalProbes);
            _raygenProgram.Uniform("uProbeBaseOffset", (uint)cascade.ProbeOffset);
            _raygenProgram.Uniform("uProbeOffset", (uint)probeOffset);
            _raygenProgram.Uniform("uScheduledProbeCount", (uint)scheduledProbes);
            _raygenProgram.Uniform("uGridMin", gridMin);
            _raygenProgram.Uniform("uProbeSpacing", probeSpacing);
            _raygenProgram.Uniform("uProbeCounts", probeCounts);
            _raygenProgram.Uniform("uRandomRotation", randomRotation);

            uint groups = ((uint)activeRays + 31u) / 32u;
            _raygenProgram.DispatchCompute(groups, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
            context.Advance(EDDGIUpdateStage.Rays);
        }

        private void EnsureRaygenProgram()
        {
            _raygenProgram = DDGIFrameContext.Get(ActivePipelineInstance).Program("ddgi_raygen");
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            var builder = context.GetOrCreateSyntheticPass(nameof(VPRC_DDGIRaygenPass), ERenderGraphPassStage.Compute);
            builder.ReadWriteBuffer(ProbeStateBufferName);
            builder.WriteBuffer(RayBufferName);
            builder.ReadWriteTexture(MakeTextureResource(DDGIResourceNames.IrradianceAtlas));
            builder.ReadWriteTexture(MakeTextureResource(DDGIResourceNames.VisibilityAtlas));
        }
    }
}
