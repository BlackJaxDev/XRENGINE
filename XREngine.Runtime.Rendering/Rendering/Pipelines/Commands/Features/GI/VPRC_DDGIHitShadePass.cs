using System;
using System.Numerics;
using XREngine.Components.Lights;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.GI.DDGI;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    /// <summary>
    /// DDGI GPU hit shading compute pass.
    /// Evaluates scene directional, point and spot lights, material emission, and multi-bounce indirect
    /// irradiance via DDGI probe sampling for all traced rays, outputting shaded radiance into DDGIRayRadianceBuffer.
    /// </summary>
    [RenderPipelineScriptCommand]
    public class VPRC_DDGIHitShadePass : VPRC_DDGIComputePass
    {
        public string RayBufferName { get; set; } = DefaultRenderPipeline.DDGIRayBufferName;
        public string HitBufferName { get; set; } = DefaultRenderPipeline.DDGIHitBufferName;
        public string RayRadianceBufferName { get; set; } = DefaultRenderPipeline.DDGIRayRadianceBufferName;
        public string ProbeBufferName { get; set; } = DefaultRenderPipeline.DDGIProbeStateBufferName;
        public string TriangleBufferVariableName { get; set; } = "DDGIGeometryTriangles";

        public string IrradianceAtlasTextureName { get; set; } = DefaultRenderPipeline.DDGIIrradianceAtlasTextureName;
        public string VisibilityAtlasTextureName { get; set; } = DefaultRenderPipeline.DDGIVisibilityAtlasTextureName;

        private XRRenderProgram? _shadeProgram;

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
            if (!context.CanRun(EDDGIUpdateStage.Hits))
                return;

            var cascade = state.GetActiveCascade();
            int scheduledProbes = cascade?.ScheduledProbeCount ?? state.ScheduledProbeCount;
            int activeRays = scheduledProbes * activeVolume.RaysPerProbe;
            if (activeRays <= 0)
                return;

            var rayBuffer = ActivePipelineInstance.GetBuffer(RayBufferName);
            var hitBuffer = ActivePipelineInstance.GetBuffer(HitBufferName);
            var radianceBuffer = ActivePipelineInstance.GetBuffer(RayRadianceBufferName);
            var probeBuffer = ActivePipelineInstance.GetBuffer(ProbeBufferName);

            if (rayBuffer is null || hitBuffer is null || radianceBuffer is null || probeBuffer is null)
                return;

            var irradianceAtlas = ActivePipelineInstance.GetTexture<XRTexture>(IrradianceAtlasTextureName);
            var visibilityAtlas = ActivePipelineInstance.GetTexture<XRTexture>(VisibilityAtlasTextureName);
            if (irradianceAtlas is null || visibilityAtlas is null)
                return;

            ActivePipelineInstance.Variables.BufferVariables.TryGetValue(TriangleBufferVariableName, out var triangleBuffer);
            if (triangleBuffer is null)
                return;
            var variables = ActivePipelineInstance.Variables;
            if (!variables.BufferVariables.TryGetValue("DDGIGeometryMaterials", out var materialBuffer) || materialBuffer is null ||
                !variables.BufferVariables.TryGetValue("DDGIGeometryNodes", out var nodeBuffer) || nodeBuffer is null ||
                !variables.BufferVariables.TryGetValue("DDGIGeometryAttributes", out var attributeBuffer) ||
                !variables.TextureVariables.TryGetValue("DDGIMaterialTextures", out var materialTextures))
                return;

            EnsureShadeProgram();
            if (_shadeProgram is null)
                return;

            // Bind SSBOs:
            // binding 0: Rays
            // binding 1: Hits
            // binding 2: Triangles
            // binding 3: RayRadianceBuffer
            // binding 4: ProbeStateBuffer
            _shadeProgram.BindBuffer(rayBuffer, 0);
            _shadeProgram.BindBuffer(hitBuffer, 1);
            _shadeProgram.BindBuffer(triangleBuffer, 2);
            _shadeProgram.BindBuffer(radianceBuffer, 3);
            _shadeProgram.BindBuffer(probeBuffer, 4);
            _shadeProgram.BindBuffer(materialBuffer, 5);
            _shadeProgram.BindBuffer(nodeBuffer, 6);
            _shadeProgram.BindBuffer(attributeBuffer, 7);
            _shadeProgram.Sampler("uMaterialTextures", materialTextures, 2);
            variables.TryGet("DDGIGeometryNodeCount", out uint nodeCount);
            variables.TryGet("DDGIGeometryTriangleCount", out uint triangleCount);
            variables.TryGet("DDGIGeometryRootIndex", out uint rootIndex);
            _shadeProgram.Uniform("uNodeCount", nodeCount);
            _shadeProgram.Uniform("uTriangleCount", triangleCount);
            _shadeProgram.Uniform("uRootIndex", rootIndex);
            variables.TryGet("DDGIGeometryMaterialCount", out uint materialCount);
            _shadeProgram.Uniform("uMaterialCount", materialCount);

            // Bind textures for multi-bounce DDGI sampling:
            _shadeProgram.Sampler("uDDGIIrradianceAtlas", irradianceAtlas, 0);
            _shadeProgram.Sampler("uDDGIVisibilityAtlas", visibilityAtlas, 1);

            // Set uniforms
            _shadeProgram.Uniform("uRayCount", (uint)activeRays);

            if (!DDGIEnvironmentResources.Bind(_shadeProgram, world))
                return;
            if (!DDGILightResources.Bind(_shadeProgram, ActivePipelineInstance, world))
                return;

            // DDGI probe sampling constants
            _shadeProgram.Uniform("uGridMin", state.GridMin);
            _shadeProgram.Uniform("uProbeSpacing", state.ProbeSpacing);
            _shadeProgram.Uniform("uProbeCounts", state.ProbeCounts);
            _shadeProgram.Uniform("uNormalBias", state.NormalBias);
            _shadeProgram.Uniform("uViewBias", state.ViewBias);
            _shadeProgram.Uniform("uChebyshevPower", state.ChebyshevPower);
            // Intensity is a presentation control, not a multiplier on each bounce.
            _shadeProgram.Uniform("uIntensity", 1.0f);
            _shadeProgram.Uniform("uIrradianceAtlasSize", new Vector2(state.IrradianceAtlasWidth, state.IrradianceAtlasHeight));
            _shadeProgram.Uniform("uVisibilityAtlasSize", new Vector2(state.VisibilityAtlasWidth, state.VisibilityAtlasHeight));

            DDGIVolumeRuntimeState.UploadCascadeUniforms(_shadeProgram, state);

            uint groups = ((uint)activeRays + 31u) / 32u;
            _shadeProgram.DispatchCompute(groups, 1u, 1u, EMemoryBarrierMask.ShaderStorage);
            context.Advance(EDDGIUpdateStage.Radiance);
        }

        private void EnsureShadeProgram()
        {
            _shadeProgram = DDGIFrameContext.Get(ActivePipelineInstance).Program("ddgi_hit_shade");
        }


    }
}
