using System;
using System.Collections.Generic;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

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
        /// <summary>Restricts this replay to late materials that explicitly opt into temporal motion.</summary>
        public bool RequireAdvancedLateMotionParticipation { get; set; }
        /// <summary>Optional output material, used by the Advanced reactive-mask replay.</summary>
        public Func<XRMaterial?>? OverrideMaterialResolver { get; set; }
        /// <summary>Uses the generated current/previous-transform velocity fragment variant.</summary>
        public bool UseMotionVectorMaterialVariant { get; set; } = true;
        public EAdvancedLateTemporalOutput AdvancedLateTemporalOutput { get; set; }

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
                if (activeInstance.ActiveMeshRenderCommands.HasRenderingMeshCommands(RenderPasses[i]))
                    return true;
            }

            return false;
        }

        protected override void Execute()
        {
            // Scene captures (light probes, reflection probes) don't need motion vectors.
            if (RuntimeEngine.Rendering.State.IsSceneCapturePass)
                return;

            XRMaterial? material = AdvancedLateTemporalOutput != EAdvancedLateTemporalOutput.None
                ? null
                : OverrideMaterialResolver?.Invoke()
                ?? (ParentPipeline as IRenderPipelinePassMaterialProvider)?.GetMotionVectorsMaterial();
            if (material is null && AdvancedLateTemporalOutput == EAdvancedLateTemporalOutput.None)
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
            if (AdvancedLateTemporalOutput != EAdvancedLateTemporalOutput.None)
            {
                string requiredTarget = AdvancedLateTemporalOutput == EAdvancedLateTemporalOutput.Velocity
                    ? AdvancedRenderPipeline.VelocityFBOName
                    : AdvancedRenderPipeline.TransparentMotionReactiveMaskFBOName;
                if (!string.Equals(targetName, requiredTarget, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Advanced late temporal replay requires bound target '{requiredTarget}', but '{targetName ?? "<none>"}' is active.");
            }
            int passIndex = string.IsNullOrWhiteSpace(targetName)
                ? int.MinValue
                : ResolvePassIndex($"RenderMotionVectors_{targetName}");

            //Debug.Out($"[Velocity] Motion vectors begin. GPU={GPUDispatch} PassCount={RenderPasses.Count}");
            using var renderGraphPassScope = passIndex != int.MinValue
                ? RuntimeEngine.Rendering.State.PushRenderGraphPassIndex(passIndex)
                : default;
            using IDisposable? overrideTicket = material is null ? null : rs.PushOverrideMaterial(material);
            using IDisposable? lateTemporalTicket = AdvancedLateTemporalOutput == EAdvancedLateTemporalOutput.None
                ? null : rs.PushAdvancedLateTemporalOutput(AdvancedLateTemporalOutput);
            using IDisposable? materialVariantTicket = UseMotionVectorMaterialVariant
                ? rs.PushUseMotionVectorMaterialVariant()
                : null;
            // Keep raster coverage on the active jittered projection so it exactly
            // matches the depth attachment produced earlier in this frame. The
            // fragment shader still encodes motion from the unjittered current and
            // previous matrices, so jitter is accounted for only by the temporal
            // resolve and never baked into the velocity field.
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
            if (RequireAdvancedLateMotionParticipation && _gpuDispatch)
                throw new InvalidOperationException("Participating transparent motion requires CPU-direct filtering; GPU indirect replay cannot preserve authored late-pass admission.");
            // The Advanced late lane explicitly requests filtered direct replay,
            // just like its color pass. A global opaque GPU override must not
            // replace that declared lane with an unfilterable indirect replay.
            var motionStrategy = RequireAdvancedLateMotionParticipation
                ? EMeshSubmissionStrategy.CpuDirect
                : RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy(_gpuDispatch);

            foreach (int pass in RenderPasses)
            {
                //Debug.Out($"[Velocity] Rendering motion vectors for pass {pass} (GPUDispatch={GPUDispatch}).");
                if (motionStrategy != EMeshSubmissionStrategy.CpuDirect)
                    commands.RenderGPU(pass, motionStrategy);
                else
                    // This auxiliary pass replays the primary visibility decision without
                    // scheduling another set of queries. Non-testable passes remain
                    // unconditional, while opaque passes peek the coordinator's current
                    // result for the same command and camera ownership.
                    commands.RenderCPUFiltered(
                        pass,
                        RequireAdvancedLateMotionParticipation
                            ? static command => IsAdvancedLateMotionReplayEligible(command)
                            : static command => command is IRenderCommandMesh,
                        respectCpuQueryOcclusion: true);
            }

            //Debug.Out("[Velocity] Motion vectors end.");
        }

        private static bool IsAdvancedLateMotionReplayEligible(RenderCommand command)
        {
            if (command is not IRenderCommandMesh mesh)
                return false;

            XRMaterial? material = mesh.MaterialOverride ?? mesh.Mesh?.Material;
            AdvancedLatePassMetadata? metadata = material?.AdvancedLatePassMetadata;
            if (metadata?.ParticipatesInMotionVectors != true)
                return false;
            string? rejectionReason;
            bool sourceValid = material!.TryValidateAdvancedLateTemporalSource(out rejectionReason);
            if (sourceValid && metadata.RequiresRigidTemporalGeometry &&
                (mesh.Mesh?.Mesh is not { HasSkinning: false, BlendshapeCount: 0 } ||
                 mesh.Instances != 1 || material.BillboardMode != EMeshBillboardMode.None ||
                 mesh.RenderOptionsOverride is not null))
            {
                sourceValid = false;
                rejectionReason = "The built-in colored-alpha temporal pair requires one rigid, non-billboard mesh without skinning, blendshapes or draw-state overrides.";
            }
            if (!sourceValid)
            {
                if (Debug.ShouldLogEvery("AdvancedLatePass.TemporalRejected", TimeSpan.FromSeconds(2)))
                    Debug.RenderingWarning("[AdvancedLatePass] Temporal replay rejected for material '{0}': {1}", material.Name ?? "<unnamed>", rejectionReason ?? "Unknown temporal eligibility failure.");
                return false;
            }
            // The generic override cannot retain arbitrary alpha/discard or
            // tessellation/displacement behavior. Until a material supplies a
            // coverage-preserving temporal variant, its metadata remains an
            // explicit editor-visible blocker rather than a silent bad replay.
            return metadata is
            {
                ParticipatesInMotionVectors: true,
                UnsupportedReason: null,
                TemporalUnsupportedReason: null,
                TemporalVelocityMaterial: not null,
                TemporalReactiveMaskMaterial: not null,
            };
        }

        private int ResolvePassIndex(string passName)
            => ParentPipeline?.TryGetRenderPassIndex(passName, out int passIndex) == true
                ? passIndex
                : int.MinValue;

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
