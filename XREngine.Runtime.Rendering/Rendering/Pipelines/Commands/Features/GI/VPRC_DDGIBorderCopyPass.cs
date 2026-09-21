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
    /// DDGI octahedral border copy compute pass.
    /// Replicates interior border texels into outer 1-pixel borders with diagonal and edge reflections
    /// for both irradiance and visibility atlases, ensuring seamless hardware bilinear filtering.
    /// Also advances the frame lifecycle on DDGIVolumeRuntimeState once updates complete.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_DDGIBorderCopyPass : VPRC_DDGIComputePass
    {
        public string IrradianceAtlasTextureName { get; set; } = DefaultRenderPipeline.DDGIIrradianceAtlasTextureName;
        public string VisibilityAtlasTextureName { get; set; } = DefaultRenderPipeline.DDGIVisibilityAtlasTextureName;

        private XRRenderProgram? _irradianceBorderProgram;
        private XRRenderProgram? _visibilityBorderProgram;

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

            int totalProbes = activeVolume.TotalProbeCount;
            if (totalProbes <= 0)
                return;

            var irradianceAtlas = ActivePipelineInstance.GetTexture<XRTexture>(IrradianceAtlasTextureName);
            var visibilityAtlas = ActivePipelineInstance.GetTexture<XRTexture>(VisibilityAtlasTextureName);

            if (irradianceAtlas is null || visibilityAtlas is null)
                return;

            EnsurePrograms();

            var context = DDGIFrameContext.Get(ActivePipelineInstance);
            var state = context.State;
            if (!context.CanRun(EDDGIUpdateStage.Visibility))
                return;

            var cascade = state.GetActiveCascade();
            int scheduledProbes = cascade?.ScheduledProbeCount ?? state.ScheduledProbeCount;
            if (scheduledProbes <= 0)
                return;

            totalProbes = cascade?.TotalProbeCount ?? activeVolume.TotalProbeCount;
            int probeOffset = cascade != null ? cascade.ProbeUpdateOffset : state.ProbeUpdateOffset;
            uint countX = (uint)Math.Max(1, cascade != null ? cascade.ProbeCounts.X : state.ProbeCounts.X);
            int cascadeIndex = cascade?.CascadeIndex ?? state.ActiveCascadeIndex;

            // Pass 1: Irradiance border copy (S = 6)
            if (_irradianceBorderProgram is not null)
            {
                _irradianceBorderProgram.BindImageTexture(0u, irradianceAtlas, 0, true, 0, XRRenderProgram.EImageAccess.ReadWrite, XRRenderProgram.EImageFormat.R11G11B10F);
                _irradianceBorderProgram.Uniform("uProbeCount", (uint)totalProbes);
                _irradianceBorderProgram.Uniform("uProbeOffset", (uint)probeOffset);
                _irradianceBorderProgram.Uniform("uScheduledProbeCount", (uint)scheduledProbes);
                _irradianceBorderProgram.Uniform("uProbeCountX", countX);
                _irradianceBorderProgram.Uniform("uProbeSize", DDGIVolumeRuntimeState.IrradianceProbeSize);
                _irradianceBorderProgram.Uniform("uCascadeIndex", cascadeIndex);
                _irradianceBorderProgram.DispatchCompute((uint)scheduledProbes, 1u, 1u, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
            }

            // Pass 2: Visibility border copy (S = 16) - skip if visibility omitted on this cascade
            if (_visibilityBorderProgram is not null && (cascade == null || cascade.VisibilityEnabled))
            {
                _visibilityBorderProgram.BindImageTexture(0u, visibilityAtlas, 0, true, 0, XRRenderProgram.EImageAccess.ReadWrite, XRRenderProgram.EImageFormat.RG16F);
                _visibilityBorderProgram.Uniform("uProbeCount", (uint)totalProbes);
                _visibilityBorderProgram.Uniform("uProbeOffset", (uint)probeOffset);
                _visibilityBorderProgram.Uniform("uScheduledProbeCount", (uint)scheduledProbes);
                _visibilityBorderProgram.Uniform("uProbeCountX", countX);
                _visibilityBorderProgram.Uniform("uProbeSize", DDGIVolumeRuntimeState.VisibilityProbeSize);
                _visibilityBorderProgram.Uniform("uCascadeIndex", cascadeIndex);
                _visibilityBorderProgram.DispatchCompute((uint)scheduledProbes, 1u, 1u, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
            }

            // Mark update cycle complete for this volume
            context.Complete();
        }

        private void EnsurePrograms()
        {
            var context = DDGIFrameContext.Get(ActivePipelineInstance);
            _irradianceBorderProgram = context.Program("ddgi_border_copy");
            _visibilityBorderProgram = context.Program("ddgi_border_copy_visibility");
        }
    }
}
