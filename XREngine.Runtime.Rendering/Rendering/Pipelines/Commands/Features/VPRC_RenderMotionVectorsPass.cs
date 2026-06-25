using System;
using System.Collections.Generic;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.RenderGraph;
using Silk.NET.OpenGL;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_RenderMotionVectorsPass : ViewportRenderCommand
    {
        private static readonly int[] DefaultRenderPasses =
        [
            (int)EDefaultRenderPass.Background,
            (int)EDefaultRenderPass.OpaqueDeferred,
            (int)EDefaultRenderPass.DeferredDecals,
            (int)EDefaultRenderPass.OpaqueForward,
            (int)EDefaultRenderPass.MaskedForward,
            (int)EDefaultRenderPass.WeightedBlendedOitForward,
            (int)EDefaultRenderPass.TransparentForward,
            (int)EDefaultRenderPass.OnTopForward,
        ];

        public int[] RenderPasses { get; set; } = DefaultRenderPasses;

        private bool _gpuDispatch = false;
        public bool GPUDispatch
        {
            get => _gpuDispatch;
            set => SetField(ref _gpuDispatch, value);
        }

        public void SetOptions(bool gpuDispatch, IReadOnlyList<int>? renderPasses = null)
        {
            GPUDispatch = gpuDispatch;
            if (renderPasses is not null)
                RenderPasses = [.. renderPasses];
        }

        protected override bool ShouldExecuteThisFrame()
        {
            if (RuntimeEngine.Rendering.State.IsSceneCapturePass || RenderPasses.Length == 0)
                return false;

            XRRenderPipelineInstance? activeInstance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            if (activeInstance is null)
                return false;

            for (int i = 0; i < RenderPasses.Length; i++)
            {
                if (activeInstance.ActiveMeshRenderCommands.HasRenderingCommands(RenderPasses[i]))
                    return true;
            }

            return false;
        }

        protected override void Execute()
        {
            // Scene captures (light probes, reflection probes) don't need motion vectors.
            if (RuntimeEngine.Rendering.State.IsSceneCapturePass)
                return;

            XRMaterial? material = ParentPipeline switch
            {
                DefaultRenderPipeline pipeline => pipeline.GetMotionVectorsMaterial(),
                DefaultRenderPipeline2 pipeline => pipeline.GetMotionVectorsMaterial(),
                _ => null,
            };

            if (material is null)
            {
                Debug.Rendering("[Velocity] Motion vectors pass skipped: parent pipeline missing, wrong type, or material unavailable.");
                return;
            }

            if (RenderPasses.Length == 0)
            {
                Debug.Rendering("[Velocity] Motion vectors pass skipped: no render passes configured.");
                return;
            }

            var rs = ActivePipelineInstance.RenderState;
            string? targetName = rs.CurrentRenderTargetBinding?.Name;
            int passIndex = string.IsNullOrWhiteSpace(targetName)
                ? int.MinValue
                : ResolvePassIndex($"RenderMotionVectors_{targetName}");

            //Debug.Out($"[Velocity] Motion vectors begin. GPU={GPUDispatch} PassCount={RenderPasses.Count}");
            using var renderGraphPassScope = passIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;
            using var overrideTicket = rs.PushOverrideMaterial(material);
            // Rasterize the motion-vector pass against the same unjittered projection
            // used for the reprojection uniforms so coverage and depth testing line up.
            using var unjitteredProjectionTicket = rs.PushUnjitteredProjection();
            // Request shader pipeline mode when enabled; combined mode builds an override-specific program.
            using var pipelineTicket = rs.PushForceShaderPipelines();
            // Motion vectors require the engine-generated mesh vertex varyings (notably FragPosLocal).
            // Some custom material vertex shaders do not emit those varyings, which leaves the velocity pass blank.
            using var generatedVertexTicket = rs.PushForceGeneratedVertexProgram();

            var commands = ActivePipelineInstance.ActiveMeshRenderCommands;
            if (commands is null)
            {
                Debug.Rendering("[Velocity] Motion vectors pass skipped: no mesh render commands available.");
                return;
            }

            // Resolve once per execution so the motion-vectors pass uses the same culling/draw
            // strategy as the lit pass on the same gpuPass instance. Otherwise RenderGPU(pass)
            // defaults to GpuIndirectInstrumented and thrashes gpuPass.MeshSubmissionStrategy
            // mid-frame, producing mismatched cull results between motion vectors and shading.
            var motionStrategy = _gpuDispatch
                ? RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy(true)
                : EMeshSubmissionStrategy.CpuDirect;
            foreach (int pass in RenderPasses)
            {
                //Debug.Out($"[Velocity] Rendering motion vectors for pass {pass} (GPUDispatch={GPUDispatch}).");
                if (_gpuDispatch)
                    commands.RenderGPU(pass, motionStrategy);
                else
                    commands.RenderCPU(pass);
            }

            //Debug.Out("[Velocity] Motion vectors end.");
        }

        private int ResolvePassIndex(string passName)
        {
            var metadata = ParentPipeline?.PassMetadata;
            if (metadata is null)
                return int.MinValue;

            foreach (RenderPassMetadata pass in metadata)
            {
                if (string.Equals(pass.Name, passName, StringComparison.OrdinalIgnoreCase))
                    return pass.PassIndex;
            }

            return int.MinValue;
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);

            if (RuntimeEngine.Rendering.State.IsSceneCapturePass || RenderPasses.Length == 0)
                return;

            string? targetName = context.CurrentRenderTarget?.Name;
            if (string.IsNullOrWhiteSpace(targetName))
                return;

            var builder = context.GetOrCreateSyntheticPass($"RenderMotionVectors_{targetName}", ERenderGraphPassStage.Graphics);
            string colorResource = string.Equals(targetName, DefaultRenderPipeline.VelocityFBOName, StringComparison.Ordinal)
                ? MakeTextureResource(DefaultRenderPipeline.VelocityTextureName)
                : MakeFboColorResource(targetName);
            string depthResource = string.Equals(targetName, DefaultRenderPipeline.VelocityFBOName, StringComparison.Ordinal)
                ? MakeTextureResource(DefaultRenderPipeline.DepthStencilTextureName)
                : MakeFboDepthResource(targetName);

            builder
                .UseEngineDescriptors()
                .UseMaterialDescriptors()
                .UseColorAttachment(
                    colorResource,
                    context.CurrentRenderTarget!.ColorAccess,
                    context.CurrentRenderTarget.ConsumeColorLoadOp(),
                    context.CurrentRenderTarget.GetColorStoreOp())
                .UseDepthAttachment(
                    depthResource,
                    ERenderGraphAccess.Read,
                    context.CurrentRenderTarget.ConsumeDepthLoadOp(),
                    context.CurrentRenderTarget.GetDepthStoreOp());
        }
    }
}
