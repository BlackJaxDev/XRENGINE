using System;
using XREngine.Components.Lights;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.GI.DDGI;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// DDGI irradiance atlas update compute pass.
    /// Integrates cosine-weighted probe ray radiance into 6x6 probe octahedral tiles in the irradiance atlas
    /// using temporal hysteresis and warm-start logic.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_DDGIUpdateIrradiancePass : VPRC_DDGIComputePass
    {
        public string RayBufferName { get; set; } = DefaultRenderPipeline.DDGIRayBufferName;
        public string RayRadianceBufferName { get; set; } = DefaultRenderPipeline.DDGIRayRadianceBufferName;
        public string IrradianceAtlasTextureName { get; set; } = DefaultRenderPipeline.DDGIIrradianceAtlasTextureName;

        private XRRenderProgram? _updateProgram;

        protected override bool ShouldExecuteThisFrame()
            => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline is
                IGlobalIlluminationPipelineProvider { UsesDDGI: true };

        protected override void ExecuteDDGI()
        {
            if (ActivePipelineInstance.Pipeline is not IGlobalIlluminationPipelineProvider { UsesDDGI: true })
                return;

            var world = ActivePipelineInstance.RenderState.WindowViewport?.World
                ?? RuntimeEngine.Rendering.State.RenderingWorld;
            if (world is null || !DDGIVolumeComponent.Registry.TryGetFirstActive(world, out var activeVolume) || activeVolume is null || !activeVolume.VolumeEnabled)
                return;

            var context = DDGIFrameContext.Get(ActivePipelineInstance);
            var state = context.State;
            if (!context.CanRun(EDDGIUpdateStage.Relocation))
                return;

            var cascade = state.GetActiveCascade();
            int scheduledProbes = cascade?.ScheduledProbeCount ?? state.ScheduledProbeCount;
            if (scheduledProbes <= 0)
                return;

            int totalProbes = cascade?.TotalProbeCount ?? activeVolume.TotalProbeCount;
            int probeOffset = cascade != null ? cascade.ProbeUpdateOffset : state.ProbeUpdateOffset;
            int countX = cascade != null ? cascade.ProbeCounts.X : state.ProbeCounts.X;
            int cascadeIndex = cascade?.CascadeIndex ?? state.ActiveCascadeIndex;

            var rayBuffer = ActivePipelineInstance.GetBuffer(RayBufferName);
            var radianceBuffer = ActivePipelineInstance.GetBuffer(RayRadianceBufferName);
            var irradianceAtlas = ActivePipelineInstance.GetTexture<XRTexture>(IrradianceAtlasTextureName);

            if (rayBuffer is null || radianceBuffer is null || irradianceAtlas is null)
                return;

            EnsureUpdateProgram();
            if (_updateProgram is null)
                return;

            // Bind SSBOs:
            // binding 0: Rays
            // binding 1: RayRadianceBuffer
            _updateProgram.BindBuffer(rayBuffer, 0);
            _updateProgram.BindBuffer(radianceBuffer, 1);

            // Bind 2D array image read/write to unit 0
            _updateProgram.BindImageTexture(0u, irradianceAtlas, 0, true, 0, XRRenderProgram.EImageAccess.ReadWrite, XRRenderProgram.EImageFormat.R11G11B10F);

            float hysteresis = state.ResolveEffectiveHysteresis();

            _updateProgram.Uniform("uProbeCount", (uint)totalProbes);
            _updateProgram.Uniform("uProbeOffset", (uint)probeOffset);
            _updateProgram.Uniform("uScheduledProbeCount", (uint)scheduledProbes);
            _updateProgram.Uniform("uRaysPerProbe", (uint)state.RaysPerProbe);
            _updateProgram.Uniform("uProbeCountX", (uint)Math.Max(1, countX));
            _updateProgram.Uniform("uHysteresis", hysteresis);
            _updateProgram.Uniform("uCascadeIndex", cascadeIndex);

            _updateProgram.DispatchCompute((uint)scheduledProbes, 1u, 1u, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
            context.Advance(EDDGIUpdateStage.Irradiance);
        }

        private void EnsureUpdateProgram()
        {
            _updateProgram = DDGIFrameContext.Get(ActivePipelineInstance).Program("ddgi_update_irradiance");
        }
    }
}
